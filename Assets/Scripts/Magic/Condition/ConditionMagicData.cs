using UnityEngine;

/// <summary>
/// 「**条件触发**」魔法的定义卡（魔法②：给敌人挂印记，满足条件时触发效果 —— 第一期 = 血量低于阈值 → 天降雷击斩杀）。
///
/// ⭐ 这同样是一整套**可复用的机制**：做完它之后，
///    "受击 N 次后爆炸" / "持续 X 秒后治疗玩家" / "被爆头后掉金币"
///    都只是**再建一张卡**（把条件与效果换成别的枚举值）—— 前提是条件与效果分开写（见下）。
///
/// ⚠️ **条件与效果刻意分成两组字段**（<see cref="conditionType"/> / <see cref="effectType"/>）：
///    第一期各自只有一个枚举值（等价于写死），但**结构已经留好缝** ——
///    以后加变体时是"加枚举值 + 在 <c>MagicMark</c> 的 switch 里加一支"，不是重写魔法。
///
/// ⚠️ 表现资源（<see cref="markIndicator"/> / <see cref="boltPrefab"/> / 音效）**留空 = 用占位外观 / 跳过**。
/// </summary>
[CreateAssetMenu(fileName = "Magic_条件斩杀", menuName = "游戏/物品/魔法/条件斩杀", order = 41)]
public class ConditionMagicData : MagicData
{
    // ==================== 条件 ====================

    [Header("【条件】")]
    [Tooltip("触发条件。第一期只有【血量低于百分比】；\n" +
             "以后要加【受击 N 次 / 被爆头 / 持续 X 秒 / 距离玩家 X 米内】，在这里加枚举值即可")]
    public ConditionTrigger conditionType = ConditionTrigger.HpBelowPercent;

    [Tooltip("血量阈值（0~1 的比例）：敌人当前血量 ≤ 最大血量 × 本值 时触发。默认 0.2 = 两成血")]
    [Range(0.01f, 1f)] public float executeThreshold = 0.20f;

    [Tooltip("兜底检查间隔（秒）。\n" +
             "⭐ 主判定走【受伤事件】（伤害结算后立刻判定，最准最省）；\n" +
             "本项只是兜底轮询（防止【没受伤但血量因别的原因变低】这类情况漏网）")]
    [Min(0.02f)] public float checkInterval = 0.2f;

    // ==================== 标记 ====================

    [Header("【标记】")]
    [Tooltip("印记持续时间（秒）。重复命中会**刷新**这个时长（用户 2026-09-28 定）")]
    [Min(0.1f)] public float markDuration = 6f;

    [Tooltip("印记的头顶图标 prefab。留空 = 用代码生成的发光小方块（占位）\n" +
             "⭐ 以后换美术**只改这个引用**")]
    public GameObject markIndicator;

    [Tooltip("印记图标的颜色（仅当 markIndicator 留空时的占位外观）")]
    public Color markColor = new Color(1f, 0.85f, 0.3f);

    [Tooltip("印记图标的浮动高度（米）")]
    [Min(0f)] public float markHeight = 2.1f;

    // ==================== 效果 ====================

    [Header("【效果】")]
    [Tooltip("条件满足时干什么。第一期只有【直接击杀】；\n" +
             "以后要加【范围爆炸 / 回血给玩家 / 给玩家加 buff / 额外掉金币】在这里加枚举值")]
    public ConditionEffect effectType = ConditionEffect.ExecuteKill;

    [Tooltip("效果的数值参数（第一期【直接击杀】不用它；\n" +
             "以后【爆炸】用它当伤害、【回血】用它当回血量 —— 留着是为了不改结构）")]
    [Min(0f)] public float effectValue = 0f;

    [Tooltip("触发时给玩家的一句提示。留空 = 不提示")]
    public string triggerMessage = "斩杀！";

    // ==================== 表现 ====================

    [Header("【表现】")]
    [Tooltip("雷柱外观 prefab。留空 = 用代码生成的圆柱（占位）")]
    public GameObject boltPrefab;
    [Tooltip("占位雷柱的直径（米）")]
    [Min(0.05f)] public float boltWidth = 1.2f;
    [Tooltip("占位雷柱的高度（米）—— 从天上落下来的柱子，够高才像【从天而降】")]
    [Min(0.5f)] public float boltHeight = 14f;
    [Tooltip("雷柱颜色（仅占位外观用）")]
    public Color boltColor = new Color(0.75f, 0.9f, 1f);
    [Tooltip("雷柱从生成到消失的时长（秒）")]
    [Min(0.02f)] public float boltLife = 0.25f;
    [Tooltip("触发时的闪光强度（0 = 不闪）")]
    [Range(0f, 1f)] public float flashAmount = 0.6f;

    // ==================== 音效（可空） ====================

    [Header("【音效】（可空 —— 留空则不放）")]
    public AudioClip triggerSound;   // 触发（雷击）时
    public AudioClip endSound;       // 消散 / 斩杀完成时

    // ==================== 镜头 ====================

    [Header("【镜头】")]
    [Tooltip("触发时的震屏强度（0 = 不震）。项目里 CameraRig.Shake 的上限是 0.5")]
    [Range(0f, 0.5f)] public float shakeAmount = 0.25f;

    protected override void OnValidate()
    {
        base.OnValidate();
        if (checkInterval < 0.02f) checkInterval = 0.02f;
        if (markDuration < 0.1f) markDuration = 0.1f;
        if (markHeight < 0f) markHeight = 0f;
        if (boltWidth < 0.05f) boltWidth = 0.05f;
        if (boltHeight < 0.5f) boltHeight = 0.5f;
        if (boltLife < 0.02f) boltLife = 0.02f;
    }
}

/// <summary>触发条件类型。⭐ **留缝**：加变体只需在这里加枚举值 + 在 <c>MagicMark.CheckCondition</c> 里加一支。</summary>
public enum ConditionTrigger
{
    /// <summary>敌人当前血量 ≤ 最大血量 × 阈值（第一期）</summary>
    HpBelowPercent = 0,
    // 以后：HitCount（受击 N 次）、HeadshotCount（被爆头 N 次）、Lifetime（挂满 X 秒）、 DistanceToPlayer…
}

/// <summary>条件满足后干什么。⭐ **留缝**：同上。</summary>
public enum ConditionEffect
{
    /// <summary>直接击杀（不走减伤，无条件致死）—— 用户 2026-09-28 定</summary>
    ExecuteKill = 0,
    // 以后：Explode（范围爆炸）、HealPlayer（给玩家回血）、BuffPlayer（给玩家加速）、DropGold…
}
