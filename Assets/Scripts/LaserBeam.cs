using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 激光瞄准器的**激光束**：从配件的出头点射出一条红线，打到墙/箱子为止，并在落点画一个光点。
///
/// 怎么用（零代码）：把本组件挂在**配件 prefab** 上 —— 推荐挂配件根节点，也可以直接挂在 `Emit_Laser` 上。
/// 之后不管配件是"运行时实例化到挂点上"还是"预挂在枪模型里用 SetActive 开关"，
/// **只要配件出现就有激光，配件没了激光也跟着没**（组件生命周期 = 配件生命周期）。
///
/// 关键约定（照项目里狙击手那条红色预警线改的，视觉与判定口径保持一致）：
///   · 起点 = `Emit_Laser` 的世界坐标（找不到就用本物体位置）
///   · 方向 = **弹道方向**（`PlayerController.BulletDir`；基准点是"枪轴上离玩家最近的点"，见 `AimBasis`），
///     与子弹走同一条线，所以"激光指哪 = 子弹打哪"。⚠️ **不要用玩家 `forward`**：枪口在身体右侧，
///     沿 forward 的激光会与弹道恒有一个夹角（表现为"激光指这边、子弹打那边"）。
///     更不能用 `Emit_Laser.forward`：它在带 180° 校正壳的枪模内部，+Z 很可能指向枪尾。
///     （准星贴到玩家身边时 `BulletDir` 会被死区冻结 → 激光也跟着锁住，这是设计行为。）
///   · 遮挡 = `Layers.Blocker`（墙 / 箱子 / 地面）。**刻意不含玩家与敌人**：激光穿过人和同伴，
///     只有掩体能让它停下（与狙击预警线同一套掩码；换成子弹掩码会被自己挡住、疯狂闪）
///   · ⚠️ 激光是**场景里的 3D 物体**，`UIState` 只管 UI —— 暂停 / 开面板 / 死亡必须在这里显式关掉
///   · 材质走 `Effects.SharedAlpha`（按颜色全局缓存的共享材质）→ **不要 Destroy、也不要每帧 new**
/// </summary>
[DisallowMultipleComponent]
public class LaserBeam : MonoBehaviour
{
    [Header("起点")]
    [Tooltip("激光出头的空物体名（会在自身与子物体里找）。找不到就用本物体的位置。\n如果你把本组件直接挂在出头的空物体上，这里留空即可")]
    public string emitPointName = "Emit_Laser";

    [Header("外观")]
    [Tooltip("激光颜色。可见激光指示器通常是红色")]
    public Color color = new Color(1f, 0.15f, 0.1f, 0.85f);

    [Tooltip("激光束直径（米，世界单位）。细一点才像光束")]
    [Min(0.001f)] public float width = 0.012f;

    [Tooltip("落点光点直径（米）。0 = 不画光点")]
    [Min(0f)] public float dotSize = 0.05f;

    [Tooltip("没打到任何东西时激光延伸多远（米）")]
    [Min(1f)] public float maxDistance = 60f;

    [Header("显隐")]
    [Tooltip("勾上 = 只在按住右键瞄准时显示；不勾 = 一直亮")]
    public bool onlyWhenAiming = false;

    Transform _emit;
    LineRenderer _line;
    Transform _dot;
    Color _builtColor;
    bool _ready;

    void OnEnable()
    {
        // ⚠️ **只在"枪在玩家手上"时才工作**：同一份模型 prefab 也被用作地上的拾取物
        //    （`WeaponData.pickupModelPrefab` 与 `heldModelPrefab` 常常是同一个）——
        //    地上那把若也带激光，每把散落的枪都会射出一条**跟着准星扫动**的红线（还每帧多一次射线检测）。
        //    判据 = 本组件是否挂在玩家的 `gunVisual` 子树下；不是就把整个组件停用（连 Update 都不跑）。
        if (!IsHeldByPlayer()) { enabled = false; return; }

        // ⚠️ 初始化放在 OnEnable（不是 Awake）：这样"预挂配件 + 用 SetActive 开关显示"的做法也能正常工作
        Build();
    }

    /// <summary>本配件是否挂在"玩家手里的枪"上（地上的拾取物不满足）。
    /// 拾取时会销毁地面实例、再由 `UpdateGunVisual` 重新 Instantiate 手上的模型 → 新实例会重新走 OnEnable。</summary>
    bool IsHeldByPlayer()
    {
        var pc = PlayerController.Instance;
        return pc != null && pc.gunVisual != null && transform.IsChildOf(pc.gunVisual);
    }

    void OnDisable()
    {
        Hide();
    }

    void Update()
    {
        if (!_ready || _line == null) return;

        // ⚠️ 暂停 / 死亡时必须关：激光是 3D 物体，`UIState` 隐藏 HUD 管不到它（否则开背包时激光还挂在世界里）
        if (GameState.Paused || GameState.GameOver) { Hide(); return; }

        var pc = PlayerController.Instance;
        if (onlyWhenAiming && (pc == null || pc.AdsProgress01 <= 0.01f)) { Hide(); return; }

        // 运行中改参数即时生效（调参方便，开销可忽略）
        _line.startWidth = _line.endWidth = width;
        if (color != _builtColor) ApplyColor(color);

        Vector3 origin = _emit != null ? _emit.position : transform.position;
        // 方向 = **与子弹同一条线**（枪口 → 准星点）：读 PlayerController.BulletDir，与 Fire() 同源。
        // ⚠️ 不要用玩家 forward —— 枪口在身体右侧，沿 forward 的激光会与弹道恒有一个夹角。
        Vector3 dir = pc != null ? pc.BulletDir : transform.root.forward;

        bool blocked = Physics.Raycast(origin, dir, out RaycastHit hit, maxDistance,
                                       Layers.Blocker, QueryTriggerInteraction.Ignore);

        _line.enabled = true;
        _line.SetPosition(0, origin);
        _line.SetPosition(1, blocked ? hit.point : origin + dir * maxDistance);

        if (_dot != null)
        {
            _dot.gameObject.SetActive(blocked);
            // 往"来光方向"缩一点，免得球体一半埋进墙里
            if (blocked) _dot.position = hit.point - dir * (dotSize * 0.25f);
        }
    }

    // ==================== 构建 / 清理 ====================

    void Build()
    {
        if (_ready) return;
        _emit = FindEmitPoint();

        var mat = Effects.SharedAlpha(color);
        _builtColor = color;

        // 光束（两个点的直线）
        var lineGo = new GameObject("~LaserBeam");
        lineGo.transform.SetParent(transform, false);
        SetFxLayer(lineGo);
        _line = lineGo.AddComponent<LineRenderer>();
        _line.positionCount = 2;
        _line.useWorldSpace = true;      // 传的是世界坐标（宽度因此不受挂载链缩放影响）
        _line.startWidth = _line.endWidth = width;
        _line.sharedMaterial = mat;
        _line.shadowCastingMode = ShadowCastingMode.Off;
        _line.receiveShadows = false;
        _line.enabled = false;

        // 落点光点：共享网格 + 无碰撞体（走 Effects，能吃到 GameAssets 里的替换）
        if (dotSize > 0f)
        {
            var dotGo = Effects.CreateMeshObject(PrimitiveType.Sphere, "~LaserDot", transform, mat);
            SetFxLayer(dotGo);
            _dot = dotGo.transform;
            SetDotScale(dotSize);
            dotGo.SetActive(false);
        }

        _ready = true;
    }

    /// <summary>光点缩放：内置球体直径是 1 → 想要"直径 = size 米"就得除以挂载链上的缩放。
    /// （枪模型的校正壳常带 2 倍缩放，不抵消的话光点会跟着忽大忽小。）</summary>
    void SetDotScale(float size)
    {
        if (_dot == null) return;
        float s = Mathf.Max(0.0001f, transform.lossyScale.x);
        _dot.localScale = Vector3.one * (size / s);
    }

    void ApplyColor(Color c)
    {
        _builtColor = c;
        var mat = Effects.SharedAlpha(c);
        if (_line != null) _line.sharedMaterial = mat;
        if (_dot != null)
        {
            var r = _dot.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = mat;
        }
    }

    void Hide()
    {
        if (_line != null) _line.enabled = false;
        if (_dot != null && _dot.gameObject.activeSelf) _dot.gameObject.SetActive(false);
    }

    /// <summary>出光线与光点统一放特效层（纯显示、无碰撞体，不参与任何射线）。</summary>
    static void SetFxLayer(GameObject go)
    {
        if (go != null && Layers.FX >= 0) go.layer = Layers.FX;
    }

    // ==================== 找出头点 ====================

    /// <summary>名字就是出头点 → 用自己；否则在子树里找；都找不到就用本物体（至少能出一条线，不会静默失效）。</summary>
    Transform FindEmitPoint()
    {
        if (!string.IsNullOrEmpty(emitPointName))
        {
            if (transform.name == emitPointName) return transform;
            var found = FindDeep(transform, emitPointName);
            if (found != null) return found;
        }
        return transform;
    }

    static Transform FindDeep(Transform t, string n)
    {
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            if (c.name == n) return c;
            var r = FindDeep(c, n);
            if (r != null) return r;
        }
        return null;
    }
}
