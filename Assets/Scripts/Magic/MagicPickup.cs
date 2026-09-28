using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地上的**魔法卡**（<see cref="MagicData"/> 的拾取物）—— 与 <see cref="WeaponPickup"/> /
/// <see cref="HealthPack"/> / <see cref="AmmoPickup"/> 是同一族，走同一套交互流程。
///
/// 外观走**两条路**（"表现资源化"那条纪律的落点）：
///   · 卡上填了 `pickupModelPrefab`（继承自 <see cref="ItemData"/>）→ **直接用模型**
///     （模型自带光影，不再画 billboard 图标和假阴影）
///   · 没填 → **图标 billboard**（SpriteRenderer 每帧与相机同朝向）+ 脚下半影阴影
///     —— 与弹药包完全同一套视觉（用户 2026-09-28 定：先用图标占位，以后有美术直接填模型）
/// ⭐ 所以"先占位、后升级美术"**零代码改动**。
///
/// 交互：走 `InteractFocus`（单一交互焦点：谁离玩家最近谁显示气泡，按 **F** 拾取）。
/// 拾取：`AddItems` 进背包（魔法卡不可堆叠 → 一格一张）；**背包满 → 留在原地 + 提示**
///       （与枪 / 弹药 / 血包的语义完全一致）。
/// </summary>
public class MagicPickup : MonoBehaviour, IInteractable
{
    /// <summary>全场魔法卡注册表（重开一局清场用；**必须**登记到 `StaticReset`）。
    /// 另外也被"改卡上的外观参数 → 场上的卡立刻跟着变"用到（见 <see cref="RefreshLook"/>）。</summary>
    public static readonly List<MagicPickup> All = new List<MagicPickup>();

    [Header("内容")]
    [Tooltip("这一张是什么魔法")]
    public MagicData data;

    /// <summary>拾取距离（米）。与"交互焦点"用同一个值（与弹药包一致）。</summary>
    public const float PickupRange = 1.6f;

    /// <summary>上下浮动幅度（全局观感常量，不逐张配 —— 与弹药包同一取舍）。</summary>
    const float BobAmplitude = 0.014f;

    // 外观参数一律从卡上读（与弹药包同一套做法：**运行时生成**的物体没法在 Inspector 里调，
    // 放卡上才能"改完对着画面看效果"）。缺卡时用这里的兜底值。
    float IconSize => data != null ? data.worldIconSize : 0.34f;
    float HoverHeight => data != null ? data.worldHoverHeight : 0.20f;
    float ShadowSize => data != null ? data.worldShadowSize : 0.30f;
    float ShadowAlpha => data != null ? data.worldShadowAlpha : 0.30f;

    SpriteRenderer _sr;
    SpriteRenderer _shadowSr;
    bool _built;
    float _t;

    void Awake()
    {
        All.Add(this);
        InteractFocus.Register(this);
        if (data != null) BuildVisual();   // 场景里手摆的（不经过 Create）也能正常显示
    }

    void OnDestroy()
    {
        All.Remove(this);
        InteractFocus.Unregister(this);    // 注册必须配注销（否则场景重载后焦点表里留死引用）
    }

    // ==================== 视觉构建 ====================

    /// <summary>建外观（幂等）。<see cref="Create"/> 在设好 `data` 之后会显式调一次
    /// （⚠️ `Awake` 早于赋 `data`，那时还是 null）。</summary>
    public void BuildVisual()
    {
        if (_built) return;
        _built = true;
        if (data == null) return;

        // ---- 路径 ①：卡上配了模型 → 用模型，不再画图标与假阴影 ----
        if (data.pickupModelPrefab != null)
        {
            var model = Instantiate(data.pickupModelPrefab, transform);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero;
            SetLayers(Layers.Pickup);
            return;
        }

        // ---- 路径 ②：没模型 → 图标 billboard + 脚下半影阴影（与弹药包同一套）----
        var iconGo = new GameObject("Icon");
        iconGo.transform.SetParent(transform, false);
        _sr = iconGo.AddComponent<SpriteRenderer>();
        _sr.sprite = ItemVisuals.IconOf(data);
        _sr.color = ItemVisuals.ColorOf(data);
        _sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _sr.receiveShadows = false;

        // 图标尺寸：Sprite 的默认世界尺寸由导入 PPU 决定（64px 图 = 0.64m）→ 缩放到想要的大小
        ApplyIconScale();

        // 阴影：平躺的径向渐变圆片（边缘平滑 = 半影）。
        // ⚠️ 必须是**图标的兄弟节点**而不是子节点 —— 否则它会跟着 billboard 一起立起来。
        var shadowGo = new GameObject("Shadow");
        shadowGo.transform.SetParent(transform, false);
        _shadowSr = shadowGo.AddComponent<SpriteRenderer>();
        _shadowSr.sprite = ItemVisuals.ShadowSprite();
        _shadowSr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _shadowSr.receiveShadows = false;
        _shadowSr.sortingOrder = -1;                                        // 永远画在图标之下
        shadowGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // 平躺
        shadowGo.transform.localPosition = new Vector3(0f, 0.008f, 0f);     // 贴地（略抬，避免 z-fighting）
        ApplyShadowScale();

        PlaceIcon(0f);
        SetLayers(Layers.Pickup);   // 与其它拾取物同一层（子弹与视线掩码都不含它）
    }

    void ApplyIconScale()
    {
        if (_sr == null) return;
        float baseSize = _sr.sprite != null ? Mathf.Max(0.0001f, _sr.sprite.bounds.size.x) : 1f;
        float k = IconSize / baseSize;
        _sr.transform.localScale = new Vector3(k, k, 1f);
    }

    void ApplyShadowScale()
    {
        if (_shadowSr == null) return;
        float baseSize = _shadowSr.sprite != null ? Mathf.Max(0.0001f, _shadowSr.sprite.bounds.size.x) : 1f;
        float k = ShadowSize / baseSize;
        _shadowSr.transform.localScale = new Vector3(k, k, 1f);
        _shadowSr.color = new Color(0f, 0f, 0f, Mathf.Clamp01(ShadowAlpha));
    }

    void LateUpdate()
    {
        // 朝相机（billboard）：与相机旋转一致 = 永远正对屏幕
        var cam = CameraCache.Main;
        if (cam != null && _sr != null)
            _sr.transform.rotation = cam.transform.rotation;

        _t += Time.deltaTime;
        PlaceIcon(Mathf.Sin(_t * 2.2f) * BobAmplitude);
    }

    void PlaceIcon(float bob)
    {
        if (_sr != null) _sr.transform.localPosition = new Vector3(0f, HoverHeight + bob, 0f);
    }

    /// <summary>把卡上的外观参数应用到本实例（只改尺寸 / 位置 / 颜色，**不重建物体** →
    /// 编辑模式下也能安全调用）。没走 billboard 路径（配了模型）时什么都不做。</summary>
    public void ApplyLook()
    {
        if (_sr == null) return;
        // ⚠️ 图标与颜色在这里**重新取一次**：改了卡上的颜色后，`ItemVisuals.IconOf` 会返回新的图 ——
        //    不重取的话场上的卡还挂着旧图，改色看不见效果（与弹药包同一处理）。
        _sr.sprite = ItemVisuals.IconOf(data);
        _sr.color = ItemVisuals.ColorOf(data);
        ApplyIconScale();
        ApplyShadowScale();
        PlaceIcon(0f);
    }

    /// <summary>把"某张魔法卡"的外观改动同步到场上的所有实例（供 <see cref="MagicData"/> 的 OnValidate 调用）
    /// —— 编辑器里改完数字**立刻能看见效果**，不用重新进 Play。</summary>
    public static void RefreshLook(MagicData def)
    {
        if (def == null) return;
        for (int i = 0; i < All.Count; i++)
        {
            var p = All[i];
            if (p != null && p.data == def) p.ApplyLook();
        }
    }

    void SetLayers(int layer)
    {
        if (layer < 0) return;
        foreach (var t in GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
    }

    // ==================== IInteractable ====================

    public Transform InteractAnchor => transform;
    public string InteractLabel => data != null ? data.DisplayName : "魔法";
    public string InteractKey => "F";
    public float InteractRange => PickupRange;

    /// <summary>按键拾取（由交互焦点调用）。
    /// 魔法卡**不可堆叠** → 一次收 1 张；背包满则整张留在原地并提示。</summary>
    public void Interact(PlayerController player)
    {
        if (player == null || data == null) return;

        var bp = player.backpack;
        int put = bp != null ? bp.AddItems(data, 1) : 0;
        if (put <= 0)
        {
            HUD.AnnounceBackpackFull();
            return;   // 收不下 → 留在原地，等玩家腾出格子
        }

        Sfx.Pickup();
        HUD.Announce("拾取  " + data.DisplayName, 1.0f, 26, data.pickupColor);
        Destroy(gameObject);
    }

    // ==================== 创建 ====================

    /// <summary>在指定位置创建一张魔法卡（丢弃 / 箱子外溢 / 开局铺货都走它）。</summary>
    public static MagicPickup Create(MagicData def, Vector3 pos)
    {
        if (def == null) return null;

        var root = new GameObject("MagicPickup_" + def.DisplayName);
        root.transform.position = pos;

        var p = root.AddComponent<MagicPickup>();
        p.data = def;
        p.BuildVisual();   // ⚠️ 必须在设好 `data` 之后手动建（Awake 早于赋 data）
        return p;
    }
}
