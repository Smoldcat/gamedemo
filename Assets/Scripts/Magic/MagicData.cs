using UnityEngine;

/// <summary>
/// **魔法（技能）定义卡** —— <see cref="ItemData"/> 的抽象子类，与 <see cref="WeaponData"/> 平级。
///
/// 为什么继承 <see cref="ItemData"/>：**白拿整套物品系统** ——
/// 背包格 / 商店买卖 / 箱子掉落 / 图标 / tooltip / 丢弃落地，一行代码都不用写。
///
/// ⭐ 与武器最大的不同（架构上的根）：
///   · 武器 = **一种行为**（发射子弹）+ N 组参数 → 加新枪 **只改卡**
///   · 魔法 = **N 种行为**，各自流程完全不同 → 加新魔法 **通常要写一个类**
/// 但下面这套设计把"写类"的成本压到最低：
///   ① 卡上填 <see cref="behaviourTypeName"/>（"类名, 程序集名"）→ 运行时**反射**创建行为
///      （与 `EnemyConfig.behaviourTypeName` 完全同一套做法，编辑器里有下拉框可用）
///   ② 公共部分（装备 / 卸下 / 冷却 / 按键入口 / 命中转发 / UI 广播）全在
///      <see cref="PlayerMagic"/> 与 <see cref="MagicBehaviour"/> 里 —— 新魔法不用重复实现
///   ③ ⭐ **同类机制只写一次代码**：一旦"投射物"这套做完，"火球 / 冰球 / 毒球 / 闪电球"
///      就**全变成改卡**了；"条件触发"同理（斩杀 / 爆炸 / 治疗都是它的变体）
///
/// ⚠️ 抽象类的理由：本类只承载"所有魔法共有"的字段；**具体数值放各自的子类**
///    （投射物有球数/速度，条件有阈值/时长），这样每张卡的 Inspector 都干净。
///    新增一种机制 = 新增一个 `<see cref="MagicData"/>` 子类 + 一个 `<see cref="MagicBehaviour"/>` 子类。
///
/// ⚠️ 字段名一律不要改（<see cref="ItemData"/> 的序列化铁律）。
/// </summary>
public abstract class MagicData : ItemData
{
    // ==================== 基础 ====================

    [Header("魔法 · 基础")]
    [Tooltip("魔法显示名（HUD / 拾取气泡 / tooltip 里显示的名字）。留空则用资源文件名")]
    public string magicName = "新魔法";

    [Tooltip("行为类名，格式 \"类名, 程序集名\"。\n" +
             "例：ProjectileGroupMagic, Assembly-CSharp\n" +
             "⭐ 不用手打 —— 卡的最下方有「行为类型（下拉选）」，自动列出所有 MagicBehaviour 子类。\n" +
             "⚠️ 真手打了也没关系：格式不对 / 类不存在时，装备后会报一条**带卡名和期望格式**的 LogError\n" +
             "   （不是静默失效，见 PlayerMagic.CreateBehaviour）。")]
    public string behaviourTypeName = "";

    [Tooltip("被动魔法 = 装备即生效、不需要按键（例：印记斩杀）。\n" +
             "主动魔法 = 按 E 释放（例：魔法球群）。\n" +
             "⚠️ 这只是「触发方式」，不是两类不同的东西 —— 所以共用同一张卡，而不是两个基类。")]
    public bool isPassive;

    [Tooltip("冷却秒数。**主动与被动都读这个字段**（2026-09-29 起被动也有冷却）：\n" +
             "  · 主动：按 E 释放后进冷却（例：魔法球群 12）\n" +
             "  · 被动：生效一次后进冷却 —— 冷却期间**完全不生效**\n" +
             "    （不挂新印记，场上已挂的印记也不会触发；覆盖在敌人身上的印记图标会留着，冷却一好立刻判）\n" +
             "  · 填 0 = 永远就绪 = 【时刻生效】（被动想要【无冷却】就填 0）\n" +
             "📌 HUD 会自动显示剩余秒数（主动与被动一样，就在快捷栏那一格里）\n" +
             "第一期只有 CD，没有蓝量 —— 以后要做蓝量，是加一个【消耗字段 + 资源系统】，不用改这里。")]
    [Min(0f)] public float cooldown = 8f;

    // ==================== 升级预留 ====================

    [Header("魔法 · 升级预留（第一期固定 1）")]
    [Tooltip("当前等级。第一期所有卡都是 1 —— 字段先留着，以后做升级不用动数据结构。\n" +
             "⚠️ 等级**只用于展示与数值缩放**，绝不参与匹配（匹配一律走 StackId）。")]
    [Min(1)] public int level = 1;

    [Tooltip("每级数值成长系数：具体魔法类读它来缩放自己的数值（例：伤害 × levelScale 的 (level-1) 次方）。\n" +
             "⚠️ 别以为它【还没接上】（这里曾经这么写过）：**已经真的在生效** ——\n" +
             "   算法 `Pow(levelScale, level-1)` 已接进投射物伤害（MagicBehaviour.LevelMulOf → MagicProjectile.OnHit）。\n" +
             "   现在看不出差别，只是因为所有卡的 level 都是 1；把 level 调成 2 就会立刻看到数值变化。")]
    [Min(0.01f)] public float levelScale = 1f;

    // ==================== 展示 ====================

    [Header("魔法 · 展示")]
    [Tooltip("一句话说明（tooltip / 以后可能用在技能提示上）")]
    [TextArea(2, 4)] public string shortDesc = "";

    // ---- 掉在地上的外观（与 AmmoData 的 world* 系列同一套；用户 2026-09-28 定"可不用但不能没有"）----
    // ⚠️ 这些是**表现参数**，改它们不需要动任何代码 —— 正是"表现资源化"那条纪律的落点。
    // ⚠️ 若卡上填了 `pickupModelPrefab`（继承自 ItemData），地上的卡会用模型而不是图标，本组参数就不起作用。
    [Header("魔法 · 地上外观（占位用；有正式模型后可忽略）")]
    [Tooltip("地上图标的显示大小（米）")]
    [Min(0.01f)] public float worldIconSize = 0.34f;
    [Tooltip("图标离地高度（米）—— 让它看起来是浮着的")]
    [Min(0f)] public float worldHoverHeight = 0.20f;
    [Tooltip("脚下阴影的直径（米）")]
    [Min(0.01f)] public float worldShadowSize = 0.30f;
    [Tooltip("阴影浓度（0~1）")]
    [Range(0f, 1f)] public float worldShadowAlpha = 0.30f;

    // ==================== 反射解析（唯一实现） ====================

    System.Type _behaviourType;

    /// <summary>本卡对应的行为类型（<see cref="behaviourTypeName"/> 的反射解析，**带缓存**）。
    /// 解析失败返回 null —— 调用方（<see cref="PlayerMagic"/>）必须做兜底并给出可读的报错，
    /// 不要静默失败（否则表现是"装备了但按 E 没反应"，极难排查）。
    /// ⚠️ 与 `EnemyConfig.BehaviourType` 同一套思路：**别在调用处各写一份 `Type.GetType`**。</summary>
    public System.Type BehaviourType
    {
        get
        {
            if (_behaviourType == null && !string.IsNullOrEmpty(behaviourTypeName))
                _behaviourType = System.Type.GetType(behaviourTypeName);
            return _behaviourType;
        }
    }

    public override string DisplayName => string.IsNullOrEmpty(magicName) ? name : magicName;

    /// <summary>编辑器兜底。<see cref="ItemData"/> 的子类重写时**必须调 `base.OnValidate()`**。</summary>
    protected override void OnValidate()
    {
        base.OnValidate();
        if (cooldown < 0f) cooldown = 0f;
        if (level < 1) level = 1;
        if (levelScale < 0.01f) levelScale = 0.01f;
        if (worldIconSize < 0.01f) worldIconSize = 0.01f;
        if (worldHoverHeight < 0f) worldHoverHeight = 0f;
        if (worldShadowSize < 0.01f) worldShadowSize = 0.01f;

        // 编辑器里改完外观参数 → 场上的魔法卡**立刻**跟着变（不用重新进 Play）。
        // 与 `AmmoData.OnValidate` 的处理一致；只改尺寸 / 位置 / 颜色、不重建物体，所以编辑模式下也安全。
        MagicPickup.RefreshLook(this);
    }
}
