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
/// 弹药定义卡（<see cref="ItemData"/> 的具体子类之一）—— **一种弹药 = 一张卡**（当前 5 种）。
///
/// 设计要点：
/// · **武器只认 <see cref="ammoKind"/>**：换弹时按口径从背包统计 / 扣除，
///   唯一入口是 `PlayerBackpack.CountAmmo / TakeAmmo`（所以"以后加一级弹/二级弹"武器侧零改动）。
/// · <see cref="packSize"/> = 地上"一包"多少发，同时也是按 E 捡起时进背包的数量；编辑器可调。
/// · 弹药**可堆叠**：在 Inspector 里勾 `canStack` + `maxStack` 填 999（本类不改基类的堆叠语义）。
/// · 世界外观**不用 3D 模型**：地上直接显示图标（见 `AmmoPickup`），图标走 `ItemVisuals.IconOf`。
///
/// ⚠️ 将来做"弹药分级"（一级 / 二级…同口径可互换）的正确做法：
///   同一口径**再建几张卡**（各自图标 / 名字 / 价值不同）即可 —— 武器的匹配仍只看 `ammoKind`，
///   统计与扣除走 `CountAmmo(kind)`（内部遍历所有同口径弹药）→ **武器侧零改动**。
///   ⚠️ 但 HUD 到那时要**按每张卡分开展示**，不能简单求和（用户 2026-09-27 明确）。
/// </summary>
[CreateAssetMenu(fileName = "Ammo_新弹药", menuName = "游戏/物品/弹药", order = 11)]
public class AmmoData : ItemData
{
    [Header("弹药")]
    [Tooltip("显示名（背包 / 箱子 / 拾取气泡 / HUD 里显示）。留空则用资源文件名")]
    public string itemName = "弹药";
    [Tooltip("口径：武器按它匹配（手枪 / 步枪 / 狙击 / 冲锋 / 霰弹）")]
    public AmmoKind ammoKind = AmmoKind.Rifle;
    [Tooltip("地上「一包」多少发，也是按 E 捡起时进背包的数量。\n" +
             "默认取该口径主力武器的弹匣量（手枪 8 / 步枪 30 / 狙击 5 / 冲锋 34 / 霰弹 7），可自由调。")]
    [Min(1)] public int packSize = 30;

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
