using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 箱子：挂在**你自己在场景里摆好的**立方体 / 宝箱模型上。
///
/// 用法（零代码）：
///   1. 在地图里摆一个 cube（位置固定，看得见、好调试）；保留它自带的 BoxCollider；
///   2. 把本脚本挂上去；
///   3. Inspector 里填「箱子名」+ 选一个「箱子配方」（右键 `Create > 游戏 > 箱子配方`）；
///   4. 勾「是实体」= 像障碍物一样挡路/挡子弹；不勾 = 能穿过去（脚本只启用/禁用 Collider，**不会删你的 Collider**）。
///
/// 交互：靠近 → 气泡提示「箱子名 + F」→ 按 F → 右上角出现一组格子 + 左侧背包面板
/// （两个面板**绑成一个整体**：一起开、一起关，见 <see cref="ChestPanel"/>）。
///
/// ⚠️ **本脚本不生成任何外观**：你摆什么就是什么（方便调试，也方便以后换成正式宝箱模型）。
///    以后换模型 = 把场景里调好的这个 cube 拖进 Project 生成 Prefab，再用它替换其它箱子实例。
///
/// ⚠️ 内容**只在第一次打开时抽一次**，抽完锁住（关掉再开内容不变）—— 否则玩家能"关了再开"刷物品。
///
/// ⚠️ 与场景重载：死亡重开 = 重新加载场景 → 箱子被重新创建、自动回到"未开箱"状态，
///    所以**不需要写任何重置代码**（这也是"箱子做成场景物体"的一个好处）。
/// </summary>
public class Chest : MonoBehaviour, IInteractable
{
    /// <summary>场上所有箱子（WaveManager / 调试用）。注册必须配注销（见 OnDestroy）。</summary>
    public static readonly List<Chest> All = new List<Chest>();

    [Header("箱子")]
    [Tooltip("气泡里的名字（例：军火箱 / 医疗箱 / 补给箱）")]
    public string promptLabel = "箱子";
    [Tooltip("这个箱子刷什么（右键 Create > 游戏 > 箱子配方 创建）。留空 → 打开是空的（编辑器里会告警）")]
    public ChestConfig config;

    [Header("交互")]
    [Tooltip("气泡里显示的按键")]
    public string interactKey = "F";
    [Tooltip("多远之内能交互（米）")]
    [Min(0.1f)] public float interactRange = 1.6f;

    [Header("行为开关")]
    [Tooltip("勾上 = 箱子是实体：挡路、挡子弹、挡视线（和场景里的障碍物一样）；\n不勾 = 纯装饰：玩家和子弹都能穿过去。\n（实现 = 启用/禁用 Collider + 切换 Layer，**不会删掉你的 Collider**，随时勾回来）")]
    public bool solid = true;
    [Tooltip("勾上 = 把东西拿光之后箱子消失；不勾 = 留在原地变成空箱")]
    public bool disappearWhenEmpty;
    [Tooltip("空箱子走到旁边还提不提示气泡（不提示 = 走过去没反应，最干净）")]
    public bool promptWhenEmpty = true;

    // ---- 运行时状态（不序列化：每局重新抽）----
    readonly List<ItemStack> _contents = new List<ItemStack>();
    bool _rolled;   // 是否已经抽过内容（第一次打开时抽一次）

    /// <summary>箱子里的东西（只读；数量 ≤ 配方格数）。</summary>
    public IList<ItemStack> Contents => _contents;

    /// <summary>箱子是不是空的。</summary>
    public bool IsEmpty
    {
        get
        {
            for (int i = 0; i < _contents.Count; i++)
                if (_contents[i] != null && !_contents[i].IsEmpty) return false;
            return true;
        }
    }

    /// <summary>显示格数（面板排版用）。配方缺失时至少 1。</summary>
    public int SlotCount => config != null ? Mathf.Max(1, config.slotCount) : 1;

    // ==================== IInteractable ====================

    public Transform InteractAnchor => transform;
    public string InteractKey => string.IsNullOrEmpty(interactKey) ? "F" : interactKey;
    public float InteractRange => interactRange;

    /// <summary>气泡里的名字。已经开过且拿空了、又勾了"空了不提示" → 返回 null（焦点会跳过它，走过去没反应）。</summary>
    public string InteractLabel
    {
        get
        {
            if (_rolled && IsEmpty && !promptWhenEmpty) return null;
            return promptLabel;
        }
    }

    public void Interact(PlayerController player)
    {
        if (player == null) return;
        if (!_rolled) Roll();          // 第一次打开才抽（抽完锁住）
        ChestPanel.Open(this);         // 打开右上角面板（会自动把左侧背包面板也一起打开）
    }

    // ==================== 取东西（由面板调用） ====================

    /// <summary>取第 i 格（越界返回 null）。</summary>
    public ItemStack GetSlot(int i) => (i >= 0 && i < _contents.Count) ? _contents[i] : null;

    /// <summary>取走一部分（可堆叠物品没拿完时用）。</summary>
    public void TakeFromSlot(int i, int n)
    {
        if (i < 0 || i >= _contents.Count) return;
        var s = _contents[i];
        if (s == null) { _contents.RemoveAt(i); return; }
        s.Take(n);
        if (s.IsEmpty) { s.Clear(); _contents.RemoveAt(i); }
        AfterLoot();
    }

    void AfterLoot()
    {
        if (disappearWhenEmpty && IsEmpty) Destroy(gameObject);
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）。</summary>
    internal static void ResetStatics()
    {
        All.Clear();
    }

    // ==================== 生命周期 ====================

    void Awake()
    {
        All.Add(this);
        InteractFocus.Register(this);
        ApplySolid();
#if UNITY_EDITOR
        SelfCheck();
#endif
    }

    void OnDestroy()
    {
        All.Remove(this);
        InteractFocus.Unregister(this);   // 注册配注销（否则场景重载后焦点表里留死引用）
    }

    /// <summary>"是否实体"开关的落地：**启用/禁用** Collider + 切 Layer（不删 Collider，随时能勾回来）。
    ///  · 实体   → Collider 启用 + `Obstacle` 层（挡路 / 挡子弹 / 挡视线，与场景里的 Crate 同一套规则）
    ///  · 非实体 → Collider 禁用 + `Pickup` 层（子弹 / 视线 / 绕障的掩码里都不含它 → 全忽略）</summary>
    void ApplySolid()
    {
        var cols = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++) cols[i].enabled = solid;

        int layer = solid ? Layers.Obstacle : Layers.Pickup;
        if (layer < 0) return;   // 工程里没定义该层 → 保持原样（不要静默改成"射线打不到"）
        var ts = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < ts.Length; i++) ts[i].gameObject.layer = layer;
    }

    /// <summary>按配方抽内容（**只在第一次打开时执行一次**）。</summary>
    void Roll()
    {
        _rolled = true;
        _contents.Clear();
        if (config == null) return;

        int slots = Mathf.Max(1, config.slotCount);

        // ① 保底：必出的那一样（可指定，**不写死成武器**）
        if (config.guaranteedItem != null)
            AddRolled(config.guaranteedItem, Random.Range(config.guaranteedMin, config.guaranteedMax + 1), slots);

        // ② 按权重决定"抽几次"，逐次抽（可堆叠的并进同类格）
        int rolls = config.PickRollCount();
        for (int i = 0; i < rolls; i++)
        {
            var e = config.PickEntry();
            if (e == null) continue;
            AddRolled(e.item, Random.Range(e.minCount, e.maxCount + 1), slots);
        }
    }

    /// <summary>把一次抽到的东西放进格子：可堆叠先进同类格，否则开新格；**超过格数的部分直接丢弃**（不会溢出）。</summary>
    void AddRolled(ItemData def, int count, int slots)
    {
        if (def == null || count <= 0) return;

        if (def.canStack)
            for (int i = 0; i < _contents.Count && count > 0; i++)
                if (_contents[i] != null && _contents[i].def != null && _contents[i].def.CanMergeWith(def))
                    count = _contents[i].Add(count);

        while (count > 0 && _contents.Count < slots)
        {
            var s = new ItemStack();
            s.def = def;
            s.count = 0;
            count = s.Add(count);
            if (s.count > 0) _contents.Add(s); else break;
        }
    }

#if UNITY_EDITOR
    /// <summary>自检（仅编辑器）：把"会静默变怪"的配置在 Console 里说清楚 —— 与 HUD / CameraRig 的自检同一套约定。</summary>
    void SelfCheck()
    {
        if (config == null)
            Debug.LogWarning($"[Chest] 「{name}」没有配「箱子配方」→ 打开会是空的。", this);
        if (GetComponentInChildren<Renderer>() == null)
            Debug.LogWarning($"[Chest] 「{name}」身上没有渲染器 → 场景里看不到它。", this);
        if (solid && GetComponentInChildren<Collider>() == null)
            Debug.LogWarning($"[Chest] 「{name}」勾了「是实体」但身上没有 Collider → 玩家和子弹照样会穿过去。", this);
    }
#endif
}
