using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 背包面板（方案 B · 视觉/交互优化版）：
/// - 视觉：圆角描边面板、程序生成武器形状图标（后期可替换真实图片）、当前槽高亮、字体层级
/// - 交互：开/关音效、操作引导、悬停 tooltip（武器属性）、丢弃二次确认、暂停提示、死亡强制关闭
///
/// **界面来源有两套（2026-09-21 起）**：
/// ① **外部 UI Prefab**：`GameAssets.backpackUIPrefab`（Inspector「背包 → 背包界面 Prefab」）。拖了就用它 ——
///    布局 / 尺寸 / 字号 / 颜色 / 增删元素全部在编辑器里拖拽，代码只负责填数据。
/// ② **代码生成（兜底）**：prefab 未配置、或结构不合格时，运行时自建 Overlay Canvas + 全套节点（与历史版本一致）。
///
/// ⚠️ **节点约定**（用外部 prefab 时必须满足；改名 / 挪层级 = 改接口，要同步 <see cref="TryBuildFromPrefab"/>）：
/// <code>
/// BackpackCanvas                    Canvas(Overlay) + GraphicRaycaster + CanvasScaler
/// └ Backpack                        面板根（开/关面板靠它 SetActive）
///   ├ PanelBg   Title   SortBtn       SortBtn 自带 Button（代码会重挂 onClick）
///   ├ Guide     Paused  SlotLabel
///   ├ Slot1 / Slot2 / Slot3           各含：Frame, Icon, Name, Mag, Key, DropBtn（DropBtn 下再挂 Txt）
///   ├ InvLabel  InvGrid               库存网格；**第一个子物体被当作"格子模板"**（Refresh 用它实例化）
///   ├ OccBarBg  OccBarFill
///   └ MedkitIcon  Medkit   Tooltip     Tooltip 下挂 Txt
/// </code>
/// 任一必需部件找不到 → 判结构不合格 → 自动退回代码生成并在 Console 打警告（不会白屏）。
///
/// 打开背包 → `UIState.Push()`（**唯一出口**：统一暂停 + 隐藏整块 HUD + 隐藏准星）；关闭 → `UIState.Pop()`。
/// ⚠️ 面板自己**不许**再直接调 `GameState.SetPaused`（`UIState` 里的明文规矩）。
/// （这里曾经写成"打开时 SetPaused(true)、关闭时只释放自己那次请求"，与实现不符，2026-09-29 修正。）
/// 开关键（**Tab / B**；用户 2026-09-24 定：Esc 不再参与）统一在本组件 Update 处理
///（PlayerController 不再监听，避免同帧双触发）。
/// </summary>
public class BackpackPanel : MonoBehaviour
{
    // ---- 武器图标类型（程序生成形状；后期可换成真实 PNG）----
    enum IconKind { Handgun, SMG, Shotgun, Sniper, Rifle, Generic }

    static BackpackPanel _inst;
    Canvas _canvas;
    PlayerController _pc;
    GameObject _root;

    // 布局常量（参考 1280×720）
    const int SlotCount = 3;
    // 2026-09-20：改成"左侧竖排面板"（参考塔科夫式背包）
    const float PanelW = 400f, PanelH = 660f;
    const float PanelLeftMargin = 16f;       // 面板距屏幕左边
    const float Pad = 16f;                   // 面板内左右边距
    const float SlotW = 112f, SlotH = 96f, SlotGap = 10f;
    const float GridW = 364f, GridH = 290f;  // 库存网格整块（5 列 × 4 行）
    const float CellW = 68f, CellH = 68f, CellGap = 6f;
    const float BarW = 300f, BarH = 14f;     // 底部占用条
    // Tooltip（悬停武器属性）尺寸：⚠️ **两条路径必须一致** —— 这里只是"代码生成"兜底路径用的值，
    // 而**实际生效的是 prefab 里 `Backpack/Tooltip`（+ 子节点 `/Txt`）的 sizeDelta**（背包走 prefab 双轨，见类注释；
    // `TryBuildFromPrefab` 刻意不改预置节点的布局）。**加/删信息行时两处都要改**（每行约 20px）。
    // 📌 **public**（2026-09-25 起）：**箱子面板**是纯代码 UI、没有 prefab 双轨，它直接取这两个值建自己的 tooltip
    //    → 箱子与背包的 tooltip 尺寸永远一致（改这里两处一起变；同理改上面的行数注释）。
    public const float TipW = 260f, TipH = 216f;

    // ---- 快捷槽元素 ----
    RectTransform[] _slotCell = new RectTransform[SlotCount];
    Image[] _slotFrame = new Image[SlotCount];   // 槽位边框（当前槽高亮）
    Image[] _slotIcon = new Image[SlotCount];
    Text[] _slotName = new Text[SlotCount];
    Text[] _slotMag = new Text[SlotCount];
    Text[] _slotDropTxt = new Text[SlotCount];
    Button[] _slotDropBtn = new Button[SlotCount];

    // ---- 库存 ----
    GameObject _invGrid;
    readonly List<RectTransform> _invCells = new List<RectTransform>();

    /// <summary>库存格子模板（外部 UI Prefab 的 InvGrid 下第一个格子；null = 格子由代码生成）。
    /// Refresh 靠它实例化 → 于是"格子长什么样"（底图 / 大小 / 角标位置）也能在 prefab 里拖拽调。</summary>
    GameObject _cellTemplate;
    const string CellTemplateName = "__CellTemplate";   // 模板隐藏留在 InvGrid 下，Refresh 清空时跳过它

    /// <summary>占用条的"满宽"与条高：prefab 模式下取 prefab 里的实际尺寸（所以这条也能拖拽调），否则用代码常量</summary>
    float _barWidth = BarW, _barHeight = BarH;

    /// <summary>快捷槽"普通态"颜色：代码生成时 = SlotColor()；用外部 prefab 时 = prefab 里 Frame 的原始色。
    /// 这样"普通槽色"可以在 prefab 里直接调，而"当前槽高亮色"仍走 GameAssets 参数（状态色归代码）。</summary>
    readonly Color[] _slotIdleColor = new Color[SlotCount];

    // ---- 杂项 UI ----
    Text _medkitText;
    Text _pausedText;
    Text _titleText;      // 标题（含「背包 x/20」计数）
    Text _goldText;       // 标题行右侧的金币（懒创建：prefab 里没有这个节点也会自动补一个）
    Image _invBarFill;    // 底部占用条（已用格数 / 总格数）
    Text _tooltipText;
    GameObject _tooltipGo;
    RectTransform _tooltipRect;   // tooltip 的 RectTransform（BuildUI 缓存，避免每帧 GetComponent）
    RectTransform _rootRect;      // 面板根的 RectTransform（tooltip 位置换算要用）
    ItemData _lastHoverDef;       // 上次显示 tooltip 的物品定义（鼠标没换东西就不重建字符串）

    // ---- 丢弃二次确认 ----
    int _dropConfirmSlot = -1;      // -1 = 无确认中；否则是槽位号
    float _dropConfirmUntil;

    /// <summary>本面板当前是否开着。**替代了旧的 `_pausedByMe`**：
    /// 现在"暂停 / 隐藏整块 HUD / 隐藏准星"三件事统一交给 `UIState` 记账（它支持多个面板共存），
    /// 本面板只记自己开没开 —— 开关键判断、死亡自动关闭、与箱子面板互联都靠它。</summary>
    bool _opened;

    /// <summary>唯一实例（箱子面板要与它"绑成一体"，所以需要从外部访问）。可能为 null（还没创建）。</summary>
    public static BackpackPanel Instance => _inst;

    /// <summary>面板当前是否开着。</summary>
    public bool IsOpen => _opened;

    // ---- 缓存（跨实例，只生成一次）----
    // 字体已改用 UIUtil 的**全局共用字体**（原来这里也自己造一份动态字体，见 UIUtil 的注释）
    static Sprite _sprPanel;        // 圆角填充（面板底）
    static Sprite _sprFrame;        // 圆角描边（格子边框）
    static Sprite _sprSlotFrame;    // 槽位边框
    static Sprite _sprCross;        // 血包十字图标
    static Sprite _sprRect;         // 纯色矩形（占用条用；圆角图被拉成细条会变形）
    static readonly Dictionary<IconKind, Sprite> _iconCache = new Dictionary<IconKind, Sprite>();

    /// <summary>静态状态清理（供 StaticReset 调用）。
    /// 运行时生成的 Sprite/Font 在退出 Play 时会被销毁，而图标缓存取用时是 TryGetValue 直接返回
    /// （不判空）→ 关闭 Reload Domain 后背包图标会凭空消失，所以必须整体清掉重建。</summary>
    internal static void ResetStatics()
    {
        _inst = null;

        // ⚠️ 字体是 UIUtil 的**共用资源**，由 `UIUtil.ResetStatics` 统一释放，这里不要碰
        //（销毁共享字体会让整个工程所有 Legacy Text 一起失效，见 §4 #25）

        // 运行时生成的 Sprite 与它的 Texture2D 是**两个独立对象**（Sprite.Create 不托管纹理）→ 必须成对销毁。
        // 统一走 `UIUtil.DestroySprite`（全项目唯一的成对销毁实现；原来这里各写了一份，2026-09-22 收敛）。
        UIUtil.DestroySprite(_sprPanel); _sprPanel = null;
        UIUtil.DestroySprite(_sprFrame); _sprFrame = null;
        UIUtil.DestroySprite(_sprSlotFrame); _sprSlotFrame = null;
        UIUtil.DestroySprite(_sprCross); _sprCross = null;
        UIUtil.DestroySprite(_sprRect); _sprRect = null;

        foreach (var s in _iconCache.Values) UIUtil.DestroySprite(s);
        _iconCache.Clear();
    }

    void Awake()
    {
        if (_inst != null && _inst != this) { Destroy(gameObject); return; }
        _inst = this;
        BuildUI();
        _root.SetActive(false);
    }

    void OnDestroy()
    {
        if (_inst == this) _inst = null;
    }

    void Update()
    {
        // 死亡时若背包还开着 → 强制关闭（避免重开一局卡在暂停，同时把光标还给结算面板）
        if (GameState.GameOver && _opened) { Close(); return; }

        // 开关键：**只有 Tab / B**（用户 2026-09-24 定：Esc 不再参与开关背包）。
        // ⚠️ 判据用**本面板自己的 `_opened`**，不要用 `GameState.Paused` ——
        //    暂停现在是"多个界面共用"的（箱子面板也会让它是 true），用 Paused 判断会出现
        //    「只开了箱子、按 Tab 却去关背包（而背包本来就没开）」这种错配。
        // 📌 为什么把 Esc 摘掉：Esc 在 Unity / 系统层面还可能被当成"解除光标锁定"，让它同时兼任"关闭键"
        //    会把"关面板"和"放开鼠标"两件事绑在一起（按一下发生两件事）；将来加暂停菜单时 Esc 另有安排。
        if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.B))
        {
            // ⚠️ 商店组占用 Tab 时不响应：Tab = 关闭商店组（且商店打开期间不允许开背包，用户 2026-09-25 定）。
            //    必须做这一层让位：两个面板都在 Update 里读 Tab、谁先跑不确定，
            //    不做的话按一下 Tab 会出现"商店关了、背包却打开了"（见 ShopPanel.HoldsTabKey）
            if (!ShopPanel.HoldsTabKey)
            {
                if (_opened) Close();
                else if (!GameState.GameOver) Open();
            }
        }

        // 丢弃二次确认超时还原
        if (_dropConfirmSlot >= 0 && Time.unscaledTime >= _dropConfirmUntil)
            ResetDropConfirm();

        // 背包打开时：悬停 tooltip（用自己的 `_opened` 判断，别被其它界面的暂停干扰）
        if (_opened)
            UpdateTooltip();
    }

    // ==================== 构建 UI ====================

    void BuildUI()
    {
        // 优先用**外部 UI Prefab**（编辑器里搭好 / 拖拽改好的那套）；未配置或结构不合格 → 退回代码生成
        var prefab = GameAssets.I != null ? GameAssets.I.backpackUIPrefab : null;
        if (prefab != null && TryBuildFromPrefab(prefab)) return;

        BuildByCode();
    }

    /// <summary>
    /// 用外部 UI Prefab 搭界面：实例化 → 按**约定节点名**找回全部部件引用 → 把 InvGrid 下第一个格子存为模板
    /// → 补齐"prefab 存不下"的东西（代码生成的 Sprite、共用字体）→ 结构自检。
    /// 返回 false（实例已销毁）时调用方会退回代码生成 —— prefab 配错只是回退，不会白屏。
    /// ⚠️ 节点名 / 层级是接口约定，见类注释里的节点图。
    /// </summary>
    bool TryBuildFromPrefab(GameObject prefab)
    {
        var go = Instantiate(prefab);
        go.name = "BackpackCanvas";
        _canvas = go.GetComponent<Canvas>();
        var root = go.transform.Find("Backpack");
        if (_canvas == null || root == null) { Destroy(go); return false; }

        _root = root.gameObject;
        _rootRect = root as RectTransform;

        // ---- 按名字找回部件 ----
        _titleText   = Comp<Text>(root, "Title");
        _pausedText  = Comp<Text>(root, "Paused");
        _medkitText  = Comp<Text>(root, "Medkit");
        _invBarFill  = Comp<Image>(root, "OccBarFill");
        _invGrid     = Go(root, "InvGrid");
        if (_invBarFill != null)
        {
            var barRt = _invBarFill.rectTransform;
            if (barRt.sizeDelta.x > 1f) _barWidth = barRt.sizeDelta.x;     // 占用条满宽以 prefab 为准（拖宽了代码跟着变）
            if (barRt.sizeDelta.y > 0.5f) _barHeight = barRt.sizeDelta.y;
        }
        _tooltipGo   = Go(root, "Tooltip");
        _tooltipRect = _tooltipGo != null ? _tooltipGo.GetComponent<RectTransform>() : null;
        _tooltipText = _tooltipGo != null ? Comp<Text>(_tooltipGo.transform, "Txt") : null;

        for (int i = 0; i < SlotCount; i++)
        {
            var slot = root.Find("Slot" + (i + 1));
            if (slot == null) continue;
            _slotCell[i]    = slot as RectTransform;
            _slotFrame[i]   = Comp<Image>(slot, "Frame");
            _slotIcon[i]    = Comp<Image>(slot, "Icon");
            _slotName[i]    = Comp<Text>(slot, "Name");
            _slotMag[i]     = Comp<Text>(slot, "Mag");
            _slotDropTxt[i] = Comp<Text>(slot, "DropBtn/Txt");
            if (_slotFrame[i] != null) _slotIdleColor[i] = _slotFrame[i].color;   // 普通槽色以 prefab 为准

            // 丢弃按钮：onClick 监听器**存不进 prefab** → 这里统一重挂；prefab 漏挂 Button 也补上
            var drop = slot.Find("DropBtn");
            if (drop != null)
            {
                _slotDropBtn[i] = drop.GetComponent<Button>();
                if (_slotDropBtn[i] == null) _slotDropBtn[i] = drop.gameObject.AddComponent<Button>();
                int idx = i;
                _slotDropBtn[i].onClick.RemoveAllListeners();
                _slotDropBtn[i].onClick.AddListener(() => OnDropBtnClick(idx));
            }
        }

        // 「整理」按钮同理：重挂 onClick
        var sortBtn = Comp<Button>(root, "SortBtn");
        if (sortBtn != null)
        {
            sortBtn.onClick.RemoveAllListeners();
            sortBtn.onClick.AddListener(OnSortBtnClick);
        }

        // ---- 结构自检：必需部件缺任一个就不硬撑（宁可在 Console 说清楚，也不要白屏）----
        bool slotsComplete = true;
        for (int i = 0; i < SlotCount; i++)
            if (_slotFrame[i] == null || _slotIcon[i] == null || _slotName[i] == null ||
                _slotMag[i] == null || _slotDropTxt[i] == null) { slotsComplete = false; break; }

        if (_titleText == null || _medkitText == null || _invGrid == null || _invBarFill == null ||
            !slotsComplete || _tooltipGo == null || _tooltipText == null)
        {
            Debug.LogWarning("[BackpackPanel] 背包界面 Prefab 结构不完整 → 已退回代码生成。请对照 BackpackPanel 类注释里的节点图检查" +
                             "（需要 Backpack / Backpack/Title / Slot1..3/{Frame,Icon,Name,Mag,DropBtn/Txt} / InvGrid / OccBarFill / Tooltip/Txt 等）。");
            Destroy(go);
            return false;
        }

        // ---- 库存格子：InvGrid 下第一个格子当模板（隐藏保留），其余交给 Refresh 按 Capacity 重建 ----
        var gridT = _invGrid.transform;
        if (gridT.childCount > 0)
        {
            _cellTemplate = gridT.GetChild(0).gameObject;
            _cellTemplate.name = CellTemplateName;
            _cellTemplate.SetActive(false);
        }

        // ---- 补齐 prefab 存不下的东西：代码生成的 Sprite + 共用字体（已配好的不覆盖）----
        FillEmptySprites(root);
        foreach (var t in go.GetComponentsInChildren<Text>(true))
            if (t.font == null) t.font = UIUtil.Font;

        Refresh();
        UIUtil.SetLayerRecursively(go, UIUtil.UILayer);   // 与代码生成一致：整棵树归 UI 层
        return true;
    }

    /// <summary>代码生成整棵 UI 树（外部 prefab 未配置 / 结构不合格时的兜底；行为与历史版本一致）</summary>
    void BuildByCode()
    {
        // ---- 取共享资源（优先 GameAssets 里配的 Sprite，留空才用代码生成）----
        var sprPanel = PanelSprite();
        var sprFrame = FrameSprite();
        var sprSlotFrame = SlotFrameSprite();
        var sprCross = MedkitSprite();

        _canvas = UIUtil.NewOverlayCanvas("BackpackCanvas", 200);
        var canvasGo = _canvas.gameObject;
        canvasGo.AddComponent<GraphicRaycaster>();   // 背包可点击（额外件：其余 3 块 UI 不需要）

        // 面板：**贴屏幕左侧、垂直居中**（参考图那样的"左侧竖排"布局）
        _root = new GameObject("Backpack");
        _root.transform.SetParent(canvasGo.transform, false);
        var rootRt = _root.AddComponent<RectTransform>();
        rootRt.anchorMin = rootRt.anchorMax = rootRt.pivot = new Vector2(0f, 0.5f);   // 锚点 = 左中
        rootRt.anchoredPosition = new Vector2(PanelLeftMargin, 0f);
        rootRt.sizeDelta = new Vector2(PanelW, PanelH);
        _rootRect = rootRt;

        float halfW = PanelW * 0.5f, halfH = PanelH * 0.5f;
        float leftX = -halfW + Pad;        // 面板内容左边缘
        float textCx = leftX + 120f;       // 左对齐文字的中心 x（宽 240）

        // 面板底（半透明圆角填充）
        var panelImg = AddImage(_root.transform, "PanelBg", new Vector2(PanelW, PanelH), PanelColor());
        panelImg.sprite = sprPanel;
        panelImg.type = Image.Type.Sliced;
        panelImg.raycastTarget = true;   // 拦截穿透点击

        // ---- 标题（含 x/20 计数，左） + 整理按钮（右）----
        float titleY = halfH - 30f;
        _titleText = AddText(_root.transform, "Title", new Vector2(240f, 30f), "背包", 22, new Color(1f, 0.94f, 0.6f));
        _titleText.fontStyle = FontStyle.Bold;
        _titleText.alignment = TextAnchor.MiddleLeft;
        _titleText.rectTransform.anchoredPosition = new Vector2(textCx, titleY);

        var sortGo = new GameObject("SortBtn");
        sortGo.transform.SetParent(_root.transform, false);
        var sortRt = sortGo.AddComponent<RectTransform>();
        sortRt.sizeDelta = new Vector2(64f, 26f);
        sortRt.anchoredPosition = new Vector2(halfW - Pad - 32f, titleY);
        var sortImg = sortGo.AddComponent<Image>();
        sortImg.sprite = sprFrame;
        sortImg.type = Image.Type.Sliced;
        sortImg.color = SlotColor();
        sortImg.raycastTarget = true;
        var sortTxt = AddText(sortGo.transform, "Txt", new Vector2(60f, 22f), "整理", 13, new Color(0.9f, 0.94f, 1f));
        sortTxt.rectTransform.anchoredPosition = Vector2.zero;
        var sortBtn = sortGo.AddComponent<Button>();
        sortBtn.targetGraphic = sortImg;
        sortBtn.onClick.AddListener(OnSortBtnClick);

        // 引导（左） + 暂停提示（右）
        float subY = titleY - 26f;
        var guide = AddText(_root.transform, "Guide", new Vector2(240f, 20f),
            "点击装备 · Tab/B 关闭", 12, new Color(0.68f, 0.72f, 0.8f));
        guide.alignment = TextAnchor.MiddleLeft;
        guide.rectTransform.anchoredPosition = new Vector2(textCx, subY);

        _pausedText = AddText(_root.transform, "Paused", new Vector2(120f, 20f), "游戏已暂停", 13, new Color(0.5f, 1f, 0.6f));
        _pausedText.alignment = TextAnchor.MiddleRight;
        _pausedText.rectTransform.anchoredPosition = new Vector2(halfW - Pad - 60f, subY);

        // ---- 快捷武器槽 ----
        float slotLabelY = subY - 30f;
        var slotLabel = AddText(_root.transform, "SlotLabel", new Vector2(240f, 20f), "快捷武器  [1/2/3]", 13, new Color(0.6f, 0.66f, 0.76f));
        slotLabel.alignment = TextAnchor.MiddleLeft;
        slotLabel.rectTransform.anchoredPosition = new Vector2(textCx, slotLabelY);

        float slotY = slotLabelY - 10f - SlotH * 0.5f;
        float startX = -(SlotW * SlotCount + SlotGap * (SlotCount - 1)) * 0.5f + SlotW * 0.5f;
        for (int i = 0; i < SlotCount; i++)
        {
            var cell = new GameObject("Slot" + (i + 1));
            cell.transform.SetParent(_root.transform, false);
            _slotCell[i] = cell.AddComponent<RectTransform>();
            _slotCell[i].sizeDelta = new Vector2(SlotW, SlotH);
            _slotCell[i].anchoredPosition = new Vector2(startX + i * (SlotW + SlotGap), slotY);

            // 槽位边框（当前槽高亮用，Sliced 描边）
            _slotFrame[i] = AddImage(cell.transform, "Frame", new Vector2(SlotW, SlotH), SlotColor());
            _slotFrame[i].sprite = sprSlotFrame;
            _slotFrame[i].type = Image.Type.Sliced;
            _slotIdleColor[i] = SlotColor();   // 普通态槽色（Refresh 里与"当前槽高亮色"二选一）

            // 图标
            _slotIcon[i] = AddImage(cell.transform, "Icon", new Vector2(56f, 34f), Color.white);
            _slotIcon[i].preserveAspect = true;
            _slotIcon[i].rectTransform.anchoredPosition = new Vector2(0f, 20f);

            // 武器名
            _slotName[i] = AddText(cell.transform, "Name", new Vector2(SlotW - 12f, 20f), "", 13, Color.white);
            _slotName[i].rectTransform.anchoredPosition = new Vector2(0f, -8f);

            // 弹匣
            _slotMag[i] = AddText(cell.transform, "Mag", new Vector2(SlotW - 12f, 16f), "", 12, new Color(0.75f, 0.8f, 0.88f));
            _slotMag[i].rectTransform.anchoredPosition = new Vector2(0f, -28f);

            // 数字角标
            var key = AddText(cell.transform, "Key", new Vector2(26f, 18f), (i + 1).ToString(), 13, new Color(0.5f, 0.56f, 0.66f));
            key.rectTransform.anchoredPosition = new Vector2(-SlotW * 0.5f + 14f, SlotH * 0.5f - 12f);

            // 丢弃按钮（二次确认）
            var dropGo = new GameObject("DropBtn");
            dropGo.transform.SetParent(cell.transform, false);
            var dropRt = dropGo.AddComponent<RectTransform>();
            dropRt.sizeDelta = new Vector2(64f, 26f);
            dropRt.anchoredPosition = new Vector2(0f, -SlotH * 0.5f - 16f);
            var dropImg = dropGo.AddComponent<Image>();
            dropImg.sprite = sprFrame;
            dropImg.type = Image.Type.Sliced;
            dropImg.color = new Color(0.45f, 0.18f, 0.15f, 0.9f);
            dropImg.raycastTarget = true;
            _slotDropTxt[i] = AddText(dropGo.transform, "Txt", new Vector2(60f, 22f), "丢弃", 12, new Color(1f, 0.8f, 0.75f));
            _slotDropTxt[i].rectTransform.anchoredPosition = Vector2.zero;
            _slotDropBtn[i] = dropGo.AddComponent<Button>();
            _slotDropBtn[i].targetGraphic = dropImg;
            int slotIdx = i;
            _slotDropBtn[i].onClick.AddListener(() => OnDropBtnClick(slotIdx));
        }

        // ---- 库存区 ----
        float invLabelY = slotY - SlotH * 0.5f - 46f;
        var invLabel = AddText(_root.transform, "InvLabel", new Vector2(240f, 20f), "背包库存", 13, new Color(0.6f, 0.66f, 0.76f));
        invLabel.alignment = TextAnchor.MiddleLeft;
        invLabel.rectTransform.anchoredPosition = new Vector2(textCx, invLabelY);

        float gridY = invLabelY - 18f - GridH * 0.5f;
        _invGrid = new GameObject("InvGrid");
        _invGrid.transform.SetParent(_root.transform, false);
        var gridRt = _invGrid.AddComponent<RectTransform>();
        gridRt.sizeDelta = new Vector2(GridW, GridH);          // 5 列 × 4 行（20 格）
        gridRt.anchoredPosition = new Vector2(0f, gridY);

        // ---- 底部：占用条（已用格数 / 总格数）----
        float barY = gridY - GridH * 0.5f - 16f - BarH * 0.5f;
        var barBg = AddImage(_root.transform, "OccBarBg", new Vector2(BarW, BarH), new Color(0f, 0f, 0f, 0.55f));
        barBg.sprite = RectSprite();
        barBg.rectTransform.anchoredPosition = new Vector2(leftX + BarW * 0.5f, barY);

        _invBarFill = AddImage(_root.transform, "OccBarFill", new Vector2(BarW, BarH), SlotActiveColor());
        _invBarFill.sprite = RectSprite();
        _invBarFill.rectTransform.pivot = new Vector2(0f, 0.5f);   // 从左往右长
        _invBarFill.rectTransform.anchoredPosition = new Vector2(leftX, barY);

        // ---- 血包 ----
        float medkitY = barY - BarH * 0.5f - 36f;
        var medkitIcon = AddImage(_root.transform, "MedkitIcon", new Vector2(22f, 22f), new Color(0.95f, 0.2f, 0.2f));
        medkitIcon.sprite = sprCross;
        medkitIcon.rectTransform.anchoredPosition = new Vector2(leftX + 11f, medkitY);
        _medkitText = AddText(_root.transform, "Medkit", new Vector2(PanelW - 60f, 22f), "", 14, new Color(0.85f, 0.95f, 0.9f));
        _medkitText.alignment = TextAnchor.MiddleLeft;
        _medkitText.rectTransform.anchoredPosition = new Vector2(leftX + 30f + (PanelW - 60f) * 0.5f, medkitY);

        // ---- Tooltip（悬停武器属性，默认隐藏）----
        _tooltipGo = new GameObject("Tooltip");
        _tooltipGo.transform.SetParent(_root.transform, false);
        var ttRt = _tooltipGo.AddComponent<RectTransform>();
        ttRt.sizeDelta = new Vector2(TipW, TipH);
        ttRt.anchorMin = ttRt.anchorMax = ttRt.pivot = new Vector2(0f, 0.5f);   // 锚在左边：位置按"左边缘 + 垂直中心"给（方便放到鼠标右侧）
        var ttImg = _tooltipGo.AddComponent<Image>();
        ttImg.sprite = sprPanel;
        ttImg.type = Image.Type.Sliced;
        ttImg.color = new Color(0.06f, 0.08f, 0.13f, 0.96f);
        ttImg.raycastTarget = false;
        _tooltipText = AddText(_tooltipGo.transform, "Txt", new Vector2(TipW - 16f, TipH - 12f), "", 12, Color.white);
        _tooltipText.rectTransform.anchoredPosition = new Vector2(0f, 0f);
        _tooltipText.alignment = TextAnchor.MiddleLeft;
        _tooltipText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _tooltipText.verticalOverflow = VerticalWrapMode.Overflow;
        _tooltipRect = _tooltipGo.GetComponent<RectTransform>();   // 缓存，避免每帧 GetComponent
        _tooltipGo.SetActive(false);

        Refresh();

        // 统一放到 UI 层（与 HUD 一致；Overlay 下不影响渲染）
        UIUtil.SetLayerRecursively(canvasGo, UIUtil.UILayer);
    }

    // ==================== 开 / 关 ====================

    public void Open()
    {
        if (_opened) return;      // 已经开着（幂等）
        _opened = true;
        UIState.Push();           // 暂停 + 隐藏整块 HUD + 隐藏准星（**唯一出口**，支持多界面共存）
        _root.SetActive(true);
        Sfx.OpenInventory();
        ResetDropConfirm();
        Refresh();
    }

    public void Close()
    {
        if (!_opened) return;     // 幂等：重复调用无副作用（与箱子面板互联时会互相调）
        _opened = false;
        _root.SetActive(false);
        Sfx.CloseInventory();
        UIState.Pop();
        ResetDropConfirm();

        // 与箱子面板"绑成一个整体"：任意一方关闭 → 两个一起关
        //（对方内部也有幂等守卫，所以不会递归打转）
        if (ChestPanel.Instance != null && ChestPanel.Instance.IsOpen) ChestPanel.Instance.Close();
    }

    /// <summary>
    /// **外部改了背包数据之后的刷新入口**（目前只有 `ChestPanel` 用它 —— 从箱子取东西 = 背包库存变了）。
    /// 面板没开时什么都不做：打开时 `Open()` 本来就会 Refresh，关着的时候没必要重建一堆 UI 节点。
    /// ⚠️ 为什么必须有它：箱子面板是**另一个面板**，不会自己知道库存变了；
    ///    不通知的话就会出现「东西已经进背包了，但左边要关掉再打开才看得见」（2026-09-24 修的 bug）。
    /// </summary>
    public void RefreshIfOpen()
    {
        if (!_opened) return;
        Refresh();
    }

    // ==================== 数据刷新 ====================

    /// <summary>金币显示色（**共享色**，与 `MedkitIconColor` 同性质）：背包标题行右侧的金币、
    /// 商店面板 / 库存面板的金币都用它 —— 只留一份，改这里三处一起变。</summary>
    public static readonly Color GoldColor = new Color(1f, 0.84f, 0.45f);

    /// <summary>标题行右侧的金币文本（**懒创建**）：prefab 与代码生成两条路径共用 ——
    /// 都挂在面板根上、锚到右上角，位置在「整理」按钮左侧（按钮宽 64 + 右内边距 + 8 间隙）。
    /// 为什么懒创建：背包面板走 prefab 双轨，而 prefab 里**没有**这个节点 —— 代码补一个，
    /// 这样"加金币显示"不需要去改 prefab（用户 2026-09-25 定：金币显示在背包标题行右侧）。</summary>
    void EnsureGoldText()
    {
        if (_goldText != null || _root == null) return;

        // 先看面板根下有没有现成的 "Gold" 节点（prefab 里将来若手工加了就用它，别重复建）
        var existing = _root.transform.Find("Gold");
        if (existing != null) _goldText = existing.GetComponent<Text>();

        if (_goldText == null)
            _goldText = AddText(_root.transform, "Gold", new Vector2(150f, 26f), "", 14, GoldColor);

        _goldText.alignment = TextAnchor.MiddleRight;
        _goldText.color = GoldColor;
        var rt = _goldText.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);   // 锚到面板右上角（与"整理"按钮同一行）
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-(Pad + 72f), -30f);
    }

    void Refresh()
    {
        if (_invGrid == null || _medkitText == null) return;   // 面板还没建完
        if (_pc == null) _pc = PlayerController.Instance;
        var bp = _pc != null ? _pc.backpack : null;

        int cap = bp != null ? bp.Capacity : 20;
        int used = bp != null ? bp.inventory.Count : 0;

        // 标题计数 + 底部占用条
        if (_titleText != null) _titleText.text = "背包   " + used + "/" + cap;

        // 金币（标题行右侧；数值来自 `GameState.Gold`，商店买卖 / 开局资金都会改它）
        EnsureGoldText();
        if (_goldText != null)
            _goldText.text = "金币 " + GameState.Gold.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        if (_invBarFill != null)
        {
            float w = cap > 0 ? Mathf.Clamp01(used / (float)cap) * _barWidth : 0f;
            _invBarFill.rectTransform.sizeDelta = new Vector2(w, _barHeight);
        }

        // 快捷槽
        for (int i = 0; i < SlotCount; i++)
        {
            var w = bp != null && bp.slots[i].weapon != null ? bp.slots[i].weapon : null;
            bool isCurrent = bp != null && bp.currentSlot == i;

            // 当前槽高亮 / 其余普通。普通色来自 `_slotIdleColor`（代码生成 = SlotColor()；外部 prefab = Frame 的原始色）
            _slotFrame[i].color = isCurrent ? SlotActiveColor() : _slotIdleColor[i];

            if (w != null)
            {
                _slotIcon[i].sprite = WeaponIconSprite(w);
                _slotIcon[i].color = w.pickupColor;
                _slotIcon[i].gameObject.SetActive(true);
                _slotName[i].text = w.weaponName;
                _slotName[i].color = w.pickupColor;
                // 弹匣行：`弹 30/30 Ⅱ` —— 等级徽标与 HUD 共用 `AmmoData.TierMark`（唯一实现）。
                // ⚠️ 等级取**这一格自己的** `ammoId`（当前槽读镜像、其它槽读槽里的记录）——
                //    否则切到别的槽时会显示成手上那把这级的等级。
                var slotCard = isCurrent ? _pc.magAmmoId : bp.slots[i].ammoId;
                string slotTier = slotCard != null ? slotCard.TierMark : "";
                _slotMag[i].text = "弹 " + (isCurrent ? _pc.magAmmo : bp.slots[i].magAmmo) + "/" + w.magSize
                                 + (slotTier.Length > 0 ? "  " + slotTier : "");
            }
            else
            {
                _slotIcon[i].gameObject.SetActive(false);
                _slotName[i].text = "空槽";
                _slotName[i].color = new Color(0.45f, 0.5f, 0.6f);
                _slotMag[i].text = "按 1/2/3 装备";
            }

            // 丢弃按钮文字（未被确认态占用时统一）
            if (_dropConfirmSlot != i)
            {
                _slotDropTxt[i].text = "丢弃";
                _slotDropTxt[i].color = new Color(1f, 0.8f, 0.75f);
            }
        }

        // 库存：清空重建（**固定显示 capacity 个格子**，空格子也画出来 —— 一眼看出还剩几格）
        // ⚠️ 模板节点（外部 prefab 带来的格子原型）必须留着 —— 下面靠它实例化
        _invCells.Clear();
        for (int i = _invGrid.transform.childCount - 1; i >= 0; i--)
        {
            var ch = _invGrid.transform.GetChild(i);
            if (ch.name == CellTemplateName) continue;
            Destroy(ch.gameObject);
        }

        const int cols = 5;                          // 5 列（20 格 = 5×4）
        int rows = Mathf.CeilToInt(cap / (float)cols);
        float gridW = _invGrid.GetComponent<RectTransform>().rect.width;   // 循环外取一次，避免每格 GetComponent

        // 格子尺寸：有 prefab 模板就按模板量（于是"格子多大"也能在 prefab 里调），否则用代码常量
        float cellW = CellW, cellH = CellH;
        if (_cellTemplate != null)
        {
            var trt = _cellTemplate.GetComponent<RectTransform>();
            if (trt != null && trt.sizeDelta.x > 1f) { cellW = trt.sizeDelta.x; cellH = trt.sizeDelta.y; }
        }
        float startY = (rows * (cellH + CellGap) - CellGap) * 0.5f - cellH * 0.5f;   // 第一行中心 y（整体垂直居中）

        for (int i = 0; i < cap; i++)
        {
            int row = i / cols, col = i % cols;
            // 库存格现在是**通用物品格**（ItemStack = 物品定义 + 数量）：武器 / 血包 / 以后的子弹都走这里，
            // 所以下面一律用 `item.def`，**不要**再假设它一定是武器（旧版这里就在 `item.weapon` 上写死了）
            var item = (bp != null && i < bp.inventory.Count) ? bp.inventory[i] : null;
            bool hasItem = item != null && item.def != null && !item.IsEmpty;
            string cellName = hasItem ? "Inv_" + item.DisplayName : "InvEmpty";

            // 格子来源：有模板就实例化（样式全部来自 prefab），否则代码生成
            GameObject cell;
            Image cellImg;
            if (_cellTemplate != null)
            {
                cell = Instantiate(_cellTemplate, _invGrid.transform);
                cell.name = cellName;
                cell.SetActive(true);
                cellImg = cell.GetComponent<Image>();
                if (cellImg == null) { cellImg = cell.AddComponent<Image>(); cellImg.color = CellColor(); }
            }
            else
            {
                cell = new GameObject(cellName);
                cell.transform.SetParent(_invGrid.transform, false);
                cellImg = cell.AddComponent<Image>();
                cellImg.sprite = FrameSprite();
                cellImg.type = Image.Type.Sliced;
                cellImg.color = CellColor();
            }

            var rt = cell.GetComponent<RectTransform>();
            if (rt == null) rt = cell.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(cellW, cellH);
            rt.anchoredPosition = new Vector2(
                col * (cellW + CellGap) - gridW * 0.5f + cellW * 0.5f,
                startY - row * (cellH + CellGap));
            cellImg.raycastTarget = hasItem;   // 空格子不接点击

            // 右键丢弃：给**有物品**的格子挂一个点击分发器（左键仍归下面那个 Button 管）
            // ⚠️ `index` 必须**每次 Refresh 重新绑** —— 格子是重建的，且丢弃后下标会整体前移
            var cellClick = cell.GetComponent<BackpackCellClick>();
            if (hasItem)
            {
                if (cellClick == null) cellClick = cell.AddComponent<BackpackCellClick>();
                cellClick.panel = this;
                cellClick.index = i;
                cellClick.enabled = true;
            }
            else if (cellClick != null) cellClick.enabled = false;

            if (!hasItem) { _invCells.Add(null); continue; }   // 空格子到此为止（只画个框）

            // 图标 + 颜色：统一走 `ItemVisuals`（背包 / 箱子 / 快捷栏共用同一份实现 → 三处永远一致）
            // ⚠️ 血包是「没有 WeaponData 的占格物品」→ 不要直接 use `item.def as WeaponData` 的结果
            var icon = Comp<Image>(cell.transform, "Icon");
            if (icon == null)
            {
                icon = AddImage(cell.transform, "Icon", new Vector2(42f, 42f), Color.white);
                icon.rectTransform.anchoredPosition = new Vector2(0f, 5f);
            }
            icon.gameObject.SetActive(true);
            icon.sprite = ItemVisuals.IconOf(item.def);
            icon.color = ItemVisuals.ColorOf(item.def);
            icon.preserveAspect = true;

            // 数量角标（右下角，只有叠了多个才显示；模板里可能自带，没有就建一个）
            var cnt = Comp<Text>(cell.transform, "Count");
            if (item.count > 1)
            {
                if (cnt == null)
                {
                    cnt = AddText(cell.transform, "Count", new Vector2(cellW - 12f, 16f), "", 12, Color.white);
                    cnt.alignment = TextAnchor.MiddleRight;
                    cnt.rectTransform.anchoredPosition = new Vector2(-2f, -cellH * 0.5f + 10f);
                }
                cnt.text = "×" + item.count;
                cnt.gameObject.SetActive(true);
            }
            else if (cnt != null) cnt.gameObject.SetActive(false);

            // 点击 = 装备到空槽；没有空槽则替换当前槽，被换下的枪**下放进库存**（只有库存真放不下才掉地上）。
            // ⚠️ **只有武器**才挂这个事件：血包 / 子弹点它不该进快捷武器槽（血包靠 Q 键消耗）
            var btn = cell.GetComponent<Button>();
            var asWeapon = item.def as WeaponData;
            var asMagic = item.def as MagicData;
            // ⚠️ 武器与魔法都能"装备"到各自的槽（2026-09-28 起魔法也有槽）；
            //    血包 / 子弹点它不该进任何装备槽（血包靠 Q 键消耗）
            if (asWeapon != null || asMagic != null)
            {
                if (btn == null) btn = cell.AddComponent<Button>();
                btn.targetGraphic = cellImg;
                btn.onClick.RemoveAllListeners();
                string id = item.def.StackId;   // 用**身份证**而不是名字（敌人掉的枪是副本，名字/引用都不可靠）
                bool isMagic = asMagic != null;
                btn.onClick.AddListener(() =>
                {
                    if (_pc != null)
                    {
                        if (isMagic) _pc.EquipMagicFromInventory(id);   // 装到魔法槽（HUD 的 E 格）
                        else _pc.EquipFromInventory(id);                // 装到武器槽（1/2/3）
                        Sfx.Confirm();
                    }
                    Refresh();
                });
            }
            else if (btn != null) btn.onClick.RemoveAllListeners();

            _invCells.Add(rt);
        }

        // 血包汇总行（血包本体就在上面的库存格子里，这里只是汇总 + 操作提示）
        _medkitText.text = bp != null && bp.Medkits > 0
            ? "血包 ×" + bp.Medkits + "   （每个占 1 格 · 按 Q 使用，需未满血）"
            : "血包 ×0   （地图上的药箱按 F 拾取，每个占 1 格）";

        // 库存格子是每次 Refresh 新建的（会重新落到 Default 层）→ 每次刷完统一设回 UI 层
        UIUtil.SetLayerRecursively(_invGrid, UIUtil.UILayer);
    }

    /// <summary>「整理」按钮（面板右上角）：把库存按武器名排序</summary>
    void OnSortBtnClick()
    {
        if (_pc == null) _pc = PlayerController.Instance;
        if (_pc != null && _pc.backpack != null) _pc.backpack.SortInventory();
        Sfx.Confirm();
        Refresh();
    }

    // ==================== 丢弃二次确认 ====================

    void OnDropBtnClick(int slotIdx)
    {
        if (_pc == null) return;
        if (_dropConfirmSlot == slotIdx)
        {
            // 已确认态再点一次 → 真正丢弃
            ResetDropConfirm();
            _pc.DropSlotAt(slotIdx);
            Sfx.Drop();
            Refresh();
            return;
        }
        // 第一次点 → 进入确认态
        ResetDropConfirm();
        _dropConfirmSlot = slotIdx;
        _dropConfirmUntil = Time.unscaledTime + 2f;
        _slotDropTxt[slotIdx].text = "再点确认！";
        _slotDropTxt[slotIdx].color = new Color(1f, 0.4f, 0.3f);
        Sfx.Confirm();
    }

    void ResetDropConfirm()
    {
        _dropConfirmSlot = -1;
        _dropConfirmUntil = 0f;
        if (_slotDropTxt != null)
            for (int i = 0; i < SlotCount; i++)
                if (_slotDropTxt[i] != null)
                {
                    _slotDropTxt[i].text = "丢弃";
                    _slotDropTxt[i].color = new Color(1f, 0.8f, 0.75f);
                }
    }

    // ==================== 库存格右键丢弃（2026-09-28） ====================

    // 库存格的"待确认丢弃"目标：第一次右键记下来，2 秒内再右键同一格才真丢。
    // ⚠️ 存的是**格子在 `backpack.inventory` 里的下标** —— 丢弃后必须 Refresh（下标会变），
    //    所以确认态用完立刻清掉。
    int _dropInvIndex = -1;
    float _dropInvUntil;

    /// <summary>
    /// 库存格**右键**：第一次 = 提示确认；2 秒内再右键同一格 = 真正丢弃。
    /// 与快捷武器槽的"点两次丢弃"同一套交互（都是再确认一次），区别只是库存格没有独立按钮，
    /// 所以第一次右键改用 `HUD.Announce` 给提示。
    ///
    /// ⚠️ 由 <see cref="BackpackCellClick"/> 转发进来（它挂在每个格子上）——
    ///    走 `EventSystem` 才能正确对应"虚拟光标指的是哪一格"。
    /// </summary>
    public void RequestDropItem(int index)
    {
        if (Time.unscaledTime > _dropInvUntil) _dropInvIndex = -1;   // 超过 2 秒 → 回到"第一次"

        if (_pc == null) _pc = PlayerController.Instance;
        if (_pc == null || _pc.backpack == null) return;
        var bp = _pc.backpack;
        if (index < 0 || index >= bp.inventory.Count) return;
        var item = bp.inventory[index];
        if (item == null || item.def == null || item.IsEmpty) return;

        if (_dropInvIndex == index)
        {
            _dropInvIndex = -1;
            _pc.DropItemFromInventory(index);
            Sfx.Drop();
            Refresh();
            return;
        }

        _dropInvIndex = index;
        _dropInvUntil = Time.unscaledTime + 2f;
        HUD.Announce("再点一次右键丢弃  " + item.DisplayName, 1.2f, 24, new Color(1f, 0.6f, 0.4f));
        Sfx.Confirm();
    }

    // ==================== Tooltip ====================

    void UpdateTooltip()
    {
        if (_pc == null) _pc = PlayerController.Instance;
        var bp = _pc != null ? _pc.backpack : null;
        // ⚠️ 鼠标位置统一走 `VirtualCursor.MousePosition`（非游玩态 = 虚拟光标的位置，继承准星）。
        //    直接读 `Input.mousePosition` 会按"被锁在屏幕中心的系统光标"判定悬停 → 光标画在一处、
        //    tooltip 却出现在另一处（2026-09-22 加虚拟光标时一并改掉）。
        Vector2 mouse = VirtualCursor.MousePosition;

        WeaponData hoverWeapon = null;   // 悬停在快捷武器槽上
        ItemStack hoverItem = null;      // 悬停在库存格上（通用物品）

        // 快捷槽（只放武器）
        for (int i = 0; i < SlotCount; i++)
            if (_slotCell[i] != null && bp != null && bp.slots[i].weapon != null &&
                RectTransformUtility.RectangleContainsScreenPoint(_slotCell[i], mouse, null))
            { hoverWeapon = bp.slots[i].weapon; break; }

        // 库存
        // 上界取两者较小值：_invCells（UI）与 bp.inventory（数据）是靠索引对齐的两个平行数组，
        // 只要有一边没同步（数据改了但没 Refresh）就会越界 → 这里兜住，最坏只是不显示 tooltip
        int invCount = bp != null ? Mathf.Min(_invCells.Count, bp.inventory.Count) : 0;
        if (hoverWeapon == null && bp != null)
            for (int i = 0; i < invCount; i++)
                if (_invCells[i] != null && bp.inventory[i] != null && !bp.inventory[i].IsEmpty &&
                    RectTransformUtility.RectangleContainsScreenPoint(_invCells[i], mouse, null))
                { hoverItem = bp.inventory[i]; break; }

        // 内容只在**悬停的物品变化**时重建（鼠标不动不该每帧拼字符串）。
        // 脏值键用 `ItemData`（同一种物品的内容不变；数量不进 tooltip）
        ItemData keyDef = hoverWeapon != null ? (ItemData)hoverWeapon
                                              : (hoverItem != null ? hoverItem.def : null);
        if (keyDef != null)
        {
            if (!ReferenceEquals(keyDef, _lastHoverDef))
            {
                _lastHoverDef = keyDef;
                _tooltipText.text = hoverWeapon != null ? WeaponInfoString(hoverWeapon)
                                                        : ItemInfoString(hoverItem);
            }
            _tooltipGo.SetActive(true);
            PlaceTooltip(mouse);
        }
        else if (_tooltipGo.activeSelf)
        {
            _tooltipGo.SetActive(false);
        }
    }

    /// <summary>本面板的 tooltip 摆位 —— 转发给共用实现（见 <see cref="PlaceTooltipNearMouse"/>）。</summary>
    void PlaceTooltip(Vector2 mouse) => PlaceTooltipNearMouse(_rootRect, _tooltipRect, _canvas, mouse);

    /// <summary>
    /// **摆 tooltip 的共用实现**（背包 / 箱子两个面板同一套，2026-09-25 抽出来）：
    /// **默认出现在鼠标右侧**（靠屏幕右边放不下时自动翻到左侧），并整体夹在屏幕内。
    /// ⚠️ 位置必须用 `ScreenPointToLocalPointInRectangle` 换算 —— 调用方的面板锚点可能是**左侧**（背包）
    ///    也可能是**右上角**（箱子），都不能按"相对屏幕中心的偏移"算
    ///    （那样 tooltip 会跑到屏幕外，2026-09-21 修掉的 bug）。传进来的 `rootRect` 是谁，就算谁的局部坐标。
    /// ⚠️ 调用前先确认 `tipRect` 的 **sizeDelta 是最终尺寸**（本函数按它算翻转和夹取）。
    /// </summary>
    public static void PlaceTooltipNearMouse(RectTransform rootRect, RectTransform tipRect, Canvas canvas, Vector2 mouse)
    {
        if (tipRect == null || rootRect == null) return;

        float sf = canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
        Vector2 size = tipRect.sizeDelta;                      // Canvas 单位
        float wPx = size.x * sf, hPx = size.y * sf;
        float gapPx = 16f * sf;

        // 水平：优先鼠标右侧；右边放不下 → 翻到左侧；最后夹进屏幕
        float leftPx = mouse.x + gapPx;
        if (leftPx + wPx > Screen.width) leftPx = mouse.x - gapPx - wPx;
        leftPx = Mathf.Clamp(leftPx, 0f, Mathf.Max(0f, Screen.width - wPx));

        // 垂直：以鼠标为基准，夹进屏幕
        float cyPx = mouse.y + 10f * sf;
        cyPx = Mathf.Clamp(cyPx, hPx * 0.5f, Mathf.Max(hPx * 0.5f, Screen.height - hPx * 0.5f));

        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rootRect, new Vector2(leftPx, cyPx), null, out var local))
            tipRect.anchoredPosition = local - AnchorPointInParent(tipRect);
    }

    /// <summary>
    /// **子节点"锚点参考点"在父节点局部坐标系里的位置**（⚠️ 2026-09-25 修 tooltip 偏移时加的，别删）。
    ///
    /// 为什么需要：`ScreenPointToLocalPointInRectangle(rootRect, …)` 给的是**相对父节点 pivot** 的局部坐标，
    /// 而 `anchoredPosition` 的语义是「子节点 pivot 相对**子节点自己的锚点参考点**的位置」——
    /// 两者只在「父节点 pivot 恰好落在子节点锚点上」时才相等：
    ///   `实际位置 = 锚点参考点 + anchoredPosition`  →  `anchoredPosition = 目标local - 锚点参考点`
    /// 所以调用处是 **减** 本函数的结果（当初我写成加，实测立刻偏出整整 2×面板宽，探针一眼就看出来了）。
    ///
    /// 为什么以前一直没暴露：背包面板根节点的 pivot 是 **(0, 0.5) 左中**，与 tooltip 的锚点 `(0,0.5)` 重合
    /// （见类里 `rootRt.anchorMin = … pivot = new Vector2(0f, 0.5f)`）→ 本函数恒返回 `(0,0)`。
    /// 而**箱子面板的根节点 pivot 是 (1,1) 右上角**（它贴屏幕右上角）→ 少了它，tooltip 整体偏 `(父宽, 父高/2)`：
    /// 实测 5 格箱子（面板 392×114、画布 scaleFactor=1）时**左边缘偏 -392px、垂直中心偏 -57px**。
    ///
    /// 📌 结论：**别假设调用方的父节点 pivot 是什么** —— 一律减掉这一项，谁都不吃亏。
    /// </summary>
    static Vector2 AnchorPointInParent(RectTransform tipRect)
    {
        var parent = tipRect.parent as RectTransform;
        if (parent == null) return Vector2.zero;
        // 拉伸锚点（anchorMin != anchorMax）时 anchoredPosition 的语义变成"相对锚点矩形中心的偏移"，
        // 本项目 tooltip 全是点锚点 → 直接跳过（真要支持拉伸锚点得另算）。
        if (tipRect.anchorMin != tipRect.anchorMax) return Vector2.zero;

        // ⚠️ `parent.rect` 的坐标原点**就是父节点的 pivot**（xMin = -pivot.x·W，xMax = (1-pivot.x)·W）→
        //    用 Lerp 在 [xMin,xMax]×[yMin,yMax] 上取锚点归一化坐标，得到的正是"父局部坐标系下的锚点位置"，
        //    与上面 `local` 同口径，可直接相减。
        Rect r = parent.rect;
        return new Vector2(
            Mathf.Lerp(r.xMin, r.xMax, tipRect.anchorMin.x),
            Mathf.Lerp(r.yMin, r.yMax, tipRect.anchorMin.y));
    }

    static string WeaponInfoString(WeaponData w)
    {
        if (w == null) return "";
        // 格式约定：**每行只放一项数据**（好扫读，以后加/删条目也不用重排整块）。
        // 单位必须写清（§9 #18 踩过"两个参数单位不同"的坑）：后坐是**米**、散布是**度**、
        // 速度系数是**倍率**、射速给"发/分"。
        // 小数位：散布 0.##、后坐 0.###（瞄准散布 / 瞄准后坐可能很小，如 RPG7 瞄准散布 0.15°、
        // M1911 瞄准横向后坐 0.018m —— 位数不够时显示值会与实际值对不上，误导调参）。
        // 「后坐·瞄准」那行 = 腰射值 × 瞄准倍率（即**乘倍率以后**的实际值）。
        // ⚠️ 行数要和 tooltip 的高度配套：代码侧 = `TipH`，prefab 侧 = `Backpack/Tooltip` 的 sizeDelta，**两处一起改**。
        float rpm = 60f / Mathf.Max(w.fireInterval, 0.001f);
        float aimRecoilUp = w.hipRecoilUp * Mathf.Max(0f, w.aimRecoilUpMultiplier);
        float aimRecoilJitter = w.hipRecoilJitter * Mathf.Max(0f, w.aimRecoilJitterMultiplier);
        return string.Format(
            "{0}\n伤害 {1}\n爆头倍率 {2:0.##}×\n弹匣 {3}\n射速 {4:0}/分（{5:0.00}s）\n换弹 {6:0.0}s\n" +
            "腰射散布 {7:0.##}°\n瞄准散布 {8:0.##}°\n" +
            "后坐·腰射  纵 {9:0.###}m / 横 {10:0.###}m\n后坐·瞄准  纵 {11:0.###}m / 横 {12:0.###}m\n" +
            "瞄准过渡  {13:0.00}s 进 / {14:0.00}s 出\n移动系数  {15:0.00} / 瞄准移动 {16:0.00}",
            w.weaponName, w.damage, w.headshotMultiplier, w.magSize, rpm, w.fireInterval, w.reloadTime,
            w.hipSpread, w.aimSpread,
            w.hipRecoilUp, w.hipRecoilJitter, aimRecoilUp, aimRecoilJitter,
            w.hipToAimTime, w.aimToHipTime,
            w.moveSpeedMultiplier, w.aimMoveSpeedMultiplier);
    }

    /// <summary>
    /// 悬停内容（**通用物品**；背包库存格与**箱子格**共用同一份 —— 2026-09-25 起 public）：
    ///  · 武器 → 直接复用上面那 12 行详细属性
    ///  · 其它物品（血包 / 以后的子弹…）→ 名字（叠了多个时加 ×N）+ 物品定义卡上的说明文字
    /// ⚠️ 说明文字来自 `ItemData.description` —— 这就是「只有图标、没有模型」的物品
    ///    能向玩家讲清自己是什么的地方（新物品记得写上）。
    /// 📌 箱子里的东西和背包里长一样，玩家在"要不要拿"时同样需要这些信息 → `ChestPanel` 直接调它，
    ///    于是"同一个物品在箱子里和背包里显示的文字"永远一致（以后改格式只改这一处）。
    /// </summary>
    public static string ItemInfoString(ItemStack s)
    {
        if (s == null || s.def == null) return "";
        if (s.def is WeaponData w) return WeaponInfoString(w);

        string text = s.def.DisplayName;
        if (s.count > 1) text += "   ×" + s.count;
        if (!string.IsNullOrEmpty(s.def.description)) text += "\n" + s.def.description;
        return text;
    }

    // ==================== 共享资源：贴图 / 图标 / 字体 ====================

    // ---- 资源 / 颜色：优先 GameAssets（拖了就替换），留空 / 默认 → 代码生成的占位 ----
    // ⚠️ 约定：背包的图请用**白色/浅色 + 透明背景**，颜色由代码控制
    //（因为要表达"当前槽高亮"等状态色；代码会把 Image.color 乘到图上）。
    // ⚠️ 下面这些是 `public`（2026-09-22 起）：**HUD 的「快捷栏」用的就是同一套**槽框 / 图标 / 状态色
    //    → 只留一份实现（改 `GameAssets` 或这里，背包与快捷栏一起变）。
    //    ⚠️ 必须是 public（不能是 internal）：编辑器程序集 `HotbarBuilder` 也要用（internal 跨程序集不可见，
    //       同 `HUD.BubbleBgColor` 那批"给编辑器工具用"的约定）。
    //    ⚠️ 生命周期仍归**本类**（`StaticReset` → `BackpackPanel.ResetStatics` 成对销毁 Sprite 与 texture）；
    //       HUD / 工具**不要**去 Destroy 这些 Sprite —— 它们不是对方创建的（见 §4 #25）。
    public static Sprite PanelSprite()
    {
        var a = GameAssets.I;
        if (a != null && a.backpackPanelSprite != null) return a.backpackPanelSprite;
        if (_sprPanel == null) _sprPanel = UIUtil.RoundedRectSprite(128, 18f, 0f);      // 圆角填充
        return _sprPanel;
    }
    public static Sprite FrameSprite()
    {
        var a = GameAssets.I;
        if (a != null && a.backpackFrameSprite != null) return a.backpackFrameSprite;
        if (_sprFrame == null) _sprFrame = UIUtil.RoundedRectSprite(128, 14f, 4f);     // 圆角描边
        return _sprFrame;
    }
    public static Sprite SlotFrameSprite()
    {
        var a = GameAssets.I;
        if (a != null && a.backpackSlotFrameSprite != null) return a.backpackSlotFrameSprite;
        if (_sprSlotFrame == null) _sprSlotFrame = UIUtil.RoundedRectSprite(128, 16f, 5f);
        return _sprSlotFrame;
    }
    public static Sprite MedkitSprite()
    {
        var a = GameAssets.I;
        if (a != null && a.backpackMedkitIconSprite != null) return a.backpackMedkitIconSprite;
        if (_sprCross == null) _sprCross = MakeCrossSprite(48);
        return _sprCross;
    }
    /// <summary>纯色小方块 sprite（占用条用）：圆角图拉到细条上会把圆角压变形，所以条改用这个。</summary>
    static Sprite RectSprite()
    {
        if (_sprRect == null)
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = Color.white;
            tex.SetPixels(px); tex.Apply();
            _sprRect = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
        }
        return _sprRect;
    }

    public static Color PanelColor()      { var a = GameAssets.I; return a != null ? a.backpackPanelColor      : new Color(0.04f, 0.05f, 0.09f, 0.92f); }
    public static Color SlotColor()       { var a = GameAssets.I; return a != null ? a.backpackSlotColor       : new Color(0.32f, 0.36f, 0.44f, 0.9f); }
    // ⚠️ 兜底色必须与 `GameAssets.backpackSlotActiveColor` 的**默认值**保持一致（两处是"同一份默认"的两个副本）。
    //    2026-09-22 审计发现：`GameAssets` 那边已改成亮蓝，这里还是旧的绿色 → 一旦资源表缺失/加载失败，
    //    高亮会"莫名变回绿色"（背包 + HUD 快捷栏一起错）。
    public static Color SlotActiveColor() { var a = GameAssets.I; return a != null ? a.backpackSlotActiveColor : new Color(0.30f, 0.65f, 1f, 1f); }
    public static Color CellColor()       { var a = GameAssets.I; return a != null ? a.backpackCellColor       : new Color(0.16f, 0.18f, 0.26f, 0.92f); }

    /// <summary>血包图标颜色（库存格子里的红十字；与面板底部那枚血包图标同色）</summary>
    public static readonly Color MedkitIconColor = new Color(0.95f, 0.22f, 0.22f);

    /// <summary>
    /// 武器图标（数据驱动）：优先用 WeaponData.icon 里配置的 Sprite（可在 Inspector 拖入）；
    /// 没配置则回落程序生成的形状图标（兜底）。想全局换风格就在各武器资源里配 icon 即可，无需改代码。
    /// </summary>
    public static Sprite WeaponIconSprite(WeaponData w)
    {
        if (w == null) return null;
        if (w.icon != null) return w.icon;   // 数据驱动：配了真实图标就用它
        IconKind kind = IconKindOf(w);
        Sprite s;
        if (_iconCache.TryGetValue(kind, out s)) return s;
        s = MakeWeaponIcon(kind);
        _iconCache[kind] = s;
        return s;
    }

    static IconKind IconKindOf(WeaponData w)
    {
        if (w == null) return IconKind.Generic;
        // 数据驱动优先：配置里显式指定了图标类型就直接用，不靠猜名字
        if (w.iconKind != WeaponData.IconKind.Auto)
        {
            switch (w.iconKind)
            {
                case WeaponData.IconKind.Handgun: return IconKind.Handgun;
                case WeaponData.IconKind.SMG:     return IconKind.SMG;
                case WeaponData.IconKind.Shotgun: return IconKind.Shotgun;
                case WeaponData.IconKind.Sniper:  return IconKind.Sniper;
                case WeaponData.IconKind.Rifle:   return IconKind.Rifle;
                default:                          return IconKind.Generic;
            }
        }
        var n = w.weaponName;
        if (n.Contains("手枪")) return IconKind.Handgun;
        if (n.Contains("冲锋")) return IconKind.SMG;
        if (n.Contains("霰弹")) return IconKind.Shotgun;
        if (n.Contains("狙击")) return IconKind.Sniper;
        if (n.Contains("AK"))  return IconKind.Rifle;
        if (w.gunLength >= 0.85f) return IconKind.Sniper;
        if (w.gunLength >= 0.65f) return IconKind.Shotgun;
        if (w.gunLength >= 0.5f) return IconKind.Rifle;
        return IconKind.Generic;
    }

    // ---- 血包十字 ----
    static Sprite MakeCrossSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float c = (size - 1) * 0.5f;
        float barH = size * 0.16f, barW = size * 0.58f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float a = 0f;
            if (Mathf.Abs(x - c) < barH * 0.5f && Mathf.Abs(y - c) < barW * 0.5f) a = 1f;   // 横杠
            if (Mathf.Abs(y - c) < barH * 0.5f && Mathf.Abs(x - c) < barW * 0.5f) a = 1f;   // 竖杠
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    // ---- 武器形状图标（64×64，枪口朝右，alpha 形状；颜色由 Image.color 上色）----
    static Sprite MakeWeaponIcon(IconKind kind)
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = (x + 0.5f) / size, v = 1f - (y + 0.5f) / size;   // v 上大下小
            float a = ShapeAlpha(kind, u, v);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    /// <summary>矩形包含判断（含边界 1px 羽化，避免像素锯齿）</summary>
    static bool In(float u, float v, float u0, float u1, float v0, float v1)
        => u >= u0 && u <= u1 && v >= v0 && v <= v1;

    static float ShapeAlpha(IconKind kind, float u, float v)
    {
        switch (kind)
        {
            case IconKind.Handgun:   // 短身 + 短管 + 握把
                return (In(u, v, 0.30f, 0.70f, 0.42f, 0.60f) ||
                        In(u, v, 0.70f, 0.90f, 0.46f, 0.56f) ||
                        In(u, v, 0.42f, 0.56f, 0.18f, 0.42f)) ? 1f : 0f;
            case IconKind.SMG:       // 细长身 + 长管 + 弯弹匣 + 握把
                return (In(u, v, 0.24f, 0.84f, 0.42f, 0.58f) ||
                        In(u, v, 0.84f, 0.94f, 0.45f, 0.55f) ||
                        In(u, v, 0.40f, 0.52f, 0.58f, 0.76f) ||
                        In(u, v, 0.54f, 0.64f, 0.22f, 0.42f)) ? 1f : 0f;
            case IconKind.Shotgun:   // 宽身 + 粗管 + 泵动护木
                return (In(u, v, 0.26f, 0.68f, 0.36f, 0.64f) ||
                        In(u, v, 0.68f, 0.93f, 0.44f, 0.56f) ||
                        In(u, v, 0.34f, 0.50f, 0.64f, 0.76f) ||
                        In(u, v, 0.10f, 0.26f, 0.36f, 0.64f)) ? 1f : 0f;
            case IconKind.Sniper:    // 超长管 + 瞄具 + 枪托
                return (In(u, v, 0.10f, 0.94f, 0.45f, 0.55f) ||
                        In(u, v, 0.40f, 0.62f, 0.55f, 0.66f) ||
                        In(u, v, 0.10f, 0.30f, 0.30f, 0.62f)) ? 1f : 0f;
            case IconKind.Rifle:     // 长身 + 长管 + 弯弹匣 + 木托
                return (In(u, v, 0.24f, 0.84f, 0.42f, 0.58f) ||
                        In(u, v, 0.84f, 0.94f, 0.45f, 0.55f) ||
                        In(u, v, 0.40f, 0.52f, 0.58f, 0.80f) ||
                        In(u, v, 0.08f, 0.24f, 0.34f, 0.62f)) ? 1f : 0f;
            default:                 // 通用：菱形
                return (Mathf.Abs(u - 0.5f) + Mathf.Abs(v - 0.5f)) <= 0.24f ? 1f : 0f;
        }
    }

    // ==================== 组件工厂 ====================

    Image AddImage(Transform parent, string name, Vector2 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = size;
        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    Text AddText(Transform parent, string name, Vector2 size, string content, int fontSize, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = size;
        var t = go.AddComponent<Text>();
        UIUtil.ApplyFont(t, UIUtil.Font);   // 字体 + 强制不截断（唯一实现，见 UIUtil 的注释）
        t.fontSize = fontSize;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = color;
        t.text = content;
        t.raycastTarget = false;
        return t;
    }

    // ==================== 外部 Prefab 的查找 / 补资源 ====================

    /// <summary>按相对路径取组件（找不到返回 null）。用于外部 prefab 的节点查找（路径写法与 Transform.Find 一致）。</summary>
    static T Comp<T>(Transform parent, string path) where T : Component
    {
        var t = parent.Find(path);
        return t != null ? t.GetComponent<T>() : null;
    }

    /// <summary>按相对路径取子物体（找不到返回 null）</summary>
    static GameObject Go(Transform parent, string path)
    {
        var t = parent.Find(path);
        return t != null ? t.gameObject : null;
    }

    /// <summary>只给"图还空着"的 Image 补占位图。
    /// ⚠️ 已有 sprite = 用户自己在 prefab 里配的真图 → **绝不覆盖**（这是"prefab 优先"的关键）。</summary>
    static void FillSprite(Image img, Sprite fallback, bool sliced)
    {
        if (img == null || img.sprite != null || fallback == null) return;
        img.sprite = fallback;
        if (sliced) img.type = Image.Type.Sliced;
    }

    /// <summary>
    /// 给外部 prefab 里"抓不到代码生成 Sprite"的 Image 补上占位图（运行时生成的图存不进 prefab，
    /// 所以从游戏里抓出来的 prefab，这些 Image 的 sprite 都是空的）。
    /// 只补空槽位；你在 prefab 里拖了真图就保持不动。
    /// </summary>
    void FillEmptySprites(Transform root)
    {
        FillSprite(Comp<Image>(root, "PanelBg"), PanelSprite(), true);
        FillSprite(Comp<Image>(root, "SortBtn"), FrameSprite(), true);
        FillSprite(Comp<Image>(root, "Tooltip"), PanelSprite(), true);
        FillSprite(Comp<Image>(root, "OccBarBg"), RectSprite(), false);
        FillSprite(_invBarFill, RectSprite(), false);
        FillSprite(Comp<Image>(root, "MedkitIcon"), MedkitSprite(), false);
        FillSprite(_cellTemplate != null ? _cellTemplate.GetComponent<Image>() : null, FrameSprite(), true);

        for (int i = 0; i < SlotCount; i++)
        {
            var slot = root.Find("Slot" + (i + 1));
            if (slot == null) continue;
            FillSprite(Comp<Image>(slot, "Frame"), SlotFrameSprite(), true);
            FillSprite(Comp<Image>(slot, "DropBtn"), FrameSprite(), true);
        }
    }
}