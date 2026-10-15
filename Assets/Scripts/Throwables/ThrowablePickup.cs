using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地上的**投掷物**（<see cref="ThrowableData"/> 的拾取物）—— 与 <see cref="WeaponPickup"/> /
/// <see cref="HealthPack"/> / <see cref="AmmoPickup"/> / <see cref="MagicPickup"/> 是同一族，走同一套交互流程。
///
/// 外观走**两条路**（"表现资源化"那条纪律的落点）：
///   · 卡上填了 `pickupModelPrefab`（继承自 <see cref="ItemData"/>）→ **直接用模型**
///   · 没填 → **图标 billboard**（SpriteRenderer 每帧与相机同朝向）+ 脚下半影阴影
///     —— 与弹药包 / 魔法卡完全同一套视觉；"先占位、后升级美术"**零代码改动**。
///
/// 交互：走 `InteractFocus`（单一交互焦点：谁离玩家最近谁显示气泡，按 **F** 拾取）。
/// 拾取：`AddItems` 进背包（投掷物**可堆叠** → 一格 N 颗）；**背包满 → 留在原地 + 提示**。
/// ⚠️ 静态注册表 `All` 已登记 `StaticReset`（重开一局清场）。
/// </summary>
public class ThrowablePickup : MonoBehaviour, IInteractable
{
    /// <summary>全场投掷物注册表（重开一局清场用；**必须**登记到 `StaticReset`）。</summary>
    public static readonly List<ThrowablePickup> All = new List<ThrowablePickup>();

    [Header("内容")]
    [Tooltip("这一颗是什么投掷物")]
    public ThrowableData data;
    [Tooltip("这一\"包\"有几颗（丢弃时整格掉 → N 颗一包；拾取时能收多少收多少）")]
    [Min(1)] public int count = 1;

    /// <summary>拾取距离（米）。与"交互焦点"用同一个值（与弹药包 / 魔法卡一致）。</summary>
    public const float PickupRange = 1.6f;

    /// <summary>上下浮动幅度（全局观感常量，不逐张配 —— 与弹药包 / 魔法卡同一取舍）。</summary>
    const float BobAmplitude = 0.014f;

    // 外观参数一律从卡上读（运行时生成的物体没法在 Inspector 里调；放卡上才能"改完对着画面看"）
    float IconSize => data != null ? data.worldIconSize : 0.30f;
    float HoverHeight => data != null ? data.worldHoverHeight : 0.16f;
    float ShadowSize => data != null ? data.worldShadowSize : 0.26f;
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
        InteractFocus.Unregister(this);
    }

    // ==================== 视觉构建 ====================

    /// <summary>建外观（幂等）。<see cref="Create"/> 在设好 `data` 之后会显式调一次
    /// （⚠️ `Awake` 早于赋 `data`，那时还是 null）。</summary>
    public void BuildVisual()
    {
        if (_built) return;
        _built = true;
        if (data == null) return;

        // ---- 路径 ①：卡上配了模型 → 用模型 ----
        if (data.pickupModelPrefab != null)
        {
            var model = Instantiate(data.pickupModelPrefab, transform);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero;
            Layers.SetRecursively(gameObject, Layers.Pickup);
            return;
        }

        // ---- 路径 ②：没模型 → 图标 billboard + 脚下半影（**共用实现**见 `PickupVisual`）----
        _sr = PickupVisual.CreateIcon(transform, data, IconSize);
        _shadowSr = PickupVisual.CreateShadow(transform, ShadowSize, ShadowAlpha);

        PlaceIcon(0f);
        Layers.SetRecursively(gameObject, Layers.Pickup);
    }

    void ApplyIconScale() => PickupVisual.FitScale(_sr, IconSize);

    void ApplyShadowScale() => PickupVisual.ApplyShadow(_shadowSr, ShadowSize, ShadowAlpha);

    void LateUpdate()
    {
        PickupVisual.Billboard(_sr);   // 朝相机（billboard）—— 共用实现

        _t += Time.deltaTime;
        PlaceIcon(Mathf.Sin(_t * 2.2f) * BobAmplitude);
    }

    /// <summary>把图标放到"离地高 + 浮动量"的位置（共用实现；浮动节奏由本类给）。</summary>
    void PlaceIcon(float bob) => PickupVisual.PlaceIcon(_sr, HoverHeight, bob);

    // 设层已收敛到 `Layers.SetRecursively`（2026-10-15）：本类原来那份 `SetLayers` 已删。

    // ==================== IInteractable ====================

    public Transform InteractAnchor => transform;
    public string InteractLabel => data == null ? "投掷物"
        : (count > 1 ? data.DisplayName + " ×" + count : data.DisplayName);
    public string InteractKey => "F";
    public float InteractRange => PickupRange;

    /// <summary>按键拾取（由交互焦点调用）：**能收多少收多少**（可堆叠 → 并进已有那格）；
    /// 背包满了则一颗都不收，留在原地并提示（与枪 / 弹药 / 血包 / 魔法卡语义一致）。</summary>
    public void Interact(PlayerController player)
    {
        if (player == null || data == null || count <= 0) return;

        var bp = player.backpack;
        int put = bp != null ? bp.AddItems(data, count) : 0;
        if (put <= 0)
        {
            HUD.AnnounceBackpackFull();
            return;   // 收不下 → 留在原地，等玩家腾出格子
        }

        count -= put;
        Sfx.Pickup();
        HUD.Announce("拾取  " + data.DisplayName + (put > 1 ? " ×" + put : ""), 1.0f, 26, data.pickupColor);
        if (count <= 0) Destroy(gameObject);
    }

    // ==================== 创建 ====================

    /// <summary>在指定位置创建一"包"投掷物（开局铺场 / 丢弃 / 以后箱子外溢都走它）。
    /// `count` = 这一包几颗（整格掉就是整包掉）。</summary>
    public static ThrowablePickup Create(ThrowableData def, Vector3 pos, int count = 1)
    {
        if (def == null) return null;

        var root = new GameObject("ThrowablePickup_" + def.DisplayName);
        root.transform.position = pos;

        var p = root.AddComponent<ThrowablePickup>();
        p.data = def;
        p.count = Mathf.Max(1, count);
        p.BuildVisual();   // ⚠️ 必须在设好 `data` 之后手动建（Awake 早于赋 data）
        return p;
    }
}
