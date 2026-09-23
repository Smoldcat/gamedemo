using UnityEngine;

/// <summary>
/// 物品定义基类（大白话：**菜谱卡**）—— 只描述「这是一样什么东西」，**绝不存运行时状态**
/// （「我有几个」存在 <see cref="ItemStack"/> 里；和项目既有做法一致：WeaponData 只存定义、弹匣存在背包槽里）。
///
/// 为什么要有它：项目里的「可拾取物」以前只有两种，而且写法完全不同 ——
///   武器 = 有 .asset 定义（WeaponData）；血包 = 代码里写死的枚举（ItemKind.Medkit）+ 一个世界脚本。
/// 于是「再加一种物品」（子弹 / 手雷 / 头盔…）就要改一堆 if / switch（背包、箱子、快捷栏、tooltip 全都要动）。
/// 有了本类：**加一种新物品 = 新建一个子类 + 新建一个 .asset**，箱子 / 背包那套代码一行都不用改。
///
/// 三个概念别混（详见交接文档）：
///   · 物品定义 <see cref="ItemData"/>        = 菜谱卡（名字 / 图标 / 能不能堆叠…）—— 每样东西一个 .asset
///   · 物品实例 <see cref="ItemStack"/>       = 冰箱里那一格（指向哪张菜谱 + 有几份）
///   · 物品清单 <see cref="ItemDatabase"/>    = 食堂门口的菜单目录（只列「我们有哪些菜」，不装字段）
///
/// ⚠️ 维护铁律（与项目既有约定一致，别违反）：
///   · **字段名一律不要改** —— .asset 是按字段名序列化的，改名会让已存的值静默回落默认（§6.8 / §5.2 铁律）
///   · 本类只放「所有物品共有」的字段；某类物品独有的数值（武器伤害 / 子弹口径 / 头盔防御）放它自己的子类
///   · 名字用**抽象属性** <see cref="DisplayName"/> 暴露：这样子类可以继续用自己**已有的**字段名实现它
///     （例：WeaponData 继续用 `weaponName` → 8 个武器资源零改名、零迁移、零风险）
/// </summary>
public abstract class ItemData : ScriptableObject
{
    [Header("外观（所有物品共有）")]
    [Tooltip("背包 / 箱子里显示的图标。留空 = 用程序生成的占位图标（名字首字 + 颜色块）")]
    public Sprite icon;
    [Tooltip("物品颜色：世界里拾取物的标识色，同时也是 UI 里的着色")]
    public Color pickupColor = Color.white;
    [Tooltip("掉在地上时的模型 Prefab。留空 = 用代码生成的占位外观。\n⚠️ 只在「这件东西会掉在地上」时才需要；只放在箱子 / 背包里的物品可以留空（那样只需要图标）")]
    public GameObject pickupModelPrefab;

    [Header("堆叠（所有物品共有）")]
    [Tooltip("同类是否允许叠在一格里。\n  · true  = 例如子弹（一格 999 发）\n  · false = 例如武器、血包（一件占一格）\n⚠️ 武器建议 false：将来加配件后「两把同款枪」可能完全不是一回事，叠加会把它们混成一格")]
    public bool canStack;
    [Tooltip("单格最多叠几个（仅「可堆叠」时生效）。1 = 等同于不可堆叠")]
    [Min(1)] public int maxStack = 1;

    [Header("身份与说明（所有物品共有）")]
    [Tooltip("身份证：判断「是不是同一种东西」只认它（**不认对象地址** —— 敌人掉落的枪是运行时副本，地址和资源不一样）。\n留空则自动用「类型名:资源名」兜底；只有「同名但其实是两样东西」的特殊情况才需要手填")]
    public string stackId;
    [Tooltip("悬停说明（背包 / 箱子 tooltip）。尤其「只有图标、没有模型」的物品，靠它说清这是什么")]
    [TextArea(2, 4)] public string description;

    /// <summary>显示名（背包 / 箱子 / 拾取气泡里显示的名字）。
    /// ⚠️ 做成**抽象属性**是刻意的：让每个子类用自己**已有的**字段名实现它（WeaponData → weaponName），
    ///    这样不需要把已存资源里的字段改名（改名 = 数据静默丢失，见类注释里的铁律）。</summary>
    public abstract string DisplayName { get; }

    /// <summary>「是不是同一种物品」的判断依据（供同类合并 / 去重用）。
    /// 手填的 <see cref="stackId"/> 优先；否则用「类型名:资源名」兜底（唯一且稳定）。</summary>
    public virtual string StackId
        => string.IsNullOrEmpty(stackId) ? (GetType().Name + ":" + name) : stackId;

    /// <summary>单格容量（不可堆叠恒为 1）。</summary>
    public int MaxStack => canStack ? Mathf.Max(1, maxStack) : 1;

    /// <summary>与另一个物品定义算不算「同一种东西」（叠加 / 去重用）。空引用一律不算。</summary>
    public bool SameKindAs(ItemData other) => other != null && StackId == other.StackId;

    /// <summary>能不能把「别的物品」叠进本物品这一格：必须同类、且本类允许堆叠。</summary>
    public bool CanMergeWith(ItemData other) => canStack && SameKindAs(other);

    /// <summary>数值兜底（仅编辑器生效）。
    /// ⚠️ 子类重写 OnValidate 时**必须调 `base.OnValidate()`**，否则这里的兜底会被子类覆盖掉。</summary>
    protected virtual void OnValidate()
    {
        if (maxStack < 1) maxStack = 1;
        if (!canStack) maxStack = 1;
    }
}
