using UnityEngine;

/// <summary>
/// **烟雾效果卡**（投掷物效果的一种）：在引爆点生成一片**烟云** —— 持续一段时间 + **遮挡敌人视线**。
///
/// ⭐ 加一种"烟"（浓烟 / 白烟 / 以后的毒烟视觉变体）= **复制一张卡改数值 + 换材质**，代码一行不动；
///   同一张卡可以拖给多个投掷物（复用），和「爆炸」卡同一套关系（见 `ThrowableEffectData` 的说明）。
/// ⚠️ 铁律（与所有效果卡一致）：卡里**只放配置**；"这片烟还剩几秒"这类运行时状态归它生成的世界对象
///   （`SmokeVolume`）—— 卡是菜谱，不许存冰箱里的东西。
/// </summary>
[CreateAssetMenu(menuName = "游戏/投掷物效果/烟雾", fileName = "Effect_烟雾")]
public class SmokeEffectData : ThrowableEffectData
{
    [Header("烟云")]
    [Tooltip("烟云半径（米）：**可见 / 占地半径**（按 XZ 圆算）。粒子铺满这个圆、且被夹在圈内不出界。\n" +
             "⚠️ 挡视线半径是它的 blockRatio 倍（见下），不是整个圆都挡——边缘毛糙处不算。")]
    [Min(0.5f)] public float radius = 6f;
    [Tooltip("圆柱高度（米）：烟立起来的高度。默认 1.65 = 玩家身高(1.6) + 一点余量；俯视角下决定“烟墙”多高。\n" +
             "⚠️ 只影响表现，不影响遮挡判定（判定只用 XZ 平面的圆）。")]
    [Min(0.1f)] public float smokeHeight = 1.65f;
    [Tooltip("发射盘半径占 radius 的比例：盘比整圆略小，给单颗烟“长大(sizeGrow)”留余量。\n" +
             "粒子中心被夹在 (radius − 半宽) 内 → 可见外缘刚好≈ radius，永远不出圈。")]
    [Range(0.5f, 1f)] public float fillRatio = 0.92f;
    [Tooltip("**挡视线半径**占 radius 的比例：0.9 = 圈内 90% 才让敌人丢失视野，外圈毛糙不算（更硬核）；\n" +
             "1 = 满圆都挡（更宽松）。右键范围指示器画的也是这个生效半径。")]
    [Range(0.5f, 1f)] public float blockRatio = 0.9f;
    [Tooltip("持续时间（秒）：从起烟到「烟最浓」的阶段。之后停止生成、剩下的粒子自然消散")]
    [Min(0.5f)] public float duration = 14f;
    [Tooltip("消散时长（秒）：停止生成后的残余时间（建议 ≥ 粒子寿命，否则烟会突然消失）。\n" +
             "⚠️ **遮挡**只在「持续时间 + 消散 × 0.6」内有效 —— 烟明显变薄后就不再挡视线")]
    [Min(0.5f)] public float fadeOut = 6f;
    [Tooltip("浓度：1 = 标准。它改的是「每片烟的透明度」（调大更浓、更挡视野；调小更透）")]
    [Range(0.2f, 3f)] public float density = 1f;
    [Tooltip("粒子材质（**必须用「Particles/Standard Unlit」且队列 = Transparent(3000)**，早于视野暗幕 `Transparent+100`）。\n" +
             "留空 = 代码生成的灰色占位材质（能看清范围与时长，但是没有烟团贴图）")]
    public Material material;

    [Header("粒子外观（按半径自动缩放，一般不用动）")]
    [Tooltip("单颗烟粒子的寿命（秒）：4~6 比较像烟；调短 = 更「碎」，调长 = 更「厚」")]
    [Min(0.5f)] public float particleLifetime = 5f;
    [Tooltip("单颗烟粒子的基准大小（米，半径 4m 时）：烟越大自动按比例放大")]
    [Min(0.2f)] public float particleSize = 3.5f;
    [Tooltip("每秒生成几颗（半径 4m 时；烟越大按面积自动放大）")]
    [Min(1f)] public float emitRate = 22f;
    [Tooltip("湍流强度（0 = 不翻卷，1 = 明显翻卷）：这是「像不像真烟」最关键的一个数。\n" +
             "⚠️ 它**不是向外偏移量**：只是让每颗烟原地打转 / 上下翻滚的乱流（平均不往外推）。\n" +
             "配合 XZ 夹取，粒子只能在圈内抖，出不了圈——所以调大只会更“活”、不会让烟变大。")]
    [Min(0f)] public float curl = 0.7f;

    [Header("高级（一般不用动；2026-10-13 从代码常量搬进检查器）")]
    [Tooltip("湍流**频率**：越大 = 烟丝越细碎、越「活」；越小 = 越大块的团。\n" +
             "⚠️ 觉得烟有**横向层纹**（压扁后粒子挤在同一高度层）→ 把这个调大试试（例如 0.12 → 0.25）")]
    [Min(0.01f)] public float curlFrequency = 0.12f;
    [Tooltip("湍流**滚动速度**：噪声场移动的快慢 —— 0 = 翻卷是静止的（会显得假）")]
    [Min(0f)] public float curlSpeed = 0.12f;
    [Tooltip("烟片**长大倍数**（寿命末相对出生）：1 = 不长大；越大越蓬松（也越容易糊住画面）")]
    [Min(1f)] public float sizeGrow = 1.6f;
    [Tooltip("「消散阶段」还挡多久视线（占消散时长的比例）：0.6 = 前 60% 还挡、之后不挡。\n" +
             "0 = 一停止生成就不挡（更宽松）；1 = 一直到彻底消失都挡（更硬核）")]
    [Range(0f, 1f)] public float blockTailRatio = 0.6f;
    [Tooltip("每片烟的**自转速度**（度/秒；正负方向逐粒子随机）：0 = 不自转。\n" +
             "⚠️ 这是「让烟看起来像活的」的一项 —— 每片烟缓慢翻转 → 整团在动。\n" +
             "默认 15 ≈ 之前写死在代码里的那版（±14.3°/秒）；想更明显就 25~35，太大像扇风车。")]
    [Min(0f)] public float spinDegPerSec = 15f;

    // ---------------- 扩散（圆柱模型下默认 0） ----------------
    // ⚠️ 固定圆柱模式：本项默认 0（粒子不往外飞，主体靠发射盘铺满）。
    //    只有想要"烟从中心慢慢涨开"时才调大；调大 = 主体随时间外扩，不再是满圆。
    [Tooltip("**向外扩散速度**（米/秒）：0 = 烟原地不动、完全靠发射盘铺满圆柱（默认，固定圆柱用这个）。\n" +
             "⚠️ 只在你想要“烟从中心慢慢涨开”时才调大；调大 = 主体会随时间外扩，不再是一丢出来就是满圆。")]
    [Min(0f)] public float driftSpeed = 0f;

    /// <summary>「按住右键看弹道」时的范围指示圈半径 = **挡视线半径** = radius × blockRatio（勾了 showRangeIndicator 才画）。
    /// ⚠️ 画的是"敌人会丢失视野的范围"，不是整片可见烟（可见烟略大、边缘逸散不算）。</summary>
    public override float DangerRadius => radius * blockRatio;

    public override void Apply(Vector3 point)
    {
        SmokeVolume.Spawn(point, this);
    }
}
