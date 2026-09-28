using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 商店面板（**屏幕右侧**的货架）：点击格子 = 买入 1 件。
///
/// 与左侧「库存面板」（<see cref="ShopInventoryPanel"/>）**绑成一个整体**：一起开、一起关 ——
/// 与"箱子面板 + 背包面板"完全同一套做法（含 `UIState` 记账、死亡自关、双方幂等守卫）。
///
/// 关于 Tab（用户 2026-09-25 定）：**商店打开期间 Tab = 关闭商店组**，且**不允许打开背包** ——
///   <see cref="HoldsTabKey"/> 就是给 `BackpackPanel` 让位用的（带"关闭那一帧"的帧号互斥，
///   否则按一下 Tab 会出现"商店关了、背包却打开了"，与交互键 F 的 `InteractConsumedFrame` 同一套思路）。
///
/// 布局：标题（商店名，左）+ 金币（右）；5 列网格 —— 格子 68×68（与背包一致），
///       **价格数字画在格子正下方**（用户 2026-09-25 定的"参考图"风格）、库存数字在格子右上角；
///       售罄 → 灰化 + 显示「补货中」。
///
/// ⚠️ 业务规则（能不能买、钱够不够）**只在 `Shop` 里实现一处** —— 本类只负责画 + 调 + 显示失败原因。
/// </summary>
public class ShopPanel : MonoBehaviour
{
    static ShopPanel _inst;
    static int _tabConsumedFrame = -1;

    /// <summary>唯一实例（可能为 null —— 还没被打开过）。</summary>
    public static ShopPanel Instance => _inst;

    /// <summary>Tab 键是否归商店管（`BackpackPanel` 据此让位）：
    ///  · 商店组开着 → 是（Tab = 关闭商店组）
    ///  · 本帧刚被商店消费掉（关闭那一帧）→ 是（防止背包面板在同一帧"接着"把背包打开）</summary>
    public static bool HoldsTabKey
    {
        get
        {
            if (_inst != null && _inst._opened) return true;
            return Time.frameCount == _tabConsumedFrame;
        }
    }

    // 布局常量（参考分辨率 1280×720，运行时由 CanvasScaler 等比缩放）
    const float PanelW = 400f;        // 与背包 / 库存面板同宽
    const float RightMargin = 16f;    // 面板距屏幕右边
    const float Pad = 16f;            // 面板内边距
    const float TitleH = 30f;         // 标题行高
    const float HeadGap = 10f;        // 标题与格子区之间
    const float GridInset = (PanelW - Pad * 2f - ShopUI.GridW) * 0.5f;   // 网格在内容区里水平居中
    const float ToastRowH = 26f;      // 面板底部"提示行"占的高度（失败原因显示在那）

    Canvas _canvas;
    RectTransform _root;
    Image _bg;
    Text _title;
    Text _gold;
    RectTransform _grid;
    readonly List<ShopUI.ShopCell> _cells = new List<ShopUI.ShopCell>();

    GameObject _tipGo;
    RectTransform _tipRect;
    Text _tipText;
    int _lastHoverIndex = -1;    // 上次显示 tooltip 的格子（没换格子就不重建字符串）
    Text _toast;                 // 面板内提示（失败原因；**不用 `HUD.Announce`** —— 面板打开时 HUD 是隐藏的）
    float _toastUntil;

    Shop _shop;
    bool _opened;
    int _openedFrame = -1;       // 打开的帧号（防止"开的那一帧又被关"，与箱子面板同一套守卫）

    /// <summary>面板当前是不是开着。</summary>
    public bool IsOpen => _opened;

    /// <summary>当前正在交易的商店（库存面板要用它算卖出价）。</summary>
    public Shop CurrentShop => _shop;

    void Awake()
    {
        // 单例防护（重复实例丢弃后出现的那个）
        if (_inst != null && _inst != this) { Destroy(gameObject); return; }
        _inst = this;
        BuildUI();
        _root.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (_inst == this) _inst = null;
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）：清掉单例与 Tab 帧号 ——
    /// 漏了帧号的话，理论上会出现"重开一局后第一帧的 Tab 被上一局占用"（概率极低，但没必要留着）。</summary>
    internal static void ResetStatics()
    {
        _inst = null;
        _tabConsumedFrame = -1;
    }

    void Update()
    {
        if (!_opened) return;

        // 死亡时强制关闭（避免重开一局卡在暂停；同时把光标还给结算面板）
        if (GameState.GameOver) { Close(); return; }

        // 悬停描述：放在最前面 —— 后面的"同帧守卫"会提前 return，别把它一起挡掉
        UpdateTooltip();

        // 提示行自动消失（⚠️ 用 unscaledTime：面板打开时 timeScale=0，用 Time.time 会永远挂着不消失）
        if (_toast != null && _toast.gameObject.activeSelf && Time.unscaledTime >= _toastUntil)
            _toast.gameObject.SetActive(false);

        if (Time.frameCount == _openedFrame) return;      // 打开的那一帧不处理关闭键
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            _tabConsumedFrame = Time.frameCount;          // 让背包面板在同一帧让位（见 HoldsTabKey）
            Close();
        }
    }

    // ==================== 打开 / 关闭 ====================

    /// <summary>打开商店（会自动把左侧库存面板也一起打开）。没有实例时按需创建 —— 不需要往场景 / prefab 里预挂。</summary>
    public static void Open(Shop shop)
    {
        if (shop == null) return;
        if (_inst == null)
        {
            var go = new GameObject("ShopPanel");
            go.AddComponent<ShopPanel>();       // AddComponent 会同步触发 Awake（BuildUI + 设 _inst）
        }
        if (_inst != null) _inst.OpenInternal(shop);
    }

    void OpenInternal(Shop shop)
    {
        _shop = shop;
        if (_opened)   // 已经开着（又开了另一个商店）→ 只换内容
        {
            // ⚠️ 必须把新商店也告诉左侧面板（`Open` 里才会更新它的 `_shop` 引用）——
            //    否则左侧仍按**旧店**的价格结算、卖回的东西也回填到旧店
            ShopInventoryPanel.Open(shop);
            Refresh();
            if (ShopInventoryPanel.Instance != null) ShopInventoryPanel.Instance.RefreshIfOpen();
            return;
        }

        _opened = true;
        _openedFrame = Time.frameCount;
        UIState.Push();                         // 暂停 + 隐藏整块 HUD + 隐藏准星（**唯一出口**）
        ShopInventoryPanel.Open(shop);          // 左侧库存面板：一起开
        _root.gameObject.SetActive(true);
        Sfx.OpenInventory();
        Refresh();
    }

    /// <summary>关闭（也会把库存面板一起关掉）。幂等：重复调用无副作用。</summary>
    public void Close()
    {
        if (!_opened) return;
        _opened = false;
        // 收起悬停提示：根节点虽被隐藏，但 tooltip 自己的 activeSelf 还留着 true，
        // 下次打开会在 Update 跑到之前闪一帧在旧位置（与箱子面板同一处理）
        if (_tipGo != null) _tipGo.SetActive(false);
        _root.gameObject.SetActive(false);
        Sfx.CloseInventory();
        UIState.Pop();

        // ⚠️ 记下"这一帧的 F 已经用掉了"：关闭键与交互键同为 F，而交互焦点的执行顺序不确定 ——
        //    不记这一笔，就会出现"按下 F 关店的同一帧又被焦点重新打开"（与箱子面板同一处理）。
        GameState.InteractConsumedFrame = Time.frameCount;

        // 一起关（对方内部也有幂等守卫 → 不会递归打转）
        if (ShopInventoryPanel.Instance != null) ShopInventoryPanel.Instance.Close();
        _shop = null;
    }

    /// <summary>外部改了数据（卖出成功 / 背包变化）后的刷新入口；面板没开时什么都不做
    /// （打开时 `Open` 本来就会 Refresh，关着的时候没必要重建 UI 节点）。</summary>
    public void RefreshIfOpen()
    {
        if (_opened) Refresh();
    }

    // ==================== 刷新 ====================

    void Refresh()
    {
        if (_grid == null || _shop == null) return;

        ShopUI.SetGold(_gold, GameState.Gold);
        _title.text = _shop.ShopName;

        int slots = _shop.SlotCount;
        int rows = ShopUI.RowsFor(slots);
        float gridH = ShopUI.GridHeight(rows);
        float h = Pad + TitleH + HeadGap + gridH + Pad + ToastRowH;

        _root.sizeDelta = new Vector2(PanelW, h);
        _bg.rectTransform.sizeDelta = new Vector2(PanelW, h);
        _grid.sizeDelta = new Vector2(ShopUI.GridW, gridH);

        // 格子清空重建（数量少，与箱子面板 / 背包库存格同一套做法）
        for (int i = _grid.childCount - 1; i >= 0; i--) Destroy(_grid.GetChild(i).gameObject);
        _cells.Clear();
        _lastHoverIndex = -1;     // ⚠️ 必须清：格子重建后同一个格号可能换成**另一样东西**，不清就会显示旧 tooltip

        for (int i = 0; i < slots; i++)
        {
            int row = i / ShopUI.Cols, col = i % ShopUI.Cols;
            var e = _shop.EntryAt(i);
            var cell = ShopUI.CreateCell(_grid, i, row, col);
            _cells.Add(cell);

            if (e == null || e.item == null)
            {
                cell.price.text = "";      // 空格子：只留框，价格行留白
                continue;
            }

            var icon = ShopUI.FillIcon(cell.root, e.item);     // 图标与背包 / 箱子同源
            cell.stock = ShopUI.CreateStockText(cell.root);

            int stock = _shop.StockAt(i);
            bool unlimited = _shop.IsUnlimited(i);
            bool soldOut = !unlimited && stock <= 0;
            int buy = Mathf.Max(0, e.buyPrice);

            cell.stock.text = unlimited ? "∞" : Mathf.Max(0, stock).ToString();
            cell.price.text = soldOut ? "补货中" : buy.ToString();
            cell.price.color = soldOut ? ShopUI.DimColor : ShopUI.PriceColor;
            cell.stock.color = soldOut ? ShopUI.DimColor : Color.white;
            if (soldOut)
            {
                icon.color = ShopUI.Dim(icon.color);
                cell.bg.color = ShopUI.Dim(BackpackPanel.CellColor());
            }

            cell.bg.raycastTarget = true;
            var btn = cell.root.gameObject.AddComponent<Button>();
            btn.targetGraphic = cell.bg;
            int idx = i;
            btn.onClick.AddListener(() => OnCellClick(idx));
        }

        UIUtil.SetLayerRecursively(_canvas.gameObject, UIUtil.UILayer);
    }

    void OnCellClick(int i)
    {
        if (_shop == null) return;

        if (!_shop.Buy(i, PlayerController.Instance, out string reason))
        {
            // ⚠️ 失败原因必须画在**面板自己身上**：面板打开时 `UIState` 把整块 HUD 关掉了，
            //    `HUD.Announce` 写进去玩家根本看不见（而且它用 Time.time 计时，暂停时还会"关店后才出现"）
            ShowTip(reason);
            return;
        }

        Refresh();                            // 库存 / 金币变了 → 重画自己
        // 东西进了背包 → 左侧也要重画（与"箱子取物后通知背包"同款：面板是各管各的，必须显式通知）
        if (ShopInventoryPanel.Instance != null) ShopInventoryPanel.Instance.RefreshIfOpen();
    }

    /// <summary>面板内提示（失败原因）：显示 1.6 秒后自动消失。
    /// ⚠️ 计时用 `Time.unscaledTime`：面板打开时 timeScale=0，用 `Time.time` 会永远挂着不消失。</summary>
    void ShowTip(string msg)
    {
        if (_toast == null) return;
        _toast.text = msg;
        _toast.gameObject.SetActive(true);
        _toastUntil = Time.unscaledTime + 1.6f;
    }

    // ==================== 悬停描述（tooltip） ====================

    void UpdateTooltip()
    {
        if (_tipGo == null || _tipText == null || _shop == null) return;

        Vector2 mouse = VirtualCursor.MousePosition;

        // 命中判定用**矩形包含**（不走 EventSystem 射线）：tooltip 自己也是 UI，
        // 走射线会"挪到 tooltip 上 → 悬停丢失 → 闪掉 → 又命中"来回抖（背包 / 箱子同款做法）
        int hover = -1;
        for (int i = 0; i < _cells.Count; i++)
        {
            var e = _shop.EntryAt(i);
            if (e == null || e.item == null) continue;
            if (_cells[i].root != null && RectTransformUtility.RectangleContainsScreenPoint(_cells[i].root, mouse, null))
            { hover = i; break; }
        }

        if (hover >= 0)
        {
            if (hover != _lastHoverIndex)
            {
                _lastHoverIndex = hover;
                var e = _shop.EntryAt(hover);
                // 内容 = 价格行（金色）+ 物品信息（与背包 / 箱子**同一份** `ItemInfoString`）
                _tipText.text = ShopUI.TipContent(new ItemStack(e.item, 1),
                                                  Mathf.Max(0, e.buyPrice), _shop.SellPriceOf(e.item));
            }
            ShopUI.PlaceTooltip(_tipGo, _tipRect, _root, _canvas);
        }
        else
        {
            _lastHoverIndex = -1;
            ShopUI.HideTooltip(_tipGo);
        }
    }

    // ==================== 构 UI ====================

    void BuildUI()
    {
        // 与背包 / 箱子同一层（sortingOrder 200），但**独立画布**；
        // ⚠️ 必须低于 400（虚拟光标在最上层，否则开商店时看不到光标）
        _canvas = UIUtil.NewOverlayCanvas("ShopCanvas", 200);
        _canvas.gameObject.AddComponent<GraphicRaycaster>();   // 面板可点击（额外件：其余 UI 不需要）

        _root = new GameObject("Shop").AddComponent<RectTransform>();
        _root.SetParent(_canvas.transform, false);
        _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(1f, 0.5f);   // **右中**（与左侧库存面板对称）
        _root.anchoredPosition = new Vector2(-RightMargin, 0f);
        _root.sizeDelta = new Vector2(PanelW, 300f);           // 真实高度 Refresh 里按行数算

        // 面板底图（颜色 / 贴图与背包同源）
        _bg = ShopUI.NewImage(_root, "Bg", new Vector2(PanelW, 300f), Vector2.zero, BackpackPanel.PanelColor());
        _bg.sprite = BackpackPanel.PanelSprite();
        _bg.type = Image.Type.Sliced;
        _bg.raycastTarget = true;      // 拦住穿透点击

        // 标题（商店名，左） + 金币（右）
        // 标题宽 200（不是 240）：给右侧金币留出位置，两者不重叠（店名太长会往右溢出，起名别太长）
        _title = ShopUI.NewText(_root, "Title", new Vector2(200f, 26f), new Vector2(Pad, -6f), 18,
                                new Color(1f, 0.94f, 0.6f), TextAnchor.MiddleLeft);
        _title.fontStyle = FontStyle.Bold;

        _gold = ShopUI.NewText(_root, "Gold", new Vector2(160f, 26f), Vector2.zero, 15,
                               ShopUI.GoldColor, TextAnchor.MiddleRight);
        ShopUI.TopRight(_gold.rectTransform, new Vector2(-Pad, -6f));

        // 格子容器（锚在面板左上角；格子按"相对它左上角"的坐标排 —— 与背包库存格同一套算法）
        _grid = ShopUI.NewRect(_root, "Grid", new Vector2(ShopUI.GridW, 100f),
                               new Vector2(Pad + GridInset, -(Pad + TitleH + HeadGap)));

        // 提示行（失败原因）建在 tooltip **之前** → tooltip 始终画在它上层（两者可能同时出现）
        _toast = ShopUI.CreateToast(_root, PanelW - Pad * 2f);

        ShopUI.CreateTooltip(_root, out _tipGo, out _tipRect, out _tipText);

        UIUtil.SetLayerRecursively(_canvas.gameObject, UIUtil.UILayer);
    }
}
