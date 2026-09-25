using UnityEngine;

/// <summary>
/// 商店货架的一项（"这家店卖这样东西：多少钱、有多少库存、补多快"）。
/// 照抄 `ChestConfig` 的写法：条目 = **引用**一张物品定义卡 + 这一项自己的数据。
///
/// ⚠️ 价格 / 库存 / 补货**不能**写进物品定义卡（`ItemData`）：
///    同一个物品在"军火商"和"黑市"应该能卖不同价、有不同库存 —— 那是**商店的数据**，不是物品的属性。
/// </summary>
[System.Serializable]
public class ShopEntry
{
    [Tooltip("物品定义（从 Assets/Items 下拖进来）")]
    public ItemData item;

    [Tooltip("玩家买入价（= 商店售价，金币）。想调试刷钱 → 把它调低（0 = 白送）")]
    public int buyPrice = 100;

    [Tooltip("玩家卖出价（= 商店收购价，金币）。0 = 这家店不收这件东西\n" +
             "⚠️ 刻意**不强制**「卖出价 < 买入价」：填成卖出价 ≥ 买入价时玩家可以低买高卖无限刷钱\n" +
             "   （如果游戏设定就是「商店老板比较傻」，无视这条提示即可）")]
    public int sellPrice = 40;

    [Tooltip("最大库存（件）。0 = 无限（买不完）")]
    public int maxStock = 5;

    [Tooltip("补货时一次补几个。0 = 直接补满到最大库存")]
    public int restockAmount = 0;
}

/// <summary>
/// 商店配方（"这家店卖什么"）—— 与箱子配方（`ChestConfig`）同一套做法：
/// 一个配方可以被**多个商店共用**；不同商店用不同配方（军火商 / 杂货铺 / 黑市…）。
///
/// 创建：Project 右键 `Create > 游戏 > 商店配方`。
///
/// 三层分工（与"物品体系"的三层一致，别混）：
///   · 物品定义（<see cref="ItemData"/>）= 这是什么东西
///   · 商店配方（本类）                 = 这家店卖它多少钱 / 多少库存 / 补多快   ← 策划数据（本 .asset）
///   · 商店组件（<see cref="Shop"/>）   = 当前还剩几件、补货计时到哪了            ← 运行时状态（**不**写进 .asset）
///
/// ⚠️ 库存与补货计时只存在 `Shop` 组件实例里（场景重载自动恢复满货）—— 本资源只描述**规则**。
/// ⚠️ 表外物品（敌人掉的枪、箱子开出来的东西）的收购价 = 物品卡上的「基础价值」× 本配方的「表外收购比例」。
/// </summary>
[CreateAssetMenu(fileName = "Shop_新商店", menuName = "游戏/商店配方", order = 0)]
public class ShopConfig : ScriptableObject
{
    [Header("货架")]
    [Tooltip("货架格数（面板排版用）。⚠️ 条目数不能超过格数 —— 超出的条目不会显示（编辑器会告警）")]
    [Min(1)] public int slotCount = 6;

    [Tooltip("货架条目：每项 = 一样物品 + 买入价 + 卖出价 + 最大库存 + 每次补几个")]
    public ShopEntry[] entries;

    [Header("规则")]
    [Tooltip("整店补货间隔（秒，**游戏时间**：暂停时停表）。从开局起算，每过这么久补一次货")]
    [Min(0f)] public float restockSeconds = 30f;

    [Tooltip("【表外物品】的收购比例：不在货架上的物品（敌人掉落的枪、箱子里开出来的东西）卖出时，\n" +
             "价格 = 物品定义卡上的「基础价值」× 本比例。0 = 这家店不收表外物品")]
    [Range(0f, 1f)] public float sellValueRatio = 0.4f;

    // ==================== 查询（买卖规则的"数据"一半；"流程"一半在 Shop 里） ====================

    /// <summary>货架上有几项条目。</summary>
    public int EntryCount => entries != null ? entries.Length : 0;

    /// <summary>取第 i 项条目（越界返回 null）。</summary>
    public ShopEntry EntryAt(int i)
        => (entries != null && i >= 0 && i < entries.Length) ? entries[i] : null;

    /// <summary>这件物品在货架上是第几项（-1 = 不在货架上）。
    /// ⚠️ 比的是**身份证 `StackId`**、不是对象引用 —— 敌人掉落的枪是 `Clone()` 出来的副本，
    ///    按引用去比会永远找不到（与"背包叠加 / 装备"同一套判据）。</summary>
    public int IndexOf(ItemData def)
    {
        if (def == null || entries == null) return -1;
        string id = def.StackId;
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e == null || e.item == null) continue;
            if (e.item.StackId == id) return i;
        }
        return -1;
    }

    /// <summary>取这件物品的货架条目（不在货架上返回 null）。</summary>
    public ShopEntry FindEntry(ItemData def)
    {
        int i = IndexOf(def);
        return i >= 0 ? entries[i] : null;
    }

    /// <summary>这件物品的**玩家买入价**（-1 = 本店不卖它）。</summary>
    public int BuyPriceOf(ItemData def)
    {
        var e = FindEntry(def);
        return e != null ? Mathf.Max(0, e.buyPrice) : -1;
    }

    /// <summary>这件物品的**玩家卖出价**（0 = 本店不收它）：
    ///  · 在货架上  → 用它的 `sellPrice`
    ///  · 不在货架上 → 兜底价 = 物品卡「基础价值」× 本配方的「表外收购比例」</summary>
    public int SellPriceOf(ItemData def)
    {
        if (def == null) return 0;
        var e = FindEntry(def);
        if (e != null) return Mathf.Max(0, e.sellPrice);
        if (sellValueRatio <= 0f || def.baseValue <= 0) return 0;
        return Mathf.Max(0, Mathf.RoundToInt(def.baseValue * sellValueRatio));
    }

    /// <summary>数值兜底 + 配置自检（仅编辑器生效；只用 if 判断，避免把合法值标脏 —— 与 EnemyConfig 同一约定）。</summary>
    void OnValidate()
    {
        if (slotCount < 1) slotCount = 1;
        if (restockSeconds < 0f) restockSeconds = 0f;

        if (entries != null)
            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e == null) continue;
                if (e.buyPrice < 0) e.buyPrice = 0;
                if (e.sellPrice < 0) e.sellPrice = 0;
                if (e.maxStock < 0) e.maxStock = 0;
                if (e.restockAmount < 0) e.restockAmount = 0;

                // 没选物品 = 静默空格（最容易查半天的那类问题）→ 明说
                if (e.item == null)
                    Debug.LogWarning($"[ShopConfig] 「{name}」的货架第 {i + 1} 项没有选「物品」→ 那一格会是空的。", this);
            }

        // 条目数 > 格数 → 多出来的条目**不会显示**。这类"静默失效"最容易查半天，所以这里明说
        if (entries != null && entries.Length > slotCount)
            Debug.LogWarning($"[ShopConfig] 「{name}」的条目数（{entries.Length}）超过货架格数（{slotCount}）→ " +
                             "多出来的条目不会显示。请把「货架格数」调大。", this);
    }
}
