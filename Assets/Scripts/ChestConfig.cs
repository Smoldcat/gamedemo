using UnityEngine;

/// <summary>
/// 物品池里的一项（**照抄 `WaveManager.enemyPool` 的写法**，你已经会用了）：
/// 一样物品 + 出现权重 + 一次抽到几个（数量范围）。
/// </summary>
[System.Serializable]
public class ItemPoolEntry
{
    [Tooltip("物品定义（从 Assets/Items 下拖进来）")]
    public ItemData item;
    [Tooltip("出现权重：越大越常抽到（同一张表里按权重随机；填 0 = 不刷）")]
    public float weight = 1f;
    [Tooltip("一次抽到几个（最少）")]
    public int minCount = 1;
    [Tooltip("一次抽到几个（最多）")]
    public int maxCount = 1;
}

/// <summary>
/// "这个箱子会刷出几样东西"的一项：次数 + 权重。
/// 用它来表达「大概率刷 3 样、小概率刷 5 样」——所以箱子**不会是每箱都塞满**。
/// </summary>
[System.Serializable]
public class RollChance
{
    [Tooltip("抽取次数（= 掉几堆东西；可堆叠的会并进同一格）")]
    public int count = 3;
    [Tooltip("权重：越大越常见")]
    public float weight = 1f;
}

/// <summary>
/// 箱子配方（"这个箱子刷什么"）。一个配方可以被**多个箱子共用**；不同箱子就用不同配方
/// （军火箱 / 医疗箱 / 补给箱…）—— 这就是"不同箱子放不同东西"的做法，不需要给每个箱子单独配一套。
///
/// 创建：Project 右键 `Create > 游戏 > 箱子配方`。
///
/// ⚠️ 内容**只在箱子第一次被打开时**抽一次，抽完锁住（关掉再开内容不变）。为什么：
///    每次打开都重抽的话，玩家可以"关掉再开"反复刷到出好东西为止。
/// </summary>
[CreateAssetMenu(fileName = "Chest_新箱子", menuName = "游戏/箱子配方", order = 0)]
public class ChestConfig : ScriptableObject
{
    [Tooltip("显示格数（以后大箱子填 10 也不用改代码）")]
    [Min(1)] public int slotCount = 5;

    [Header("刷什么（物品池：物品 + 权重 + 数量范围）")]
    [Tooltip("每项 = 一样物品 + 出现权重 + 一次抽到几个。空表 = 箱子永远是空的")]
    public ItemPoolEntry[] pool;

    [Tooltip("【保底】必出的物品（留空 = 不保底）。\n⚠️ 刻意**不写死成武器**：军火箱填一把枪、医疗箱填一个血包 —— 由你指定")]
    public ItemData guaranteedItem;
    [Tooltip("保底物品的数量范围（最少）")]
    public int guaranteedMin = 1;
    [Tooltip("保底物品的数量范围（最多）")]
    public int guaranteedMax = 1;

    [Header("刷几样（按权重随机 → 箱子不会每箱都塞满）")]
    [Tooltip("抽取次数表：每项 = 次数 + 权重。\n例：3 样 50% / 4 样 30% / 5 样 10% / 2 样 10%。\n留空 = 每箱都抽满（次数 = 格数）")]
    public RollChance[] rollTable;

    [Header("搜刮（2026-10-26 新增）")]
    [Tooltip("每件物品的搜刮时长（秒）。\n⚠️ 0 = 用资源总表 GameAssets 的全局默认（默认 0.6）；填 >0 = 这个箱子统一按它来。\n（个别物品要更久 → 在物品卡 ItemData.searchSeconds 上单独覆盖）")]
    [Min(0f)] public float searchSecondsPerItem = 0f;

    /// <summary>按权重随机决定"抽几次"。</summary>
    public int PickRollCount()
    {
        if (rollTable == null || rollTable.Length == 0) return Mathf.Max(1, slotCount);

        float total = 0f;
        for (int i = 0; i < rollTable.Length; i++)
            if (rollTable[i] != null) total += Mathf.Max(0f, rollTable[i].weight);
        if (total <= 0f) return Mathf.Max(1, slotCount);

        float roll = Random.value * total;
        for (int i = 0; i < rollTable.Length; i++)
        {
            if (rollTable[i] == null) continue;
            roll -= Mathf.Max(0f, rollTable[i].weight);
            if (roll <= 0f) return Mathf.Max(0, rollTable[i].count);
        }
        return Mathf.Max(1, slotCount);   // 浮点误差兜底
    }

    /// <summary>按权重随机抽一条物品池（池空 / 全无效 → null）。</summary>
    public ItemPoolEntry PickEntry()
    {
        if (pool == null || pool.Length == 0) return null;

        float total = 0f;
        for (int i = 0; i < pool.Length; i++)
            if (pool[i] != null && pool[i].item != null) total += Mathf.Max(0f, pool[i].weight);
        if (total <= 0f) return null;

        float roll = Random.value * total;
        for (int i = 0; i < pool.Length; i++)
        {
            var e = pool[i];
            if (e == null || e.item == null) continue;
            roll -= Mathf.Max(0f, e.weight);
            if (roll <= 0f) return e;
        }
        return null;
    }

    /// <summary>数值兜底（仅编辑器生效）：只用 if 判断，避免把合法值也标成"已修改"而脏化资源。</summary>
    void OnValidate()
    {
        if (slotCount < 1) slotCount = 1;

        if (pool != null)
            for (int i = 0; i < pool.Length; i++)
            {
                var e = pool[i];
                if (e == null) continue;
                if (e.weight < 0f) e.weight = 0f;
                if (e.minCount < 1) e.minCount = 1;
                if (e.maxCount < e.minCount) e.maxCount = e.minCount;
            }

        if (guaranteedMin < 1) guaranteedMin = 1;
        if (guaranteedMax < guaranteedMin) guaranteedMax = guaranteedMin;

        if (rollTable != null)
            for (int i = 0; i < rollTable.Length; i++)
            {
                var r = rollTable[i];
                if (r == null) continue;
                if (r.count < 0) r.count = 0;
                if (r.weight < 0f) r.weight = 0f;
            }
    }
}
