using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 箱子：挂在**你自己在场景里摆好的**立方体 / 宝箱模型上。
///
/// 用法（零代码）：
///   1. 在地图里摆一个 cube（位置固定、看得见、好调试）；保留它自带的 BoxCollider；
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
///
/// ⭐ **槽位是固定的**（2026-10-26 改）：拿掉一格的东西后，**后面的物品不会前移**，空出来的格子
///    就一直是空的。为什么：搜刮过程中"物品在哪个格子"要能被玩家记住（我搜到一半，第 6 格出了把枪，
///    回头去拿时它还在第 6 格）；紧凑列表的 `RemoveAt` 会让所有格子整体前移，视觉上"东西会跑"。
///    ⇒ **不要退回 `List.RemoveAt` 那套写法**（`GetSlot(i)` 可能返回 null，调用方要判空）。
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
    /// <summary>固定槽位（长度 = <see cref="SlotCount"/>；空格 = null）。⚠️ 拿走后**不前移**。</summary>
    ItemStack[] _slots;
    /// <summary>每个槽位是否已经"搜出来"（运行时；关掉再开保持）。</summary>
    bool[] _revealed;
    bool _rolled;   // 是否已经抽过内容（第一次打开时抽一次）

    /// <summary>箱子是不是空的。</summary>
    public bool IsEmpty
    {
        get
        {
            if (_slots == null) return true;
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i] != null && !_slots[i].IsEmpty) return false;
            return true;
        }
    }

    /// <summary>显示格数（面板排版用）。配方缺失时至少 1。</summary>
    public int SlotCount => config != null ? Mathf.Max(1, config.slotCount) : 1;

    /// <summary>这个槽位的东西是否已经搜出来（空格 / 越界一律 false）。</summary>
    public bool IsRevealed(int i) => i >= 0 && _revealed != null && i < _revealed.Length && _revealed[i];

    /// <summary>还有没有"有东西、但还没搜出来"的槽位（面板据此决定要不要播搜刮）。</summary>
    public bool HasUnrevealedLoot
    {
        get
        {
            if (_slots == null) return false;
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i] != null && !_slots[i].IsEmpty && !IsRevealed(i)) return true;
            return false;
        }
    }

    /// <summary>标记某一格"已搜出来"（由面板在搜刮动画里调用）。</summary>
    public void RevealSlot(int i)
    {
        if (i < 0 || _revealed == null || i >= _revealed.Length) return;
        _revealed[i] = true;
    }

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

    /// <summary>取第 i 格（越界返回 null）。⚠️ 空格位返回 null，调用方必须判空。</summary>
    public ItemStack GetSlot(int i) => (i >= 0 && _slots != null && i < _slots.Length) ? _slots[i] : null;

    /// <summary>取走一部分（可堆叠物品没拿完时用）。
    /// ⚠️ **拿空后只把这一格置 null，不移除、不前移**（见类注释）。</summary>
    public void TakeFromSlot(int i, int n)
    {
        if (i < 0 || _slots == null || i >= _slots.Length) return;
        var s = _slots[i];
        if (s == null) return;
        s.Take(n);
        if (s.IsEmpty) { s.Clear(); _slots[i] = null; }
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

        // 统一实现（含"层没定义就原样不动"的守卫；2026-10-15 收敛）
        Layers.SetRecursively(gameObject, solid ? Layers.Obstacle : Layers.Pickup);
    }

    /// <summary>按配方抽内容（**只在第一次打开时执行一次**）。</summary>
    void Roll()
    {
        _rolled = true;
        int slots = SlotCount;
        _slots = new ItemStack[slots];
        _revealed = new bool[slots];
        if (config == null) return;

        // ① 保底：必出的那一样（可指定，**不写死成武器**）
        if (config.guaranteedItem != null)
            AddRolled(config.guaranteedItem, Random.Range(config.guaranteedMin, config.guaranteedMax + 1));

        // ② 按权重决定"抽几次"，逐次抽（可堆叠的并进同类格）
        int rolls = config.PickRollCount();
        for (int i = 0; i < rolls; i++)
        {
            var e = config.PickEntry();
            if (e == null) continue;
            AddRolled(e.item, Random.Range(e.minCount, e.maxCount + 1));
        }
    }

    /// <summary>把一次抽到的东西放进格子：可堆叠先进同类格，否则放进**第一个空槽**；槽位满了就丢弃（不溢出）。</summary>
    void AddRolled(ItemData def, int count)
    {
        if (def == null || count <= 0 || _slots == null) return;

        if (def.canStack)
            for (int i = 0; i < _slots.Length && count > 0; i++)
                if (_slots[i] != null && _slots[i].def != null && _slots[i].def.CanMergeWith(def))
                    count = _slots[i].Add(count);

        while (count > 0)
        {
            int idx = -1;
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i] == null) { idx = i; break; }
            if (idx < 0) return;   // 槽位满了 → 丢弃（与原逻辑一致）

            var s = new ItemStack();
            s.def = def;
            s.count = 0;
            count = s.Add(count);
            if (s.count > 0) _slots[idx] = s; else return;
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
