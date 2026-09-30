using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 商店两个面板（`ShopPanel` 右侧货架 / `ShopInventoryPanel` 左侧库存）的**共用小工具**：
/// 布局常量、建节点、建格子、建 tooltip、金币格式 —— 只留一份实现。
///
/// 为什么独立成一个类：两个面板都要画"一样的格子 + 一样的悬停提示"，
/// 各写一份必然出现"货架上的 tooltip 和库存里不一样"这类问题（项目里已有过"两处算法不一致"的教训）。
///
/// 颜色 / 贴图 / 字体**全部复用 `BackpackPanel` 的 public static 访问器与 `UIUtil`** →
/// 与背包 / 箱子 / 快捷栏永远一致（改 `GameAssets` 的「背包」组，四处一起变）。
///
/// 格子尺寸的约定（用户 2026-09-25 定）：**格子本身 68×68（与背包一致），价格数字画在格子正下方**
/// （所以行间距 = 格子高 + 价格行高 + 行间隙；想调大小改下面这组常量，两个面板一起变）。
/// </summary>
internal static class ShopUI
{
    // ---- 布局常量（参考分辨率 1280×720，运行时由 CanvasScaler 等比缩放）----
    public const float CellW = 68f, CellH = 68f, CellGap = 6f;
    public const float PriceH = 16f;     // 格子下方那一行「价格」
    public const float PriceGap = 3f;    // 格子与价格之间
    public const float RowGap = 8f;      // 行与行之间
    public const int Cols = 5;           // 每行 5 格（与背包的 5×4 一致）
    public const float RowUnit = CellH + PriceGap + PriceH;   // 一行「格子 + 价格」的总高
    public const int IconSize = 42;

    public static readonly Color PriceColor = new Color(1f, 0.84f, 0.45f);   // 价格（金币色）
    public static readonly Color DimColor = new Color(0.48f, 0.52f, 0.62f);  // 售罄 / 不收（灰）
    public static readonly Color ToastColor = new Color(1f, 0.62f, 0.5f);    // 面板内提示（失败原因）

    /// <summary>金币色 = `BackpackPanel.GoldColor`（共享色，只留一份）。</summary>
    public static Color GoldColor => BackpackPanel.GoldColor;

    /// <summary>5 列格子的总宽（两个面板的网格宽一致）。</summary>
    public const float GridW = Cols * CellW + (Cols - 1) * CellGap;

    // tooltip 尺寸：**比背包那一份高一档**（多一行「价格」），其余一致
    public const float TipW = BackpackPanel.TipW;
    public const float TipH = BackpackPanel.TipH + 24f;

    public static float GridHeight(int rows) => rows <= 0 ? 0f : rows * RowUnit + (rows - 1) * RowGap;
    public static float CellX(int col) => col * (CellW + CellGap);
    public static float CellY(int row) => -(row * (RowUnit + RowGap));
    public static float PriceY(int row) => -(row * (RowUnit + RowGap) + CellH + PriceGap);
    public static int RowsFor(int slots) => Mathf.Max(1, Mathf.CeilToInt(slots / (float)Cols));

    // ==================== 金币 ====================

    /// <summary>"金币 3,750"（千分位；固定用不变文化，避免不同系统区域设置下变成别的分隔符）。</summary>
    public static string GoldString(int gold) => "金币 " + gold.ToString("N0", CultureInfo.InvariantCulture);

    public static void SetGold(Text t, int gold)
    {
        if (t != null) t.text = GoldString(gold);
    }

    // ==================== 建节点（锚点统一：相对父节点的左上角；pivot 左上） ====================

    public static RectTransform NewRect(Transform parent, string name, Vector2 size, Vector2 pos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        return rt;
    }

    /// <summary>把节点从「贴在父节点左上角」（<see cref="NewRect"/> 的默认锚点）改成**相对父节点居中**。</summary>
    public static void Center(RectTransform rt)
    {
        if (rt == null) return;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
    }

    /// <summary>锚到父节点右上角（金币、库存数字这类"贴右边"的元素用）。</summary>
    public static void TopRight(RectTransform rt, Vector2 offset)
    {
        if (rt == null) return;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = offset;
    }

    /// <summary>锚到父节点右下角（数量角标用 —— 与背包 / 箱子的角标位置同一约定）。</summary>
    public static void BottomRight(RectTransform rt, Vector2 offset)
    {
        if (rt == null) return;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = offset;
    }

    public static Image NewImage(Transform parent, string name, Vector2 size, Vector2 pos, Color color)
    {
        var rt = NewRect(parent, name, size, pos);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;      // 纯显示（要点击的由调用方单独打开）
        return img;
    }

    public static Text NewText(Transform parent, string name, Vector2 size, Vector2 pos,
                               int fontSize, Color color, TextAnchor anchor)
    {
        var rt = NewRect(parent, name, size, pos);
        var t = rt.gameObject.AddComponent<Text>();
        UIUtil.ApplyFont(t, UIUtil.Font);   // 字体 + 强制不截断（与全项目同一份实现）
        t.fontSize = fontSize;
        t.alignment = anchor;
        t.color = color;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.supportRichText = true;
        return t;
    }

    /// <summary>把颜色调暗（售罄 / 不收的灰化用；保留 alpha）。</summary>
    public static Color Dim(Color c) => new Color(c.r * 0.4f, c.g * 0.4f, c.b * 0.4f, c.a);

    // ==================== 格子 ====================

    /// <summary>一格"物品格"的节点引用（两个面板共用同一套画法）。</summary>
    internal struct ShopCell
    {
        public RectTransform root;   // 格子框（有物品时才可点击）
        public Image bg;             // 格子底（灰化用）
        public Image icon;           // 图标
        public Text price;           // 格子**正下方**的价格行
        public Text stock;           // 右上角库存数字（库存面板不用 → null）
    }

    /// <summary>建一格（含格子下方那一行价格）。内容由调用方在 <see cref="SetItem"/> 里填。</summary>
    public static ShopCell CreateCell(RectTransform grid, int index, int row, int col)
    {
        var cell = new ShopCell();
        cell.root = NewRect(grid, "Slot" + index, new Vector2(CellW, CellH), new Vector2(CellX(col), CellY(row)));

        cell.bg = cell.root.gameObject.AddComponent<Image>();
        cell.bg.sprite = BackpackPanel.FrameSprite();
        cell.bg.type = Image.Type.Sliced;
        cell.bg.color = BackpackPanel.CellColor();
        cell.bg.raycastTarget = false;   // 默认不吃射线；**有物品的格子**由面板显式打开（与背包空格子同一约定）

        cell.price = NewText(grid, "Price" + index, new Vector2(CellW, PriceH),
                             new Vector2(CellX(col), PriceY(row)), 12, PriceColor, TextAnchor.MiddleCenter);

        return cell;
    }

    /// <summary>填一个有物品的格子：图标 + 颜色（统一走 `ItemVisuals`，与背包 / 箱子 / 快捷栏同源）。</summary>
    public static Image FillIcon(RectTransform cell, ItemData def)
    {
        var icon = NewImage(cell, "Icon", new Vector2(IconSize, IconSize), Vector2.zero, Color.white);
        Center(icon.rectTransform);          // ⚠️ NewRect 默认锚在左上角，必须 Center 否则图标偏左上
        icon.sprite = ItemVisuals.IconOf(def);
        icon.color = ItemVisuals.ColorOf(def);
        icon.preserveAspect = true;
        return icon;
    }

    /// <summary>建右上角的库存数字（商店格子用；库存面板不需要）。</summary>
    public static Text CreateStockText(RectTransform cell)
    {
        var t = NewText(cell, "Stock", new Vector2(30f, 14f), Vector2.zero, 11, Color.white, TextAnchor.UpperRight);
        TopRight(t.rectTransform, new Vector2(-3f, -2f));
        return t;
    }

    // ==================== 面板内提示（toast） ====================

    /// <summary>建一行"面板内提示"（贴在面板底部居中，默认隐藏）。
    ///
    /// ⚠️ **为什么不能用 `HUD.Announce`**：面板打开时 `UIState` 会把**整块 HUD Canvas 关掉**
    /// （`HUD.SetContentVisible(false)`）→ 公告写在 HUD 上 = **玩家根本看不见**；
    /// 而且 `HUD.Announce` 用 `Time.time` 计时，暂停（timeScale=0）时那条公告还会"关店后才淡入"。
    /// 所以商店的失败提示（金币不足 / 已售罄 / 背包已满）必须画在**面板自己身上**。
    /// （📌 箱子的"背包已满"也有同样毛病 —— 那是既有问题，等用户决定要不要一并收口。）</summary>
    public static Text CreateToast(RectTransform root, float width)
    {
        var t = NewText(root, "Toast", new Vector2(width, 18f), Vector2.zero, 13, ToastColor, TextAnchor.MiddleCenter);
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);   // 锚在面板底部居中（不随面板高度变化）
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, 8f);
        t.gameObject.SetActive(false);
        return t;
    }

    // ==================== 悬停提示（tooltip） ====================

    /// <summary>建 tooltip（内容 / 摆位都复用背包的 public static 实现，见 <see cref="PlaceTooltip"/>）。
    /// ⚠️ 必须建在格子**之后**：uGUI 同层按兄弟顺序绘制，后建的盖在上面。</summary>
    public static void CreateTooltip(Transform parent, out GameObject go, out RectTransform rect, out Text text)
    {
        go = new GameObject("Tooltip");
        go.transform.SetParent(parent, false);
        rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(TipW, TipH);
        // ⚠️ 锚点 / 轴心必须是 **(0, 0.5) = 左边缘 + 垂直中心**：PlaceTooltipNearMouse 算的就是这两个量
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 0.5f);

        var img = go.AddComponent<Image>();
        img.sprite = BackpackPanel.PanelSprite();
        img.type = Image.Type.Sliced;
        img.color = new Color(0.06f, 0.08f, 0.13f, 0.96f);   // 与背包 tooltip 同一底色
        img.raycastTarget = false;   // ⚠️ 必须 false：它弹在光标旁，能挡射线的话格子会点不动

        text = NewText(go.transform, "Txt", new Vector2(TipW - 16f, TipH - 12f), Vector2.zero,
                       12, Color.white, TextAnchor.MiddleLeft);
        Center(text.rectTransform);
        text.horizontalOverflow = HorizontalWrapMode.Wrap;      // 说明文字 / 属性行要能换行
        text.verticalOverflow = VerticalWrapMode.Overflow;
        go.SetActive(false);
    }

    /// <summary>tooltip 内容 = **价格行（首行，金色）** + 物品信息（与背包 / 箱子同一份 `ItemInfoString`）。
    /// <paramref name="buy"/> = -1 表示"这一侧不涉及买入"（库存面板只看卖出价）。</summary>
    public static string TipContent(ItemStack stack, int buy, int sell)
    {
        string head = "";
        if (buy >= 0) head += "买入 " + buy;
        if (sell > 0) head += (head.Length > 0 ? "    " : "") + "卖出 " + sell;
        if (head.Length == 0) head = "本店不收";

        string body = BackpackPanel.ItemInfoString(stack);
        return "<color=#FFD24D>" + head + "</color>" + (string.IsNullOrEmpty(body) ? "" : "\n" + body);
    }

    /// <summary>悬停显示：把 tooltip 打开并摆到光标旁。
    /// ⚠️ 鼠标位置必须走 `VirtualCursor.MousePosition`（全项目取鼠标位置的唯一入口）——
    ///    非游玩态那是虚拟光标的位置，直接读 `Input.mousePosition` 会"光标画在一处、tooltip 出现在另一处"。</summary>
    public static void PlaceTooltip(GameObject go, RectTransform tipRect, RectTransform rootRect, Canvas canvas)
    {
        if (go == null) return;
        go.SetActive(true);
        BackpackPanel.PlaceTooltipNearMouse(rootRect, tipRect, canvas, VirtualCursor.MousePosition);
    }

    public static void HideTooltip(GameObject go)
    {
        if (go != null && go.activeSelf) go.SetActive(false);
    }
}
