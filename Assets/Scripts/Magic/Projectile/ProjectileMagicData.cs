using UnityEngine;

/// <summary>
/// 「**投射物群**」魔法的定义卡（魔法①：一次放 N 个球，各自索敌 → 追踪 → 命中 → 消散）。
///
/// ⭐ 这一类卡是一整套**可复用的机制**：做完这一个之后，
///    "火球 / 冰球 / 毒球 / 闪电球 / 飞刀群" 都只是**再建一张卡 + 改数值和颜色**，
///    **不需要写任何代码**。这就是"机制代码化、内容配置化"的落点。
///
/// ⚠️ 纪律一：**所有数值与开关都在卡上** —— 代码里不许写死数字。
///    以后调手感（球数 / 速度 / 半径 / 时限 / 穿透）全部在 Inspector 里改。
/// ⚠️ 纪律二：**表现资源留空 = 用占位外观 / 跳过**，填了 = 用你的资源。
///    所以现在一个都不填也能跑（"可不用但不能没有"）。
/// </summary>
[CreateAssetMenu(fileName = "Magic_投射物群", menuName = "游戏/物品/魔法/投射物群", order = 40)]
public class ProjectileMagicData : MagicData
{
    // ==================== 基础 ====================

    [Header("【基础】")]
    [Tooltip("一次放出几个球")]
    [Min(1)] public int orbCount = 5;
    [Tooltip("每个球命中敌人的伤害（会乘上等级成长系数）")]
    [Min(0f)] public float damage = 25f;

    // ==================== 索敌 ====================

    [Header("【索敌】")]
    [Tooltip("索敌半径（米）。⭐ 圆心是【玩家】而不是球本身，而且**只在【轮到某颗球】的那一刻**判定：\n" +
             "   · 半径内有活敌人 → 这颗球飞出去追；\n" +
             "   · 半径内没有      → 这颗球【继续挂在身上】，等下个间隔再判断。\n" +
             "📌 这个值直接决定【球愿意跑多远去打人】：6 米 = 只有贴到脸上的敌人才打；12 米 = 视野内基本都打")]
    [Min(0.5f)] public float searchRadius = 12f;

    // ==================== 出现（起止位置 + 时长） ====================

    [Header("【出现】（起止位置 + 时长的通用做法）")]
    [Tooltip("出生点相对玩家的半径（米）：N 个球会均匀分布在这个圆上；同时用作【环绕半径】")]
    [Min(0f)] public float spawnRadius = 1.2f;
    [Tooltip("出生点高度（米，相对【玩家原点】）。\n" +
             "⚠️ 这个值决定球飞多高、也就是能打到多高的敌人 —— 之前踩过的坑：\n" +
             "   玩家原点在半身高处（约 0.55m），敌人胶囊体顶端只有约 1.17m；\n" +
             "   当时填了 +1.0 → 球飞到 1.55m，从敌人头顶掠过、一颗都打不中。\n" +
             "⭐ 现在飞行时会自动朝【目标的真实高度】修正，所以这里只给个大致合理的起点即可（默认 0.25 ≈ 胸口）")]
    [Min(-1f)] public float spawnHeight = 0.25f;
    [Tooltip("出现过程的时长（秒）。\n" +
             "  · 0 = 瞬间出现在终点位置\n" +
             "  · 大于 0 = 从起点平滑飞到终点，用来看清【球从哪冒出来、怎么成型】\n" +
             "⭐ 同一个参数即可表达两种表现 —— 这就是为什么【瞬现】也要写成【起止 + 时长】")]
    [Min(0f)] public float spawnDuration = 0f;

    // ==================== 挂在身上（诸葛亮被动式） ====================

    [Header("【挂在身上】（用户 2026-09-28 定，参考王者荣耀诸葛亮的被动）")]
    [Tooltip("法球**挂在玩家身上**时的公转速度（度/秒）。360 = 每秒转一圈。\n" +
             "⚠️ 只影响挂在身上的转圈快慢，不影响飞出去之后的飞行速度（那是【运动】区的 orbSpeed）。\n" +
             "📌 旧参数 orbitDuration（【必须转满 N 秒才出击】）已按用户要求删除 —— 现在球一直挂着，\n" +
             "   直到【攻击间隔到点 + 范围内有敌人】才被派出；没敌人就不出去，靠 orbLifetime 到点消散")]
    [Min(0f)] public float orbitSpeed = 320f;
    [Tooltip("出现的**起点**相对终点的偏移（米）。⚠️ 只有 spawnDuration 大于 0 时才起作用。\n" +
             "例：想让球从玩家手心冒出来 → 填一个朝向玩家的负偏移即可（第一期留零）")]
    public Vector3 spawnFromOffset = Vector3.zero;

    // ==================== 运动 ====================

    [Header("【运动】")]
    [Tooltip("飞行速度（米/秒）")]
    [Min(0.1f)] public float orbSpeed = 14f;
    [Tooltip("转向速度（度/秒）：决定追踪弧线好不好看。\n" +
             "太小会绕大圈、太大就像瞬间掉头（失去追踪的观感）")]
    [Min(30f)] public float turnSpeed = 720f;
    [Tooltip("是否追踪目标。关掉 = 直飞弹幕（朝出生方向飞出去，撞上谁算谁）")]
    public bool homing = true;

    // ==================== 生存 ====================

    [Header("【生存】")]
    [Tooltip("存活时限（秒）：到点自动消散。\n" +
             "⭐ 必须设 —— 否则飞出去打空的球会永久留在场上不断积累")]
    [Min(0.5f)] public float orbLifetime = 6f;
    [Tooltip("穿透数：命中后还能再打几个敌人（0 = 命中即消散）。第一期 0")]
    [Min(0)] public int pierceCount = 0;

    // ==================== 阻挡 ====================

    [Header("【阻挡】")]
    [Tooltip("是否被墙 / 箱子挡住。\n" +
             "  · false = 穿墙（用户 2026-09-28 定；实现上就是检测掩码只留敌人层）\n" +
             "  · true  = 撞墙即消散（掩码再加上 Obstacle 与 Ground）")]
    public bool blockedByObstacle = false;

    // ==================== 消亡 ====================

    [Header("【消亡】（与【出现】同一套起止位置模式）")]
    [Tooltip("消散过程时长（秒）。\n" +
             "  · 0 = 命中 / 超时后立即销毁（第一期）\n" +
             "  · 大于 0 = 命中后飞散、原地炸开之类的收尾表现（第二步实现插值，参数先留着）")]
    [Min(0f)] public float endDuration = 0f;

    // ==================== 表现（可空） ====================

    [Header("【表现】（可空 —— 留空则用代码占位外观）")]
    [Tooltip("球的显示模型。留空 = 用代码生成的球体（占位）；\n" +
             "以后换成你的火焰球 prefab 直接填这里 —— 逻辑一行都不用改")]
    public GameObject orbPrefab;
    [Tooltip("拖尾 prefab（留空 = 不挂拖尾）")]
    public GameObject trailPrefab;
    [Tooltip("占位球体的颜色（仅当 orbPrefab 留空时生效）")]
    public Color orbColor = new Color(0.45f, 0.75f, 1f);

    // ==================== 音效（可空） ====================

    [Header("【音效】（可空 —— 留空则不放）")]
    public AudioClip castSound;     // 释放时
    public AudioClip hitSound;      // 命中时
    public AudioClip endSound;      // 消散时

    // ==================== 时序 ====================

    [Header("【时序】")]
    [Tooltip("施法前摇（秒）：按下 E 之后多久才真正出球。第一期 0 = 立即\n" +
             "（以后要做抬手动作就填个 0.2）")]
    [Min(0f)] public float castDelay = 0f;
    [Tooltip("多个球之间的发射间隔（秒）—— ⭐ 已实现（用户 2026-09-28 定：默认 0.5）。\n" +
             "  · 0 = 五个球同时出\n" +
             "  · 0.5 = 每隔半秒补一个球：先是 1 个在环绕，然后 2 个、3 个…… 一个个长成护体球环\n" +
             "⭐ 每个球各自计自己的环绕时长 → 出击时刻也天然错开，不会出现【五个球一起齐射】\n" +
             "⚠️ orbCount × launchInterval = 最后一个球出场的时刻，别让总时长超过冷却时间")]
    [Min(0f)] public float launchInterval = 0.5f;

    [Tooltip("补位速度（度/秒）：某颗球被派出去之后，剩下的球要【重新均分圆环】——\n" +
             "本参数控制它们朝新位置靠拢的快慢（限速滑动，不是瞬间吸附）。\n" +
             "  · 太小 = 补位慢吞吞，像卡住\n" +
             "  · 720（默认）≈ 0.1 ~ 0.2 秒滑到位，观感自然\n" +
             "⚠️ 从前是【直接吸附】：5 等分变 4 等分时，最外侧那颗会**瞬移 1.09 米**（2026-09-28 实测），非常突兀")]
    [Min(0f)] public float orbitCatchUpSpeed = 720f;

    protected override void OnValidate()
    {
        base.OnValidate();
        if (orbCount < 1) orbCount = 1;
        if (damage < 0f) damage = 0f;
        if (searchRadius < 0.5f) searchRadius = 0.5f;
        if (spawnRadius < 0f) spawnRadius = 0f;
        if (spawnDuration < 0f) spawnDuration = 0f;
        if (orbSpeed < 0.1f) orbSpeed = 0.1f;
        if (turnSpeed < 30f) turnSpeed = 30f;
        if (orbLifetime < 0.5f) orbLifetime = 0.5f;
        if (pierceCount < 0) pierceCount = 0;
        if (endDuration < 0f) endDuration = 0f;
        if (castDelay < 0f) castDelay = 0f;
        if (launchInterval < 0f) launchInterval = 0f;
        if (orbitCatchUpSpeed < 0f) orbitCatchUpSpeed = 0f;
    }
}
