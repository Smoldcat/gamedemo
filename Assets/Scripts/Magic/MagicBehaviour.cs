using UnityEngine;

/// <summary>
/// **魔法行为基类**（策略模式）—— 与 <see cref="EnemyBehaviour"/> 完全同一套设计，
/// 因为两者面对的是同一个问题："一套框架 + N 种各不相同的具体做法"。
///
/// ⭐ 新增魔法 = 写一个继承本类的脚本 + 建一张卡（卡的最下方有「行为类型（下拉选）」，
///    会自动列出本类的所有子类 —— 见 `Assets/Editor/MagicDataEditor.cs`）——
///    **不需要改 <see cref="PlayerMagic"/> / HUD / 背包 / 战斗系统的任何代码**。
///
/// 分工（这条线要守住，否则每个魔法都会重复写一遍公共逻辑）：
///   · 本组件 = 「**这个魔法怎么工作**」：放出去干什么、挂什么状态、什么条件下触发
///   · <see cref="PlayerMagic"/> = 「**装备与释放的公共流程**」：装备 / 卸下 / 冷却 /
///     按键入口 / 命中转发 / 冷却与释放的 UI 广播
///
/// ⚠️ **成对纪律**：<see cref="OnEquip"/> 里做过的事（挂监听、生成物体、给敌人加组件），
///    <see cref="OnUnequip"/> 里必须全部撤掉 —— 否则中途换魔法会留下"幽灵效果"
///    （例如：印记魔法卸下后，之前挂上的印记还在敌人身上继续生效）。
///
/// ⚠️ 生命周期：本组件挂在 **Player** 物体上（由 <see cref="PlayerMagic"/> 用 `AddComponent` 创建），
///    换魔法时销毁重建 —— **不要**在子类里持有跨装备周期的静态状态。
/// </summary>
public abstract class MagicBehaviour : MonoBehaviour
{
    /// <summary>本行为的定义卡。由 <see cref="Init"/> 注入。</summary>
    protected MagicData data;

    /// <summary>宿主（玩家侧的魔法管理器）。用于报告事件、查玩家状态。</summary>
    protected PlayerMagic host;

    /// <summary>由 <see cref="PlayerMagic"/> 在创建后**立即**调用（每次装备只调一次）。
    /// ⚠️ 顺序：`Awake` → `Init` → 之后才可能收到 `OnEquip` / `OnPlayerHitEnemy`。</summary>
    public void Init(MagicData data, PlayerMagic host)
    {
        this.data = data;
        this.host = host;
        OnInit();
    }

    /// <summary>子类初始化钩子：建 prefab 引用、缓存组件、读卡上参数 —— **早于** <see cref="OnEquip"/>，
    /// 且此时还不知道"会不会被立刻卸下"，所以这里**不要**产生任何外部副作用。</summary>
    protected virtual void OnInit() { }

    // ==================== 主动魔法 ====================

    /// <summary>主动魔法：现在能不能放。
    /// 冷却由 <see cref="PlayerMagic"/> 统一管，这里只加**魔法自己**的条件
    /// （例："范围内必须有敌人" / "必须在场地上"）。
    /// ⚠️ 返回 false 时必须通过 <see cref="DenyCast"/> 说一句 —— 别静默失败。</summary>
    public virtual bool CanCast() => true;

    /// <summary>主动魔法：释放（由 <see cref="PlayerMagic"/> 在"冷却就绪 + CanCast 通过"后调用）。
    /// 内部只负责"产生效果"；冷却开始/结束、UI 刷新由 <see cref="PlayerMagic"/> 负责。</summary>
    public virtual void Cast() { }

    // ==================== 被动魔法 ====================

    /// <summary>被动魔法：玩家**命中敌人**时被调用（由 <see cref="PlayerMagic"/> 转发，
    /// 来源是 <see cref="Combat"/> 的命中结算 —— 直击与爆炸两条路径都会转发）。
    /// <paramref name="headshot"/> = 这一击是否爆头（有的魔法想只对爆头生效）。</summary>
    public virtual void OnPlayerHitEnemy(Health enemy, bool headshot) { }

    // ==================== 通用 ====================

    /// <summary>装备时（被动魔法在这里挂监听；主动魔法一般不需要实现）。</summary>
    public virtual void OnEquip() { }

    /// <summary>卸下 / 被替换 / 丢弃时（⚠️ 必须把 <see cref="OnEquip"/> 里做过的事全部撤掉）。</summary>
    public virtual void OnUnequip() { }

    /// <summary>每帧（由 <see cref="PlayerMagic.Tick"/> 驱动）。
    /// ⚠️ 冷却计时**不在这里**（那是公共流程）；这里只做本魔法自己的持续逻辑。
    /// ⚠️ 需要高频轮询（索敌 / 条件检查）时请**降频**，不要每帧扫全场 ——
    ///    项目里的既有做法见 `EnemyAI` 的索敌（0.12s 一次）。</summary>
    public virtual void Tick(float dt) { }

    /// <summary>释放失败时说一句（复用 HUD 公告，颜色与武器提示一致）。
    /// ⚠️ 提示语要**说清原因**（"冷却中 3.2s" / "附近没有敌人"），别只说"不能放"。</summary>
    protected void DenyCast(string msg)
        => HUD.Announce(msg, 1.0f, 26, new Color(0.9f, 0.7f, 0.4f));

    /// <summary>取本魔法的数值成长系数。
    /// ⚠️ 别写成"第一期恒为 1"（这里曾经这么写过）：**算法本身已经在按等级缩放**
    ///    （`Pow(levelScale, level-1)`，见 <see cref="LevelMulOf"/>）——
    ///    现在之所以都是 1，只是因为**两张卡的 `level` 都是 1**；填了 levelScale 就会立刻生效。
    /// 具体魔法算伤害时写成 `基础值 × LevelMulOf(卡)` 的形式即可 ——
    /// 这样以后开了升级，数值自动跟着走，不用回头改结构。</summary>
    protected float LevelMul() => LevelMulOf(data);

    /// <summary>同一份成长系数算法（⭐ **唯一实现**）—— 给不是 <see cref="MagicBehaviour"/> 子类的地方复用
    /// （例：运行时生成的投射物 <c>MagicProjectile</c> 也要按等级缩放伤害）。
    /// ⚠️ 别在别处再写一遍 `Pow(levelScale, level-1)` —— 两处各写一份，改一处就会不一致
    ///    （项目里"同一个量两处定义"的坑已经踩过）。</summary>
    public static float LevelMulOf(MagicData d)
    {
        if (d == null || d.level <= 1) return 1f;
        return Mathf.Pow(d.levelScale, d.level - 1);
    }
}

/// <summary>魔法的中文显示名（编辑器「行为类型」下拉框用）。
/// 不写则用类名去掉 `Magic` 后缀 —— 与 `EnemyBehaviour` 的 `[BehaviourName]` 是同一套约定。</summary>
[System.AttributeUsage(System.AttributeTargets.Class)]
public sealed class MagicName : System.Attribute
{
    public string Name;
    public MagicName(string name) { Name = name; }
}
