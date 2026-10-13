using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 商店用的**背包库存面板**（屏幕左侧）。它是"打开商店时才出现"的那个左侧面板 ——
/// **只显示库存格**（不显示快捷武器槽 / 整理按钮 / 占用条 / 血包行），
/// 尺寸与格子排布跟背包面板一致（用户 2026-09-25 定：格子数 = 背包库存格子数）。
///
/// 用法：**不单独打开** —— 只由 `ShopPanel` 在开店时一起打开、关店时一起关闭（用户 2026-09-25 定）。
/// 本面板**不响应任何按键**：关闭统一走 `ShopPanel` 的 Tab（两个面板是同一个开关）。
///
/// 点击格子 = **卖出该格的 1 件**（只卖库存；快捷槽 / 手上的枪不参与）。
/// 格子下方显示该物品的**卖出价**（本店不收 → 灰字「不收」，格子图标一起灰化）。
///
/// ⚠️ 业务规则只在 `Shop` 里一处实现；本类只负责画 + 调 + 显示失败原因。
/// </summary>
public class ShopInventoryPanel : MonoBehaviour
{
    static ShopInventoryPanel _inst;

    /// <summary>唯一实例（可能为 null —— 还没被打开过）。</summary>
    public static ShopInventoryPanel Instance => _inst;

    // 布局常量（参考分辨率 1280×720）——宽度 / 内边距 / 格子尺寸与 `BackpackPanel` 一致
    const float PanelW = 400f;
    const float LeftMargin = 16f;     // 面板距屏幕左边（与背包面板相同）
    const float Pad = 16f;
    const float TitleH = 30f;
    const float HeadGap = 10f;
    const float GridInset = (PanelW - Pad * 2f - ShopUI.GridW) * 0.5f;
    const float ToastRowH = 26f;      // 面板底部"提示行"占的高度（失败原因显示在那）
    // 可视区行数上限（用户 2026-10-13 定）：**6 行** —— 容量 30（6 行）正好全见、无需滚动；
    // 容量 36+ 才出现滚动。6 行面板总高 ≈ 660px（商店格子下带价格行，行距 95 比背包的 74 高），
    // 720p 下上下各剩 ~30px —— 实测觉得挤就把这里改成 5。
    const int MaxViewRows = 6;

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
    int _lastHoverIndex = -1;
    Text _toast;                 // 面板内提示（失败原因；**不用 `HUD.Announce`** —— 面板打开时 HUD 是隐藏的）
    float _toastUntil;

    Shop _shop;
    bool _opened;

    /// <summary>库存网格滚动（与背包同一份共享实现，见 InvGridUI.ScrollableGrid）：
    /// 超出 6 行可视区的内容被 RectMask2D 裁掉，滚轮平滑露出。</summary>
    readonly ScrollableGrid _invScroll = new ScrollableGrid();

    /// <summary>面板当前是不是开着。</summary>
    public bool IsOpen => _opened;

    void Awake()
    {
        if (_inst != null && _inst != this) { Destroy(gameObject); return; }
        _inst = this;
        BuildUI();
        _root.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (_inst == this) _inst = null;
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）。</summary>
    internal static void ResetStatics()
    {
        _inst = null;
    }

    void Update()
    {
        if (!_opened) return;

        // 死亡时强制关闭（避免重开一局卡在暂停；与背包 / 箱子 / 商店面板同一约定）
        if (GameState.GameOver) { Close(); return; }

        // 库存滚动（机制在共享的 ScrollableGrid；内容没超出 6 行可视区时 Tick 自动 no-op）
        _invScroll.Tick();

        UpdateTooltip();

        // 提示行自动消失（⚠️ 用 unscaledTime：面板打开时 timeScale=0，用 Time.time 会永远挂着不消失）
        if (_toast != null && _toast.gameObject.activeSelf && Time.unscaledTime >= _toastUntil)
            _toast.gameObject.SetActive(false);

        // ⚠️ 本面板**刻意不处理任何按键**：商店组的关闭键（Tab）统一由 `ShopPanel` 负责 ——
        //    两个面板是同一个开关，各自处理会出现"关一次少一个 / 多关一次"的错位。
    }

    // ==================== 打开 / 关闭（只由 ShopPanel 调用） ====================

    public static void Open(Shop shop)
    {
        if (_inst == null)
        {
            var go = new GameObject("ShopInventoryPanel");
            go.AddComponent<ShopInventoryPanel>();   // AddComponent 同步触发 Awake
        }
        if (_inst != null) _inst.OpenInternal(shop);
    }

    void OpenInternal(Shop shop)
    {
        _shop = shop;
        if (_opened) { Refresh(); return; }          // 已开着（换了个商店）→ 只换内容

        _opened = true;
        UIState.Push();                              // 暂停 + 隐藏 HUD + 隐藏准星（与商店面板各记一次）
        _root.gameObject.SetActive(true);
        _invScroll.ResetToTop();                     // 每次打开都从顶开始（与背包一致）
        Refresh();
    }

    /// <summary>关闭（幂等）。也会反向关闭商店面板 —— 双方都有幂等守卫，不会递归打转。</summary>
    public void Close()
    {
        if (!_opened) return;
        _opened = false;
        if (_tipGo != null) _tipGo.SetActive(false);
        _root.gameObject.SetActive(false);
        UIState.Pop();

        if (ShopPanel.Instance != null && ShopPanel.Instance.IsOpen) ShopPanel.Instance.Close();
        _shop = null;
    }

    /// <summary>外部改了背包 / 商店数据的刷新入口（买入成功、卖出成功都会调）。</summary>
    public void RefreshIfOpen()
    {
        if (_opened) Refresh();
    }

    // ==================== 刷新 ====================

    void Refresh()
    {
        if (_grid == null) return;

        var pc = PlayerController.Instance;
        var bp = pc != null ? pc.backpack : null;
        int cap = bp != null ? bp.Capacity : 20;                       // 格子数 = 背包库存格子数（用户 2026-09-25 定）
        int used = bp != null ? bp.inventory.Count : 0;

        ShopUI.SetGold(_gold, GameState.Gold);
        _title.text = "背包   " + used + "/" + cap;

        int rows = ShopUI.RowsFor(cap);
        float contentH = ShopUI.GridHeight(rows);                       // 内容真实高（可超过可视区）
        float viewH = ShopUI.GridHeight(Mathf.Min(rows, MaxViewRows));  // 可视区高：≤6 行全见，再多靠滚轮
        float h = Pad + TitleH + HeadGap + viewH + Pad + ToastRowH;     // 面板高度只按**可视区**算（不再无限长高）

        _root.sizeDelta = new Vector2(PanelW, h);
        _bg.rectTransform.sizeDelta = new Vector2(PanelW, h);
        _grid.sizeDelta = new Vector2(ShopUI.GridW, viewH);   // 网格 = 固定可视区（RectMask2D 裁掉溢出行）

        for (int i = _grid.childCount - 1; i >= 0; i--) Destroy(_grid.GetChild(i).gameObject);
        _cells.Clear();
        _invScroll.ClearMovers();   // 格子全量重建 → 上一轮滚动登记作废
        _lastHoverIndex = -1;     // ⚠️ 必须清：格子重建后同一个格号可能换成**另一样东西**（卖掉一件后后面顶上来），不清就会显示旧 tooltip

        for (int i = 0; i < cap; i++)
        {
            int row = i / ShopUI.Cols, col = i % ShopUI.Cols;
            var stack = (bp != null && i < bp.inventory.Count) ? bp.inventory[i] : null;
            bool has = stack != null && stack.def != null && !stack.IsEmpty;

            var cell = ShopUI.CreateCell(_grid, i, row, col);
            _cells.Add(cell);
            // 滚动登记：格子 + 它下面的价格行都要一起滚（价格是网格的子节点，不是格子的 —— 漏了会"格动价不动"）
            _invScroll.AddMover(cell.root, cell.root.anchoredPosition);
            _invScroll.AddMover(cell.price.rectTransform, cell.price.rectTransform.anchoredPosition);

            if (!has)
            {
                cell.price.text = "";      // 空格子：只留框
                continue;
            }

            var icon = ShopUI.FillIcon(cell.root, stack.def);

            // 数量角标（叠了多个才显示）—— 与背包 / 箱子同一套做法；当前物品都不可堆叠（恒 1 → 不显示），
            // 等"可堆叠物品"上线即自动生效
            if (stack.count > 1)
            {
                var cnt = InvGridUI.CreateCountBadge(cell.root, ShopUI.CellW);   // 共享角标（与背包同一份实现）
                cnt.text = "×" + stack.count;
            }

            int sell = _shop != null ? _shop.SellPriceOf(stack.def) : 0;
            bool refuse = sell <= 0;       // 本店不收（表内 sellPrice=0 / 表外无兜底价）
            cell.price.text = refuse ? "不收" : sell.ToString();
            cell.price.color = refuse ? ShopUI.DimColor : ShopUI.PriceColor;
            if (refuse) icon.color = ShopUI.Dim(icon.color);

            cell.bg.raycastTarget = true;
            var btn = cell.root.gameObject.AddComponent<Button>();
            btn.targetGraphic = cell.bg;
            int idx = i;
            btn.onClick.AddListener(() => OnCellClick(idx));
        }

        _invScroll.Sync(contentH);   // 重算滚动范围并套用偏移（≤6 行时不可滚、偏移恒 0）

        UIUtil.SetLayerRecursively(_canvas.gameObject, UIUtil.UILayer);
    }

    void OnCellClick(int i)
    {
        if (_shop == null) return;

        if (!_shop.Sell(i, PlayerController.Instance, out string reason))
        {
            // ⚠️ 同 `ShopPanel`：失败原因画在面板自己身上（面板打开时 HUD 被 UIState 关掉了，玩家看不见公告）
            ShowTip(reason);
            return;
        }

        Refresh();                      // 背包少了一件
        // 表内物品卖回货架 → 库存 +1、金币变了 → 右侧要重画（显式通知，别指望面板自己知道）
        if (ShopPanel.Instance != null) ShopPanel.Instance.RefreshIfOpen();
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
        if (_tipGo == null || _tipText == null) return;

        var pc = PlayerController.Instance;
        var bp = pc != null ? pc.backpack : null;
        if (bp == null) { ShopUI.HideTooltip(_tipGo); return; }

        Vector2 mouse = VirtualCursor.MousePosition;
        int hover = -1;
        int count = Mathf.Min(_cells.Count, bp.inventory.Count);   // 平行数组防越界（与背包同一道守卫）
        for (int i = 0; i < count; i++)
        {
            var stack = bp.inventory[i];
            if (stack == null || stack.def == null || stack.IsEmpty) continue;
            if (_cells[i].root != null && RectTransformUtility.RectangleContainsScreenPoint(_cells[i].root, mouse, null))
            { hover = i; break; }
        }

        if (hover >= 0)
        {
            if (hover != _lastHoverIndex)
            {
                _lastHoverIndex = hover;
                var stack = bp.inventory[hover];
                int sell = _shop != null ? _shop.SellPriceOf(stack.def) : 0;
                // 库存面板只涉及"卖出" → 买入价传 -1
                _tipText.text = ShopUI.TipContent(stack, -1, sell);
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
        _canvas = UIUtil.NewOverlayCanvas("ShopInventoryCanvas", 200);
        _canvas.gameObject.AddComponent<GraphicRaycaster>();

        _root = new GameObject("ShopInventory").AddComponent<RectTransform>();
        _root.SetParent(_canvas.transform, false);
        _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0f, 0.5f);   // **左中**（与背包面板一致）
        _root.anchoredPosition = new Vector2(LeftMargin, 0f);
        _root.sizeDelta = new Vector2(PanelW, 300f);

        _bg = ShopUI.NewImage(_root, "Bg", new Vector2(PanelW, 300f), Vector2.zero, BackpackPanel.PanelColor());
        _bg.sprite = BackpackPanel.PanelSprite();
        _bg.type = Image.Type.Sliced;
        _bg.raycastTarget = true;

        _title = ShopUI.NewText(_root, "Title", new Vector2(200f, 26f), new Vector2(Pad, -6f), 18,
                                new Color(1f, 0.94f, 0.6f), TextAnchor.MiddleLeft);
        _title.fontStyle = FontStyle.Bold;

        _gold = ShopUI.NewText(_root, "Gold", new Vector2(160f, 26f), Vector2.zero, 15,
                               ShopUI.GoldColor, TextAnchor.MiddleRight);
        ShopUI.TopRight(_gold.rectTransform, new Vector2(-Pad, -6f));

        _grid = ShopUI.NewRect(_root, "Grid", new Vector2(ShopUI.GridW, 100f),
                               new Vector2(Pad + GridInset, -(Pad + TitleH + HeadGap)));
        _invScroll.Attach(_grid);   // 挂裁剪掩码（可视高 Refresh 时实时读，不在这里记）

        // 提示行（失败原因）建在 tooltip **之前** → tooltip 始终画在它上层（两者可能同时出现）
        _toast = ShopUI.CreateToast(_root, PanelW - Pad * 2f);

        ShopUI.CreateTooltip(_root, out _tipGo, out _tipRect, out _tipText);

        UIUtil.SetLayerRecursively(_canvas.gameObject, UIUtil.UILayer);
    }
}
