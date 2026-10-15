using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地上的**弹药包**：不做 3D 模型，直接显示图标（用户 2026-09-27 定）。
///
/// 渲染方式（用户选"朝相机"，并要求"离地 + 阴影"来体现实体感）：
///   · 图标 = `SpriteRenderer`，`LateUpdate` 里**与相机同朝向** → 任何俯视角都看到完整形状；
///   · 图标抬高 `hoverHeight`（离地一点点，显得浮着）；
///   · 脚下贴一块压扁的暗片当**阴影**（只跟 x/z、不随图标翻转）→ 视觉上"站在地上"。
///   ⚠️ 所以本物体是"不动的父级"，图标（Icon）与阴影（Shadow）是两个**各自负责自己变换**的子物体 ——
///      别把阴影做成图标的子物体，否则它会跟着 billboard 立起来。
///
/// 交互：走 `InteractFocus`（与武器 / 药箱同一套：谁离得最近谁显示气泡、按 **F** 拾取）。
/// 拾取：`AddItems(def, packSize)` —— 弹药**可堆叠**，会自动并进同类格；
///       背包满 → 弹药包**留在原地** + 提示（与枪 / 药箱的语义一致）。
/// </summary>
public class AmmoPickup : MonoBehaviour, IInteractable
{
    /// <summary>全场弹药包注册表（重开一局清场用；**必须**登记到 `StaticReset`）。</summary>
    public static readonly List<AmmoPickup> All = new List<AmmoPickup>();

    [Header("内容")]
    [Tooltip("这一包是什么弹药")]
    public AmmoData data;
    [Tooltip("这一包几发（一般 = 弹药卡上的 packSize，可在场景里单独调）")]
    [Min(1)] public int packSize = 1;

    // ⚠️ 外观三件套（尺寸 / 离地 / 阴影）**不在这里配**，而是放在弹药卡（`AmmoData`）上 ——
    //    因为弹药包是**运行时动态生成**的：组件字段在 Inspector 里根本没有实例可调（等于没法调）。
    //    放卡上还有两个好处：① 每种弹药可以不一样（粗壳 / 细长弹各调各的）；
    //    ② 编辑器里改完会**立刻同步**到场上的包（`AmmoData.OnValidate` → `RefreshLook`），方便对着画面调。
    //    上下浮动属于全局观感，用常量即可。
    const float BobAmplitude = 0.014f;

    float IconSize => data != null ? data.worldIconSize : 0.30f;
    float HoverHeight => data != null ? data.worldHoverHeight : 0.16f;
    float ShadowSize => data != null ? data.worldShadowSize : 0.26f;

    /// <summary>拾取距离（米）。与"交互焦点"用同一个值。</summary>
    public const float PickupRange = 1.6f;

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
        InteractFocus.Unregister(this);    // 注册配注销（场景重载后焦点表里不会留死引用）
    }

    // ==================== 视觉构建 ====================

    /// <summary>建图标 + 阴影（幂等）。`Create` 在设好 `data` 之后会显式调一次。</summary>
    public void BuildVisual()
    {
        if (_built) return;
        _built = true;

        // ---- 图标（billboard 在 LateUpdate 里做）+ 脚下半影：**共用实现**见 `PickupVisual` ----
        // （2026-10-15 收敛：原来弹药包/投掷物/魔法卡三类里这段逐段同构；数值仍由各类传 —— 三类的图标尺寸本来就不同）
        _sr = PickupVisual.CreateIcon(transform, data, IconSize);
        _shadowSr = PickupVisual.CreateShadow(transform, ShadowSize, data != null ? data.worldShadowAlpha : 0.30f);

        PlaceIcon(0f);
        Layers.SetRecursively(gameObject, Layers.Pickup);   // 与 WeaponPickup / HealthPack 同一层（子弹与视线掩码都不含它）
    }

    void LateUpdate()
    {
        PickupVisual.Billboard(_sr);   // 朝相机（billboard）—— 共用实现

        _t += Time.deltaTime;
        PlaceIcon(Mathf.Sin(_t * 2.2f) * BobAmplitude);
    }

    /// <summary>把图标放到"离地高 + 浮动量"的位置（共用实现；浮动节奏由本类给）。</summary>
    void PlaceIcon(float bob) => PickupVisual.PlaceIcon(_sr, HoverHeight, bob);

    /// <summary>阴影的**尺寸**与**颜色**（透明度来自弹药卡的 `worldShadowAlpha`）。
    /// Sprite 的默认世界尺寸由 PPU 决定（程序生成的 64px 图 = 0.64m）→ 缩放到想要的大小；
    /// 平躺（绕 X 转 90°）之后，localScale 的 x/y 仍然对应"平面内的两个方向"。
    /// **唯一实现**：`BuildVisual` 与 `ApplyLook` 共用一份。</summary>
    void SetShadowLook()
        => PickupVisual.ApplyShadow(_shadowSr, ShadowSize, data != null ? data.worldShadowAlpha : 0.30f);

    /// <summary>把弹药卡上的外观参数应用到本实例（只改尺寸 / 位置 / 颜色，**不重建物体** → 编辑模式下也能安全调用）。</summary>
    public void ApplyLook()
    {
        if (_sr == null) return;
        // ⚠️ 图标与颜色在这里**重新取一次**：改了弹药卡的口径色 / 描边色（等级描边）后，
        //    `ItemVisuals.IconOf` 会返回新的合成图 —— 不重取的话场上的包还挂着旧图，改色看不见效果。
        _sr.sprite = ItemVisuals.IconOf(data);
        _sr.color = ItemVisuals.ColorOf(data);
        PickupVisual.FitScale(_sr, IconSize);
        SetShadowLook();
        PlaceIcon(0f);
    }

    /// <summary>把"某张弹药卡"的外观改动同步到场上的所有弹药包（由 `AmmoData.OnValidate` 调用）——
    /// 编辑器里改完数字**立刻能看见效果**，不用重新进 Play。只改尺寸、不销毁重建，所以编辑模式与运行时都安全。</summary>
    public static void RefreshLook(AmmoData def)
    {
        if (def == null) return;
        for (int i = 0; i < All.Count; i++)
        {
            var p = All[i];
            if (p != null && p.data == def) p.ApplyLook();
        }
    }

    // 设层已收敛到 `Layers.SetRecursively`（2026-10-15）：本类原来那份 `SetLayers` 已删。

    // ==================== IInteractable ====================

    public Transform InteractAnchor => transform;
    public string InteractLabel => data != null ? data.DisplayName + " ×" + packSize : "弹药";
    public string InteractKey => "F";
    public float InteractRange => PickupRange;

    /// <summary>按键拾取（由交互焦点调用）。
    /// 背包只收得下一部分时（同类格余量不够），**扣掉拿走的、余量留在原地**（与箱子取物同一套语义）。
    /// ⚠️ 不能"收下多少都销毁整包" —— 那会让没收下的那部分**凭空蒸发**（2026-09-27 审查发现的 bug）。</summary>
    public void Interact(PlayerController player)
    {
        if (player == null || data == null || packSize <= 0) return;

        var bp = player.backpack;
        int put = bp != null ? bp.AddItems(data, packSize) : 0;
        if (put <= 0)
        {
            HUD.AnnounceBackpackFull();
            return;   // 一点都收不下 → 整包留在地上，等玩家腾出格子
        }

        Sfx.Pickup();

        if (put < packSize)
        {
            // 只收下了一部分 → 余量留在原地（拾取气泡/角标会显示新数量），不销毁
            packSize -= put;
            HUD.Announce("拾取  " + data.DisplayName + " ×" + put + "（地上还剩 " + packSize + "）", 1.0f, 26, data.pickupColor);
            return;
        }

        HUD.Announce("拾取  " + data.DisplayName + " ×" + put, 1.0f, 26, data.pickupColor);
        Destroy(gameObject);
    }

    // ==================== 创建 ====================

    /// <summary>在指定位置创建一包弹药。`packSize ≤ 0` 时用弹药卡上的默认值。</summary>
    public static AmmoPickup Create(AmmoData def, Vector3 pos, int packSize = -1)
    {
        if (def == null) return null;

        var root = new GameObject("AmmoPickup_" + def.DisplayName);
        root.transform.position = pos;

        var p = root.AddComponent<AmmoPickup>();
        p.data = def;
        p.packSize = packSize > 0 ? packSize : Mathf.Max(1, def.packSize);
        p.BuildVisual();   // ⚠️ 必须在设好 `data` 之后手动建（Awake 早于赋 data，那时还是 null）
        return p;
    }
}
