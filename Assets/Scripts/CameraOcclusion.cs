using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 俯视角城镇的遮挡处理（挂在 Main Camera 上，在 CameraRig 之后执行）：
///
/// ①【进屋隐藏屋顶】玩家进入某栋建筑的外墙范围 → 隐藏该栋的屋顶类节点（`屋顶*` / `女儿墙*`）的**渲染**，
///    走出即恢复。判定用"外墙部件的世界包围盒 + 缓冲距离"，纯几何、零碰撞体开销、不碰场景结构。
///    ⚠️ **只关 MeshRenderer.enabled，不 SetActive(false)**：屋顶节点上带 MeshCollider，
///       用 SetActive 会连碰撞一起消掉（子弹/视线/魔法投射物穿屋顶）—— 关渲染才能"看得见的消失、挡得住的还在"。
///
/// ②【遮挡圆形透视】把 `Obstacle` 层物体的材质换成「Town/SeeThrough」着色器：
///    屏幕上以玩家为圆心、半径可调的圆内，像素按 4x4 Bayer 抖动被丢弃（网点半透明），圆外完全正常。
///    · 圆心/半径/柔化/保留密度都是**全局量**（每帧写一次），几百个物体共享，无逐物体更新；
///    · 从原材质复制 颜色/平滑度/金属度 → **圆外外观与替换前一致**，且与既有
///      MaterialPropertyBlock 玩法（Health 受击闪白、TimedEffect 淡出）兼容（透视材质保留 `_Color`）；
///    · 每个"原材质"只克隆一份透视材质（多物体共享）→ 不破坏合批、不产生逐物体材质实例。
///
/// ⚠️ 约定与边界（与项目既有纪律一致）：
///   · 只在运行时改 `renderer.sharedMaterial`，**不动任何材质资产**；组件禁用/销毁时**还原材质并销毁克隆**（不留材质实例）；
///   · 层索引走 `Layers`、相机引用走 `CameraCache`（项目收敛点）；
///   · 无射线、无碰撞体、**不新增任何 static 状态**（不涉及 StaticReset）；
///   · 玩家在室内时自动关闭透视（屋顶已隐藏，室内再挖洞只会把身边的墙挖花）—— 可开关；
///   · 地面（Ground）、玩家、敌人、子弹天然不参与（只按层筛选）。
///   · 本 Shader 不影响阴影投射：被透视的物体投影仍是完整的（已知限制，见 Shader 注释）。
/// </summary>
[DefaultExecutionOrder(100)]   // 排在 CameraRig(默认 0) 之后：读到的是本帧最终的相机位置
public class CameraOcclusion : MonoBehaviour
{
    [Header("① 进屋隐藏屋顶")]
    [Tooltip("总开关。关闭时：屋顶不会被隐藏（也不会有任何状态被改写）。")]
    public bool hideRoofs = true;
    [Tooltip("建筑根节点的名字（场景里装 10 栋建筑的那个父物体）。")]
    public string buildingsRootName = "建筑";
    [Tooltip("外墙范围外扩多少米仍算「在屋内」（防止站在门口时屋顶反复闪）。")]
    public float roofBuffer = 0.3f;

    [Header("② 遮挡圆形透视")]
    [Tooltip("总开关。关闭时：不会替换任何材质。")]
    public bool seeThrough = true;
    [Tooltip("透视圆半径 = **屏幕高度的百分比**（0.10 = 屏高 10%，1080p 下约 108 像素）。用比例而不是像素：换分辨率/窗口大小都不用重调。")]
    [Range(0.02f, 0.5f)] public float radiusScreenRatio = 0.10f;
    [Tooltip("圆边缘柔化宽度（同样是屏高比例）：越大过渡越软；0 = 硬边。")]
    [Range(0f, 0.3f)] public float softnessScreenRatio = 0.04f;
    [Tooltip("圆内保留密度：0 = 完全挖空（直接透视）；0.35 ≈ 三分之一像素留下（推荐，还能看出「这里有东西」）；1 = 完全不挖。")]
    [Range(0f, 1f)] public float keepDensity = 0.35f;
    [Tooltip("参与透视的层。留空（0）时自动取 Obstacle 层 —— 建筑/围墙/树/路灯/车/道具/家具都在这一层；地面、玩家、敌人、子弹天然排除。")]
    public LayerMask occluderLayers;
    [Tooltip("透视圆心在玩家身上的高度（米）：0 = 脚底，1.0 = 胸口。")]
    public float centerHeight = 1.0f;
    [Tooltip("站在屋内时自动关闭透视（避免把身边的墙和家具挖花）。")]
    public bool disableSeeThroughIndoors = true;

    // ---------------- 运行时数据 ----------------
    Camera _cam;
    Transform _player;

    /// <summary>屋顶渲染器条目：记录组件 + 它在场景里原本的开关状态（恢复时按原状还原，不假设"原本一定是开的"）。</summary>
    class RoofRenderer
    {
        public Renderer renderer;
        public bool enabledAtStart;
    }

    class Building
    {
        public Vector4 rect;                              // 外墙范围 (minX, maxX, minZ, maxZ)
        public readonly List<RoofRenderer> roofs = new List<RoofRenderer>();
        public bool hidden;                               // 屋顶当前是否已被隐藏
        public bool inside;                               // 玩家当前是否在其中
    }
    readonly List<Building> _buildings = new List<Building>();

    readonly Dictionary<Material, Material> _seeMatOf = new Dictionary<Material, Material>();
    readonly List<Renderer> _swapped = new List<Renderer>();
    readonly List<Material> _originals = new List<Material>();
    bool _materialsApplied;

    // Shader 的全局属性名（与 城镇透视.shader 里声明的一致）
    const string CenterProp = "_SeeCenter";
    const string KeepProp = "_SeeKeep";
    static readonly Vector4 NoCircle = new Vector4(-99999f, -99999f, 0f, 0.001f);

    void Awake()
    {
        _cam = GetComponent<Camera>();
        if (_cam == null) _cam = CameraCache.Main;

        var pc = PlayerController.Instance;
        if (pc == null) pc = FindObjectOfType<PlayerController>();
        _player = pc != null ? pc.transform : null;
        if (_player == null)
            Debug.LogWarning("[CameraOcclusion] 场景里没有 PlayerController → 室内判定与透视圆心都不会生效。", this);

        if (occluderLayers.value == 0)
        {
            if (Layers.Obstacle >= 0) occluderLayers = 1 << Layers.Obstacle;
            else Debug.LogWarning("[CameraOcclusion] 工程里没有定义 Obstacle 层，请在 Inspector 里手动指定「参与透视的层」。", this);
        }

        CollectBuildings();
        ApplySeeThrough();

        // 初始就把圆关掉：第一帧 LateUpdate 之前不应有残留圆
        Shader.SetGlobalVector(CenterProp, NoCircle);
        Shader.SetGlobalFloat(KeepProp, 1f);
    }

    /// <summary>扫描建筑：记录每栋的「屋顶渲染器（含原始开关状态）」与「外墙包围盒」。</summary>
    void CollectBuildings()
    {
        _buildings.Clear();
        var root = GameObject.Find(buildingsRootName);
        if (root == null)
        {
            Debug.LogWarning("[CameraOcclusion] 找不到名为「" + buildingsRootName + "」的建筑根节点 → 屋顶隐藏不会生效。", this);
            return;
        }

        foreach (Transform bld in root.transform)
        {
            var b = new Building();

            // 屋顶类节点：屋顶_南坡 / 屋顶_北坡 / 屋顶_脊瓦 / 屋顶（民居B）/ 屋顶板 + 女儿墙_*（商铺）
            foreach (Transform c in bld)
            {
                if (!c.name.StartsWith("屋顶") && !c.name.StartsWith("女儿墙")) continue;
                var rs = c.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++)
                {
                    var item = new RoofRenderer();
                    item.renderer = rs[i];
                    item.enabledAtStart = rs[i].enabled;
                    b.roofs.Add(item);
                }
            }

            // 外墙范围（只统计 外墙_ / 山墙_ 部件 → 不含月台、雨棚、门廊、铺装等附属物）
            bool has = false;
            Bounds bb = new Bounds();
            foreach (Transform c in bld)
            {
                if (!c.name.StartsWith("外墙_") && !c.name.StartsWith("山墙_")) continue;
                var rs = c.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++)
                {
                    if (!has) { bb = rs[i].bounds; has = true; }
                    else bb.Encapsulate(rs[i].bounds);
                }
            }
            if (!has || b.roofs.Count == 0) continue;   // 不是建筑（或没有屋顶）→ 跳过

            b.rect = new Vector4(bb.min.x, bb.max.x, bb.min.z, bb.max.z);
            _buildings.Add(b);
        }
    }

    /// <summary>把参与层上的所有渲染器换成透视材质（按"原材质"克隆，多物体共享；不产生逐物体实例）。</summary>
    void ApplySeeThrough()
    {
        _materialsApplied = false;
        if (!seeThrough) return;

        var sh = Shader.Find("Town/SeeThrough");
        if (sh == null)
        {
            Debug.LogWarning("[CameraOcclusion] 找不到着色器 Town/SeeThrough（是不是 城镇透视.shader 编译失败了？）→ 透视不会生效。", this);
            return;
        }

        // ⚠️ 必须带 true：隐藏中的障碍物也要换，否则它被激活时不会有透视效果
        var all = FindObjectsOfType<Renderer>(true);
        for (int i = 0; i < all.Length; i++)
        {
            var r = all[i];
            if (r == null || r.sharedMaterial == null) continue;
            if (((1 << r.gameObject.layer) & occluderLayers.value) == 0) continue;

            var src = r.sharedMaterial;
            Material dst;
            if (!_seeMatOf.TryGetValue(src, out dst))
            {
                dst = new Material(sh);
                dst.name = src.name + "_透视";
                // 复制 Standard 的可见属性 → 圆外外观与替换前一致
                if (src.HasProperty("_Color")) dst.SetColor("_Color", src.GetColor("_Color"));
                if (src.HasProperty("_Glossiness")) dst.SetFloat("_Glossiness", src.GetFloat("_Glossiness"));
                if (src.HasProperty("_Metallic")) dst.SetFloat("_Metallic", src.GetFloat("_Metallic"));
                _seeMatOf[src] = dst;
            }

            _swapped.Add(r);
            _originals.Add(src);
            r.sharedMaterial = dst;
        }

        _materialsApplied = _swapped.Count > 0;
    }

    void LateUpdate()
    {
        UpdateInsideFlags();
        if (hideRoofs) UpdateRoofVisibility();
        UpdateSeeThrough();
    }

    /// <summary>玩家是否在某栋建筑的外墙范围内（带缓冲）。</summary>
    void UpdateInsideFlags()
    {
        if (_player == null) return;
        Vector3 p = _player.position;
        for (int i = 0; i < _buildings.Count; i++)
        {
            var b = _buildings[i];
            b.inside = p.x >= b.rect.x - roofBuffer && p.x <= b.rect.y + roofBuffer
                    && p.z >= b.rect.z - roofBuffer && p.z <= b.rect.w + roofBuffer;
        }
    }

    /// <summary>只在状态变化时写 Renderer.enabled（避免每帧无谓调用）。</summary>
    void UpdateRoofVisibility()
    {
        for (int i = 0; i < _buildings.Count; i++)
        {
            var b = _buildings[i];
            if (b.inside == b.hidden) continue;   // hidden = "屋顶当前是否已被藏起来"
            b.hidden = b.inside;
            for (int j = 0; j < b.roofs.Count; j++)
            {
                var item = b.roofs[j];
                if (item.renderer == null) continue;
                // 隐藏时关渲染；恢复时还原"场景原本的开关状态"（不假设它原本一定是开的）
                item.renderer.enabled = b.inside ? false : item.enabledAtStart;
            }
        }
    }

    /// <summary>每帧把透视圆心（玩家的屏幕投影）与参数写进全局量。</summary>
    void UpdateSeeThrough()
    {
        if (!_materialsApplied) return;

        bool indoors = false;
        if (disableSeeThroughIndoors)
        {
            for (int i = 0; i < _buildings.Count; i++)
                if (_buildings[i].inside) { indoors = true; break; }
        }

        if (_player == null || _cam == null || indoors || !seeThrough)
        {
            Shader.SetGlobalVector(CenterProp, NoCircle);   // 关掉圆（等价于普通材质）
            Shader.SetGlobalFloat(KeepProp, 1f);
            return;
        }

        Vector3 sp = _cam.WorldToScreenPoint(_player.position + Vector3.up * centerHeight);
        float radius = radiusScreenRatio * Screen.height;
        float soft = Mathf.Max(softnessScreenRatio * Screen.height, 0.001f);
        // 玩家在相机背后（sp.z <= 0）时把圆移出屏幕，避免出现"反方向"的假圆
        Shader.SetGlobalVector(CenterProp, sp.z > 0f ? new Vector4(sp.x, sp.y, radius, soft) : NoCircle);
        Shader.SetGlobalFloat(KeepProp, keepDensity);
    }

    /// <summary>还原材质与屋顶，并让全局圆失效（组件禁用/销毁时调用）。</summary>
    void OnDisable()
    {
        for (int i = 0; i < _swapped.Count; i++)
            if (_swapped[i] != null) _swapped[i].sharedMaterial = _originals[i];
        _swapped.Clear();
        _originals.Clear();
        _materialsApplied = false;

        for (int i = 0; i < _buildings.Count; i++)
        {
            var b = _buildings[i];
            b.inside = false;
            if (!b.hidden) continue;
            b.hidden = false;
            for (int j = 0; j < b.roofs.Count; j++)
            {
                var item = b.roofs[j];
                if (item.renderer != null) item.renderer.enabled = item.enabledAtStart;
            }
        }

        Shader.SetGlobalVector(CenterProp, NoCircle);
        Shader.SetGlobalFloat(KeepProp, 1f);
    }

    /// <summary>销毁运行时克隆出来的透视材质（避免每次 Play 泄漏材质实例）。</summary>
    void OnDestroy()
    {
        foreach (var kv in _seeMatOf) if (kv.Value != null) Destroy(kv.Value);
        _seeMatOf.Clear();
    }
}
