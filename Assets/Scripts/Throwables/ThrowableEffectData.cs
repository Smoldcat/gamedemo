using UnityEngine;

/// <summary>
/// **投掷物效果卡**（抽象基类）：拖到 <see cref="ThrowableData.effect"/> 上，引爆瞬间由投掷物调 <see cref="Apply"/>。
///
/// ⭐ 为什么单独成卡（用户 2026-10-12 定）：效果要**可复用、可替换** ——
///   同一张「爆炸」效果卡能给手雷、能给以后的 RPG 弹；「烟雾1 / 烟雾2」两张卡改数值即可。
///   加一种全新效果 = **新建一个子类 + 建一张资产** → 主流程 / 别的效果 / 所有投掷物卡**一行不动**。
///
/// 与"敌人 / 魔法"那套（`behaviourTypeName` + 反射挂载行为类）的区别：
///   敌 / 魔法的行为**要挂在物体上、有逐帧生命周期**；投掷物效果是**一次性触发**（炸一下 / 生成一片烟），
///   没有生命周期 → 直接用**子类多态**最省一层反射和字符串配置。
///
/// ⚠️ **铁律：效果卡里绝不放运行时状态**（烟雾还剩几秒、火焰跳了几次伤害都不许存卡里）——
///   那些归它**生成的世界对象**（如烟雾体积 / 火焰区域各自的小组件管）。
///   这是与 `ItemData`「菜谱卡不存运行时状态」同一条规矩。
/// </summary>
public abstract class ThrowableEffectData : ScriptableObject
{
    [Header("公共")]
    [Tooltip("效果名（调试 / 以后做效果列表时显示）")]
    public string effectName = "效果";
    [TextArea(2, 4)]
    [Tooltip("说明（给自己看的：这个效果干什么、参数口径）")]
    public string description;

    [Header("范围指示圈（2026-10-12 加）")]
    [Tooltip("在「按住右键看弹道」时，于**准星落点**画一个「生效范围指示圈」（圆环 + 半透明填充）。\n" +
             "⚠️ 它只是**尺子**：把「生效半径有多大」画给你看 —— 真实杀伤区（含「隔墙不掉血」）请结合弹道自行判断。\n" +
             "生效范围**抽象**的投掷物（比如全屏闪光）→ 不勾选即可（这就是那个「可以关掉」的开关）。")]
    public bool showRangeIndicator;

    /// <summary>这套效果的"生效半径"（米，给范围指示圈用）。**0 = 没有范围可画**；子类按需覆写（爆炸 = `radius`）。
    /// ⚠️ 只在 `showRangeIndicator` 勾选时才被读取（两个条件是"与"）。</summary>
    public virtual float DangerRadius => 0f;

    /// <summary>在落点触发效果（由投掷物在"爆炸时刻"调用）。
    /// ⚠️ `point` = 手雷**当时所在的位置**（可能在滚动中 / 空中 / 贴墙上，都是合法的爆炸点）。</summary>
    public abstract void Apply(Vector3 point);
}
