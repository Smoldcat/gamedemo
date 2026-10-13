using UnityEngine;

/// <summary>弹药口径：**武器按它匹配弹药**（换弹时统计与扣除都走它）。</summary>
public enum AmmoKind
{
    /// <summary>不吃弹药：换弹**不消耗备弹**（无限）。用于 RPG 等特殊武器（用户 2026-09-27 定）。</summary>
    None    = -1,
    Handgun = 0,   // 手枪弹（M1911 等）
    Rifle   = 1,   // 步枪弹（M4 / AK74 / M249 等）
    Sniper  = 2,   // 狙击弹（M107 等）
    SMG     = 3,   // 冲锋枪弹（乌兹等）
    Shotgun = 4    // 霰弹（贝内利 M4 等）
}

/// <summary>
/// 弹药定义卡（<see cref="ItemData"/> 的具体子类之一）—— **一种弹药 = 一张卡**
/// （5 个口径 × 每个等级各一张；⚠️ 别写"当前 5 种"，那是只有一级时的旧数量）。
///
/// 设计要点：
/// · **武器只认 <see cref="ammoKind"/>**（口径）：一把枪能用哪些弹只看这一项 —— **等级不参与匹配**。
///   换弹时按「弹匣当前装的那张卡」（`Slot.ammoId` / `ItemStack.ammoId`）统计与扣除，
///   唯一入口是 `PlayerBackpack.CountAmmo(AmmoData)` / `TakeAmmo(AmmoData, int)`（**按卡**的那对）。
///   ⚠️ 别用按口径求和 —— 那会把一级 + 二级加在一起（按口径的那对已于 2026-09-27 删除）。
/// · <see cref="packSize"/> = 地上"一包"多少发，同时也是按 **F** 捡起时进背包的数量；编辑器可调。
/// · 弹药**可堆叠**：在 Inspector 里勾 `canStack` + `maxStack` 填 999（本类不改基类的堆叠语义）。
/// · 世界外观**不用 3D 模型**：地上直接显示图标（见 `AmmoPickup`），图标走 `ItemVisuals.IconOf`。
///
/// ⭐ **弹药分级已上线**（2026-09-27，一级 / 二级；用户方案）：
///   · 同一口径**各建一张卡**（`tier` 区分等级、`damageMultiplier` 区分伤害）→ 武器匹配仍只看 `ammoKind`，
///     **武器侧零改动** ✓
///   · 一 / 二级是**两张不同的卡** → `StackId` 不同 → 背包里天然**各占一格**（用户要的"分开存储"）
///   · 统计 / 扣除 / HUD 显示一律**按卡**（`CountAmmo(AmmoData)`）—— 绝不能求和
///   · 按 **T** 开弹药等级选择器、滚轮选档、再按 T / 左键提交 = 一次换弹（旧弹退回背包）：
///     见 `PlayerController.BeginAmmoGradeSwap`（由 `AmmoSelector` 调用）/ `FinishSwitchAmmo`
///   · 伤害倍率在 `Bullet.Spawn` 一次乘进 `ShotProfile.damage`
///   · 等级描边（绿框区分外观）在 `ItemVisuals.AmmoIconOutlined`（`tierOutlineColor` + `outlineWidth`）
/// </summary>
[CreateAssetMenu(fileName = "Ammo_新弹药", menuName = "游戏/物品/弹药", order = 11)]
public class AmmoData : ItemData
{
    [Header("弹药")]
    [Tooltip("显示名（背包 / 箱子 / 拾取气泡 / HUD 里显示）。留空则用资源文件名")]
    public string itemName = "弹药";
    [Tooltip("口径：武器按它匹配（手枪 / 步枪 / 狙击 / 冲锋 / 霰弹）")]
    public AmmoKind ammoKind = AmmoKind.Rifle;
    [Tooltip("地上「一包」多少发，也是按 F 捡起时进背包的数量。\n" +
             "默认取该口径主力武器的弹匣量（手枪 8 / 步枪 30 / 狙击 5 / 冲锋 34 / 霰弹 7），可自由调。")]
    [Min(1)] public int packSize = 30;

    [Header("弹药分级（同口径可互换；用户 2026-09-27 定）")]
    [Tooltip("等级：1 = 一级弹、2 = 二级弹…（决定按 T 切换的先后顺序与展示排序）。\n" +
             "⚠️ `0` **只在武器本身不吃弹药时**才允许（`AmmoKind.None`，如 RPG —— 它压根没有弹药卡）。\n" +
             "   普通口径的卡填 0 会让它**永远装不进枪**（\"有货的最低等级\"与按 T 都会跳过 `tier <= 0`）。\n" +
             "⚠️ **绝不用它做匹配** —— 武器永远只看 `ammoKind`（否则加三级弹就得回头改武器）。\n" +
             "⚠️ 军火箱里一级 weight 2 / 二级 weight 1 是**在箱子配方里手填的**，代码不做 tier → 权重的换算。")]
    [Min(0)] public int tier = 1;
    [Tooltip("伤害倍率：开火时乘在武器基础伤害上（一级 1.0 / 二级 1.1，用户 2026-09-27 定）。\n" +
             "⚠️ 用**倍率**而不是绝对值：倍率对不同武器自动等比（M4 的 22×1.1 与 M107 的 130×1.1 都合理），\n" +
             "   而固定加成会破坏武器平衡（给狙击枪 +10 等于没加）。")]
    [Min(0f)] public float damageMultiplier = 1f;
    [Tooltip("等级描边色：图标外圈那圈描边（一级留**透明** = 不描边；二级绿；以后三四级换别的色）。\n" +
             "⚠️ 宽度用 `outlineWidth`；这两项改完**立刻**同步到背包 / 箱子 / 地上的弹药包（走 `ItemVisuals` 的缓存）。")]
    public Color tierOutlineColor = new Color(0f, 0f, 0f, 0f);
    [Tooltip("描边宽度（像素，按 64×64 图标算）。用户 2026-09-27 定：2。0 = 不描边")]
    [Min(0f)] public float outlineWidth = 2f;

    /// <summary>等级徽标（HUD 用）：1 → `Ⅰ`、2 → `Ⅱ`…（0 或超出 12 级返回空串 = 不显示徽标）。
    /// **唯一实现**：别在 HUD / 背包里各写一份罗马数字表。</summary>
    public string TierMark
    {
        get
        {
            if (tier <= 0) return "";
            string[] marks = { "", "Ⅰ", "Ⅱ", "Ⅲ", "Ⅳ", "Ⅴ", "Ⅵ", "Ⅶ", "Ⅷ", "Ⅸ", "Ⅹ", "Ⅺ", "Ⅻ" };
            return tier < marks.Length ? marks[tier] : tier.ToString();
        }
    }

    // 📌 曾经有过一个 `TierName`（"二级"这种中文说法）—— 因为全工程无人调用（提示语里都用 `DisplayName`，
    //    而 `DisplayName` 本身就带"一级/二级"字样），2026-09-27 独立审计建议后删掉，避免出现第二份"等级说法"实现。

    [Header("世界外观（地上的弹药包）")]
    [Tooltip("地上图标的世界尺寸（米）：图标长边大约占这么大。\n" +
             "⚠️ 俯视角下太小会看不见（0.22 实测像个小点）→ 默认 0.30 更醒目。\n" +
             "改这里会**立刻同步**到场上的弹药包（不用重新进 Play），方便对着画面调。")]
    [Min(0.01f)] public float worldIconSize = 0.30f;
    [Tooltip("图标中心离地高度（米）：抬起来才有\"浮\"着的感觉，配合阴影才像实体")]
    [Min(0f)] public float worldHoverHeight = 0.16f;
    [Tooltip("脚下阴影的直径（米）")]
    [Min(0f)] public float worldShadowSize = 0.26f;
    [Tooltip("脚下阴影的**透明度**（0 = 完全看不见，1 = 纯黑）。\n" +
             "想让影子更柔和就调低（0.18~0.25），想更有\"压在地上\"的存在感就调高。\n" +
             "⚠️ 边缘的\"半影\"（模糊感）来自程序生成的径向渐变贴图，不受本值影响。")]
    [Range(0f, 1f)] public float worldShadowAlpha = 0.30f;

    public override string DisplayName => string.IsNullOrEmpty(itemName) ? name : itemName;

    protected override void OnValidate()
    {
        base.OnValidate();      // ⚠️ 必须调：否则基类的数值兜底会被这里覆盖掉
        if (packSize < 1) packSize = 1;
        if (worldIconSize < 0.01f) worldIconSize = 0.01f;
        if (worldHoverHeight < 0f) worldHoverHeight = 0f;
        if (worldShadowSize < 0f) worldShadowSize = 0f;
        worldShadowAlpha = Mathf.Clamp01(worldShadowAlpha);

        if (tier < 0) tier = 0;
        if (damageMultiplier < 0f) damageMultiplier = 0f;
        if (outlineWidth < 0f) outlineWidth = 0f;

        // 口径色 / 描边色 / 描边宽度 / 图标改了 → 弹药的"染色 + 描边"合成图**作废重建**
        // （不清理的话，背包 / 箱子里还挂着旧描边的图 —— 改色看不到效果）
        ItemVisuals.InvalidateAmmoIcon(this);

        // 编辑器里改这几项 → 场上的弹药包**立刻**跟着变（方便对着画面调大小 / 高度 / 阴影）
        AmmoPickup.RefreshLook(this);
    }

    /// <summary>口径的中文名（提示语 / HUD 用）。**唯一实现**：别在别处再写一遍 switch。</summary>
    public static string DisplayOf(AmmoKind kind)
    {
        switch (kind)
        {
            case AmmoKind.Handgun: return "手枪弹";
            case AmmoKind.Rifle:   return "步枪弹";
            case AmmoKind.Sniper:  return "狙击弹";
            case AmmoKind.SMG:     return "冲锋枪弹";
            case AmmoKind.Shotgun: return "霰弹";
            default:               return "弹药";
        }
    }
}
