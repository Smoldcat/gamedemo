using UnityEngine;

/// <summary>
/// 物品实例（大白话：**冰箱里那一格**）：指向一个物品定义 + 有几个。
/// 「数量」只存在这里 —— 物品定义（<see cref="ItemData"/>）永远不存数量，
/// 否则同一个定义被背包和箱子同时引用时会「串味」（改一处两边都变）。
///
/// ⚠️ 它**不是** ScriptableObject：运行时状态绝不能写进 .asset（那会把资源文件改坏）。
///    打 `[System.Serializable]` 是为了能让 Unity 直接序列化它（放进 `PlayerBackpack` / 箱子内容里）。
///
/// ⚠️ 将来的扩展点（现在**不做**，但别把这个结构堵死）：
///    武器以后要带「配件」这种**实例数据**（两把同款枪装的配件不同）→ 那时在本类里再加一个可空的实例数据字段即可，
///    不要改成「一格只能是一个对象引用」。
/// </summary>
[System.Serializable]
public class ItemStack
{
    [Tooltip("物品定义（Assets/Items 下的 .asset）")]
    public ItemData def;
    [Tooltip("数量。不可堆叠的物品恒为 1")]
    public int count = 1;

    public ItemStack() { }

    public ItemStack(ItemData def, int count)
    {
        this.def = def;
        this.count = Mathf.Max(1, count);
    }

    /// <summary>这格是不是空的（没定义或数量为 0）。</summary>
    public bool IsEmpty => def == null || count <= 0;

    /// <summary>显示名（空格返回空串）。</summary>
    public string DisplayName => def != null ? def.DisplayName : "";

    /// <summary>本物品的单格容量（空引用时为 1）。</summary>
    public int MaxStack => def != null ? def.MaxStack : 1;

    /// <summary>本格还能再收下几个「这种物品」（0 = 收不下）。</summary>
    public int RoomFor(ItemData other)
    {
        if (def == null || other == null) return 0;
        if (!def.CanMergeWith(other)) return 0;
        return Mathf.Max(0, MaxStack - count);
    }

    /// <summary>尽量收下 n 个；返回**没装下**的数量（0 = 全收下）。</summary>
    public int Add(int n)
    {
        if (n <= 0) return 0;
        if (def == null) return n;                       // 空格不接受任何东西（调用方应先写 def）
        int put = Mathf.Min(n, Mathf.Max(0, MaxStack - count));
        count += put;
        return n - put;
    }

    /// <summary>取走 n 个；返回**实际取走**的数量（不够就全拿走）。</summary>
    public int Take(int n)
    {
        if (n <= 0 || def == null) return 0;
        int take = Mathf.Min(n, count);
        count -= take;
        return take;
    }

    /// <summary>清空这一格（丢弃 / 用掉之后调）。</summary>
    public void Clear()
    {
        def = null;
        count = 0;
    }
}
