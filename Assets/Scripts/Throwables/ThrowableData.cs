using UnityEngine;

/// <summary>
/// **投掷物定义卡**（手雷 / 烟雾弹 / 闪光弹 / 燃烧瓶…）—— 与 `WeaponData` / `AmmoData` / `MagicData` 同一族
/// （都继承 <see cref="ItemData"/>，白拿 图标 / 堆叠 / 价格 / 说明 / 拾取物外观，以及背包|箱子|商店的全套通道）。
///
/// ⭐ **加一种新投掷物 = 建一张卡**（+ 全新效果才加一个效果类），槽位 / 选择器 / 轨迹 / 物理 / 引信 一行不动。
/// 与"枪"的区别：枪的差异在"数值 + 弹道"，投掷物的差异在"数值 + **效果卡**"（见 <see cref="ThrowableEffectData"/>）。
///
/// 设计口径（2026-10-12 与用户逐条敲定，详见交接文档「最近变更」）：
///   · 投掷物 = **可堆叠的背包库存物品**（勾 `canStack` 即生效）—— 一格 N 颗，G 槽的数量是**派生值**（数库存）；
///   · 落点 = **准星指着的地面点**（超出 `maxThrowDistance` 时夹取）；出手点 = 手部；
///   · 抛物线 = **单条真抛物线**：峰值高度固定（本卡参数，**绝对离地高度**）—— 准星越远弧线越平、越近越陡；
///   · 真实飞行与轨迹预览**共用同一份"求解 + 步进"**（所见即所得，唯一实现见 `ThrowableBallistics`）；
///   · 爆炸时刻 = **三个计时里最先到的**（碰地面 1s / 撞障碍 5s / 出手 8s，全部本卡可调）。
/// </summary>
[CreateAssetMenu(menuName = "游戏/投掷物", fileName = "Throwable_新投掷物")]
public class ThrowableData : ItemData
{
    [Header("名称")]
    [Tooltip("显示名（背包 / 选择器 / 拾取气泡都用它）")]
    public string throwableName = "手雷";

    [Header("弹道（抛物线）")]
    [Tooltip("最大投掷距离（米）：准星指得比它还远时，落点被夹在最大距离处（预览也如实显示）")]
    [Min(1f)] public float maxThrowDistance = 15f;
    [Tooltip("峰值高度（米，**绝对离地高度**）：抛物线最高点离地多高（定值）。\n" +
             "· 准星越远 → 弧线越平、越近 → 越陡；想「扔过矮箱子」就调准星距离，让弧线在箱子处刚好高过它\n" +
             "· 峰值越高，顶点越接近「起点 / 落点」的正中（因为出手点比地面高约 1.2m）")]
    [Min(1f)] public float peakHeight = 3f;
    [Tooltip("出手速度（米/秒）：水平速度 = 它 → 飞行时间 = 投掷距离 ÷ 它")]
    [Min(2f)] public float throwSpeed = 12f;

    [Header("飞行物理")]
    [Tooltip("撞到东西后**法向**（迎面）速度保留比例：0 = 完全不弹，1 = 完全弹性")]
    [Range(0f, 1f)] public float bounceNormal = 0.4f;
    [Tooltip("撞到东西后**切向**（顺着表面）速度保留比例（摩擦）")]
    [Range(0f, 1f)] public float bounceTangential = 0.7f;
    [Tooltip("速度低于它视为停稳（米/秒）：停稳后不再积分，只等引信")]
    [Min(0f)] public float stopSpeed = 1.2f;

    [Header("引信（爆炸时刻 = 三个里最先到的那个）")]
    [Tooltip("**主引信**：碰到**地面**后多少秒爆炸。0 = 触地即爆（燃烧瓶用这个）")]
    [Min(0f)] public float groundFuse = 1f;
    [Tooltip("**保底 A**：首次撞到**非地面的障碍物**（墙 / 箱子）后多少秒必炸。\n" +
             "0 = 撞到即爆（燃烧瓶：碰任何东西都碎）；默认 5 防「墙间反复反弹一直不落地」")]
    [Min(0f)] public float impactFuse = 5f;
    [Tooltip("**保底 B**：出手后多少秒必炸（防「什么都没撞到」的哑弹）。0 = 关闭")]
    [Min(0f)] public float maxLife = 8f;

    [Header("效果")]
    [Tooltip("引爆时触发的**效果卡**（拖一张 ThrowableEffectData：爆炸 / 以后：烟雾 / 闪光 / 燃烧）。\n" +
             "同一张效果卡可以拖给多个投掷物（复用）；留空 = 只炸个响（无伤害等效果）")]
    public ThrowableEffectData effect;

    [Header("世界外观（只在「没配 pickupModelPrefab」的图标占位路径用）")]
    [Min(0.01f)] public float worldIconSize = 0.30f;
    [Min(0f)] public float worldHoverHeight = 0.16f;
    [Min(0.01f)] public float worldShadowSize = 0.26f;
    [Range(0f, 1f)] public float worldShadowAlpha = 0.30f;

    public override string DisplayName => throwableName;

    /// <summary>数值兜底（仅编辑器生效）。⚠️ 必须调 `base.OnValidate()`（守 ItemData 的堆叠约定）。</summary>
    protected override void OnValidate()
    {
        base.OnValidate();
        if (maxThrowDistance < 1f) maxThrowDistance = 1f;
        if (peakHeight < 1f) peakHeight = 1f;
        if (throwSpeed < 2f) throwSpeed = 2f;
        if (stopSpeed < 0f) stopSpeed = 0f;
        if (groundFuse < 0f) groundFuse = 0f;
        if (impactFuse < 0f) impactFuse = 0f;
        if (maxLife < 0f) maxLife = 0f;
        if (worldIconSize < 0.01f) worldIconSize = 0.01f;
        if (worldHoverHeight < 0f) worldHoverHeight = 0f;
        if (worldShadowSize < 0.01f) worldShadowSize = 0.01f;
        if (worldShadowAlpha < 0f) worldShadowAlpha = 0f;
    }
}
