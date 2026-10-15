using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 商店：挂在**你自己在场景里摆好的**物体上（用法与 `Chest` 完全一致）。
///
/// 用法（零代码）：
///   1. 在地图里摆一个 cube（或以后换成正式的柜台 / 建筑模型），保留它的 Collider；
///   2. 把本脚本挂上去；
///   3. Inspector 填「商店名」+ 选一个「商店配方」（右键 `Create > 游戏 > 商店配方`）；
///   4. 勾「是实体」= 挡路 / 挡子弹 / 挡视线；不勾 = 能穿过去（只启用 / 禁用 Collider，**不删你的 Collider**）。
///
/// 交互：靠近 → 气泡「商店名 + F」→ 按 F → **两个面板一起打开**：
///   右侧 = 货架（点击买入 1 件）、左侧 = 背包库存（点击卖出 1 件）；**Tab 关闭**（两个一起关）。
///
/// ⚠️ 交易规则**只在本类实现一处**（校验顺序：售罄 → 金币 → 背包空间）——
///    UI（`ShopPanel` / `ShopInventoryPanel`）只负责画格子 + 调这里 + 显示失败原因，**不要自己判断业务**。
/// ⚠️ 库存与补货计时是**运行时状态**（不序列化）：死亡重开 = 场景重载 → 自动恢复满货，不需要任何重置代码。
/// </summary>
public class Shop : MonoBehaviour, IInteractable
{
    /// <summary>场上所有商店（调试 / 静态清理用）。注册必须配注销（见 OnDestroy）。</summary>
    public static readonly List<Shop> All = new List<Shop>();

    [Header("商店")]
    [Tooltip("气泡与面板标题里的名字（例：军火商 / 杂货铺 / 黑市）")]
    public string promptLabel = "商店";
    [Tooltip("这家店卖什么（右键 Create > 游戏 > 商店配方 创建）。留空 → 空货架（编辑器里会告警）")]
    public ShopConfig config;

    [Header("交互")]
    [Tooltip("气泡里显示的按键")]
    public string interactKey = "F";
    [Tooltip("多远之内能交互（米）")]
    [Min(0.1f)] public float interactRange = 1.6f;

    [Header("行为开关")]
    [Tooltip("勾上 = 商店是实体：挡路、挡子弹、挡视线（和场景里的障碍物一样）；\n不勾 = 纯装饰：玩家和子弹都能穿过去。\n（实现 = 启用/禁用 Collider + 切换 Layer，**不会删掉你的 Collider**，随时勾回来）")]
    public bool solid = true;

    // ---- 运行时状态（不序列化：每局重新满货）----
    int[] _stock;          // 每个条目的当前库存；maxStock ≤ 0 的条目视为无限、不参与扣减
    float _restockTimer;   // 整店补货计时（开局起算；暂停时停表）

    // ==================== 查询（UI 只读这些） ====================

    public string ShopName => string.IsNullOrEmpty(promptLabel) ? "商店" : promptLabel;

    /// <summary>货架格数（面板排版用）。配方缺失时至少 1。</summary>
    public int SlotCount => config != null ? Mathf.Max(1, config.slotCount) : 1;

    /// <summary>货架上有几项条目。</summary>
    public int EntryCount => config != null ? config.EntryCount : 0;

    /// <summary>取第 i 项条目（越界 / 无配方 → null）。</summary>
    public ShopEntry EntryAt(int i) => config != null ? config.EntryAt(i) : null;

    /// <summary>第 i 格是不是无限库存（配方里 maxStock ≤ 0）。</summary>
    public bool IsUnlimited(int i)
    {
        var e = EntryAt(i);
        return e == null || e.maxStock <= 0;
    }

    /// <summary>第 i 格还剩几件；**无限库存返回 -1**（调用方显示「∞」）。</summary>
    public int StockAt(int i)
    {
        if (IsUnlimited(i)) return -1;
        EnsureStock();
        return (i >= 0 && _stock != null && i < _stock.Length) ? _stock[i] : 0;
    }

    /// <summary>这件物品的玩家买入价（-1 = 本店不卖它）。</summary>
    public int BuyPriceOf(ItemData def) => config != null ? config.BuyPriceOf(def) : -1;

    /// <summary>这件物品的玩家卖出价（0 = 本店不收它）。</summary>
    public int SellPriceOf(ItemData def) => config != null ? config.SellPriceOf(def) : 0;

    // ==================== IInteractable ====================

    public Transform InteractAnchor => transform;
    public string InteractKey => string.IsNullOrEmpty(interactKey) ? "F" : interactKey;
    public float InteractRange => interactRange;

    /// <summary>气泡里的名字。配方没配 → 返回 null（焦点会跳过它，走过去没反应 —— 与"空箱不提示"同一套约定）。</summary>
    public string InteractLabel => config != null ? ShopName : null;

    public void Interact(PlayerController player)
    {
        if (player == null) return;
        EnsureStock();          // 编辑器里改过配方（条目数变了）→ 这里会重建；否则什么都不做
        ShopPanel.Open(this);
    }

    // ==================== 生命周期 ====================

    void Awake()
    {
        All.Add(this);
        InteractFocus.Register(this);
        InitStock();
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

    /// <summary>整店补货：从开局起算，每 `restockSeconds` 秒把**没满的**条目补一次。
    /// ⚠️ 必须显式检查暂停（打开面板时 timeScale=0，但 Update 照常执行）——
    ///    与"波次倒计时 / 血包重生 / 僵局计时"同一套约定，不能只靠 Time.deltaTime 顺带实现。</summary>
    void Update()
    {
        if (GameState.Paused || GameState.GameOver) return;
        if (config == null || config.restockSeconds <= 0f) return;

        _restockTimer += Time.deltaTime;
        if (_restockTimer < config.restockSeconds) return;

        _restockTimer -= config.restockSeconds;   // 用减法而不是归零：间隔很小时不会越漂越多
        Restock();
    }

    // ==================== 库存（运行时） ====================

    void InitStock()
    {
        int n = config != null ? config.EntryCount : 0;
        _stock = new int[n];
        for (int i = 0; i < n; i++)
        {
            var e = config.EntryAt(i);
            _stock[i] = e != null ? Mathf.Max(0, e.maxStock) : 0;   // 开局满货
        }
        _restockTimer = 0f;
    }

    /// <summary>库存数组与配方条目数不符时重建（首次打开 / 编辑器里改过配方）。</summary>
    void EnsureStock()
    {
        int n = config != null ? config.EntryCount : 0;
        if (_stock != null && _stock.Length == n) return;
        InitStock();
    }

    void Restock()
    {
        if (config == null) return;
        int n = config.EntryCount;
        for (int i = 0; i < n && i < _stock.Length; i++)
        {
            var e = config.EntryAt(i);
            if (e == null || e.maxStock <= 0) continue;    // 无限库存不用补
            if (_stock[i] >= e.maxStock) continue;
            int add = e.restockAmount <= 0 ? e.maxStock : e.restockAmount;
            _stock[i] = Mathf.Min(e.maxStock, _stock[i] + add);
        }
    }

    /// <summary>[DEBUG-ONLY] 调试面板用：**立刻整店补货**（等价于把 `_restockTimer` 一次走完），
    /// 并把补货计时归零（下次自然补货从整段间隔重新计）。
    /// 中性语义：跳过等待。发布前清理见 `Assets/Debug/README.md` 登记表。</summary>
    public void ForceRestock()
    {
        if (config == null) return;
        EnsureStock();
        Restock();
        _restockTimer = 0f;
    }

    /// <summary>本店还差多少件才满货（调试面板显示用；无限库存的格不计入）。</summary>
    public int MissingToFull()
    {
        if (config == null) return 0;
        EnsureStock();
        int n = config.EntryCount, miss = 0;
        for (int i = 0; i < n && i < _stock.Length; i++)
        {
            var e = config.EntryAt(i);
            if (e == null || e.maxStock <= 0) continue;
            miss += Mathf.Max(0, e.maxStock - _stock[i]);
        }
        return miss;
    }

    // ==================== 买卖（规则唯一实现） ====================

    /// <summary>能不能买这一格（**不改任何状态**，只返回原因 —— 供 UI 预判 / 以后做按钮灰化）。</summary>
    public bool CanBuy(int i, PlayerController pc, out string reason)
    {
        reason = null;
        var e = EntryAt(i);
        if (e == null || e.item == null) { reason = "这格没有东西"; return false; }
        if (!IsUnlimited(i) && StockAt(i) <= 0) { reason = "已售罄"; return false; }
        if (GameState.Gold < Mathf.Max(0, e.buyPrice)) { reason = "金币不足"; return false; }

        var bp = pc != null ? pc.backpack : null;
        if (bp == null) { reason = "背包不可用"; return false; }
        if (!bp.CanAdd(e.item)) { reason = "背包已满"; return false; }
        return true;
    }

    /// <summary>买入第 i 格 1 件。成功 = true；失败时 <paramref name="reason"/> 是给玩家看的原因。
    /// 结算顺序：预检（售罄 / 金币 / 背包空间）→ 扣钱 → 放入 → 扣库存；放入失败会**退钱回滚**。</summary>
    public bool Buy(int i, PlayerController pc, out string reason)
    {
        EnsureStock();      // 账本与配方同步（防御：编辑器里改过条目数也不会越界）
        if (!CanBuy(i, pc, out reason)) return false;

        var e = EntryAt(i);
        var bp = pc.backpack;
        int price = Mathf.Max(0, e.buyPrice);

        GameState.Gold -= price;
        // ⚠️ 买回来的东西走 `AddItems`（**只进库存**）—— 不自动占快捷槽，避免抢走玩家手上的枪（用户 2026-09-25 定）
        if (bp.AddItems(e.item, 1) <= 0)
        {
            GameState.Gold += price;      // 兜底回滚（CanBuy 已预检，正常走不到这里）
            reason = "背包已满";
            return false;
        }

        if (!IsUnlimited(i)) _stock[i] = Mathf.Max(0, _stock[i] - 1);
        Sfx.Buy();
        return true;
    }

    /// <summary>卖出**背包库存第 invIndex 格**的 1 件（快捷槽 / 手上的枪不参与，用户 2026-09-25 定）。
    /// 表内物品卖回货架（库存 +1，到上限为止）；表外物品只给钱、不上架。</summary>
    public bool Sell(int invIndex, PlayerController pc, out string reason)
    {
        reason = null;
        // ⚠️ 必须放在最前面：下面的"回填货架"要写 `_stock[ei]`，而账本长度可能和配方不同步
        //   （编辑器里在 Play 中加/删条目）→ 不先重建就是越界异常，且异常点在"已扣物、已加钱"之后
        EnsureStock();
        var bp = pc != null ? pc.backpack : null;
        if (bp == null) { reason = "背包不可用"; return false; }
        if (invIndex < 0 || invIndex >= bp.inventory.Count) { reason = "这格没有东西"; return false; }

        var stack = bp.inventory[invIndex];
        if (stack == null || stack.def == null || stack.IsEmpty) { reason = "这格没有东西"; return false; }

        var def = stack.def;
        int price = SellPriceOf(def);
        if (price <= 0) { reason = "本店不收这个"; return false; }

        // ⚠️ 先取出再给钱：取出失败（理论上不会）就不会凭空产钱
        if (bp.TakeFromInventory(invIndex, 1) <= 0) { reason = "这格没有东西"; return false; }
        GameState.Gold += price;

        // 表内物品 → 收回货架（到上限为止；无限库存不累加）
        int ei = config != null ? config.IndexOf(def) : -1;
        if (ei >= 0 && !IsUnlimited(ei) && ei < _stock.Length)
        {
            var e = config.EntryAt(ei);
            if (e != null) _stock[ei] = Mathf.Min(Mathf.Max(0, e.maxStock), _stock[ei] + 1);
        }

        Sfx.Sell();
        return true;
    }

    // ==================== 实体开关 / 自检 / 静态清理 ====================

    /// <summary>"是否实体"开关的落地（照抄 `Chest.ApplySolid`，两处语义必须一致）：
    ///  · 实体   → Collider 启用 + `Obstacle` 层（挡路 / 挡子弹 / 挡视线，与场景里的 Crate 同一套规则）
    ///  · 非实体 → Collider 禁用 + `Pickup` 层（子弹 / 视线 / 绕障的掩码里都不含它 → 全忽略）</summary>
    void ApplySolid()
    {
        var cols = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++) cols[i].enabled = solid;

        // 统一实现（含"层没定义就原样不动"的守卫；2026-10-15 收敛）
        Layers.SetRecursively(gameObject, solid ? Layers.Obstacle : Layers.Pickup);
    }

#if UNITY_EDITOR
    /// <summary>自检（仅编辑器）：把"会静默变怪"的配置在 Console 里说清楚 —— 与 Chest / HUD 的自检同一套约定。</summary>
    void SelfCheck()
    {
        if (config == null)
            Debug.LogWarning($"[Shop] 「{name}」没有配「商店配方」→ 走过去不会有反应（气泡里也不会出现这家店）。", this);
        if (config != null && config.EntryCount == 0)
            Debug.LogWarning($"[Shop] 「{name}」的配方「{config.name}」条目是空的 → 打开会是空货架。", this);
        if (GetComponentInChildren<Renderer>() == null)
            Debug.LogWarning($"[Shop] 「{name}」身上没有渲染器 → 场景里看不到它。", this);
        if (solid && GetComponentInChildren<Collider>() == null)
            Debug.LogWarning($"[Shop] 「{name}」勾了「是实体」但身上没有 Collider → 玩家和子弹照样会穿过去。", this);
    }
#endif

    /// <summary>静态状态清理（供 `StaticReset` 调用）。</summary>
    internal static void ResetStatics()
    {
        All.Clear();
    }
}
