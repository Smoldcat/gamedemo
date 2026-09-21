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
/// 打开背包 → GameState.SetPaused(true)（内部置 Paused + Time.timeScale=0，敌人/子弹冻结）；关闭时只释放自己那次请求。
/// 开关键（Tab/B/Esc）统一在本组件 Update 处理（PlayerController 不再监听，避免同帧双触发）。
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
    Image _invBarFill;    // 底部占用条（已用格数 / 总格数）
    Text _tooltipText;
    GameObject _tooltipGo;
    RectTransform _tooltipRect;   // tooltip 的 RectTransform（BuildUI 缓存，避免每帧 GetComponent）
    RectTransform _rootRect;      // 面板根的 RectTransform（tooltip 位置换算要用）
    WeaponData _lastHoverWeapon;  // 上次显示 tooltip 的武器（鼠标没换武器就不重建字符串）

    // ---- 丢弃二次确认 ----
    int _dropConfirmSlot = -1;      // -1 = 无确认中；否则是槽位号
    float _dropConfirmUntil;

    /// <summary>本次暂停是不是自己申请的。只有自己申请过才在关闭时释放，
    /// 避免把别的系统（将来的 ESC 暂停菜单 / 结算页）的暂停一起解掉。</summary>
    bool _pausedByMe;

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

        // 运行时生成的 Sprite 与它的 Texture2D 是**两个独立对象**（Sprite.Create 不托管纹理），
        // 只销毁 Sprite 会漏掉纹理，所以必须成对销毁。
        DestroySprite(_sprPanel); _sprPanel = null;
        DestroySprite(_sprFrame); _sprFrame = null;
        DestroySprite(_sprSlotFrame); _sprSlotFrame = null;
        DestroySprite(_sprCross); _sprCross = null;
        DestroySprite(_sprRect); _sprRect = null;

        foreach (var s in _iconCache.Values) DestroySprite(s);
        _iconCache.Clear();
    }

    /// <summary>销毁运行时生成的 Sprite 及其纹理（两者独立，必须成对销毁）</summary>
    static void DestroySprite(Sprite s)
    {
        if (s == null) return;
        var tex = s.texture;
        Destroy(s);
        if (tex != null) Destroy(tex);
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
        if (GameState.GameOver && _pausedByMe) { Close(); return; }

        // 开关键：Tab/B 开关（游戏中打开、暂停中关闭）；Esc 仅关闭
        bool toggleKey = Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.B);
        bool escKey = Input.GetKeyDown(KeyCode.Escape);
        if (toggleKey)
        {
            if (GameState.Paused) Close();
            else if (!GameState.GameOver) Open();
        }
        else if (escKey && GameState.Paused)
            Close();

        // 丢弃二次确认超时还原
        if (_dropConfirmSlot >= 0 && Time.unscaledTime >= _dropConfirmUntil)
            ResetDropConfirm();

        // 背包打开时：悬停 tooltip + 确认态按钮文字维护（用 _pausedByMe 判断，别被别的系统的暂停干扰）
        if (_pausedByMe)
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

        var canvasGo = new GameObject("BackpackCanvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        canvasGo.AddComponent<GraphicRaycaster>();   // 背包可点击
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280f, 720f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        _canvas = canvas;

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
            "点击装备 · Tab/Esc 关闭", 12, new Color(0.68f, 0.72f, 0.8f));
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
        ttRt.sizeDelta = new Vector2(260f, 152f);
        ttRt.anchorMin = ttRt.anchorMax = ttRt.pivot = new Vector2(0f, 0.5f);   // 锚在左边：位置按"左边缘 + 垂直中心"给（方便放到鼠标右侧）
        var ttImg = _tooltipGo.AddComponent<Image>();
        ttImg.sprite = sprPanel;
        ttImg.type = Image.Type.Sliced;
        ttImg.color = new Color(0.06f, 0.08f, 0.13f, 0.96f);
        ttImg.raycastTarget = false;
        _tooltipText = AddText(_tooltipGo.transform, "Txt", new Vector2(244f, 140f), "", 12, Color.white);
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
        if (_pausedByMe) return;          // 已经开着，避免重复申请暂停
        _pausedByMe = true;
        GameState.SetPaused(true);        // 统一暂停入口（内部置 Paused + Time.timeScale=0）
        _root.SetActive(true);
        Sfx.OpenInventory();
        if (Crosshair.InstanceUI != null) Crosshair.InstanceUI.SetVisible(false);
        HUD.SetContentVisible(false);   // 隐藏整块 HUD：避免与左侧背包面板重叠（且信息重复）
        ResetDropConfirm();
        Refresh();
    }

    public void Close()
    {
        // 只释放自己申请的暂停：写死 timeScale=1 会把别的系统的暂停一起解掉
        if (_pausedByMe)
        {
            _pausedByMe = false;
            GameState.SetPaused(false);
        }
        _root.SetActive(false);
        Sfx.CloseInventory();
        if (Crosshair.InstanceUI != null) Crosshair.InstanceUI.SetVisible(true);
        HUD.SetContentVisible(true);    // 恢复整块 HUD
        ResetDropConfirm();
    }

    // ==================== 数据刷新 ====================

    void Refresh()
    {
        if (_invGrid == null || _medkitText == null) return;   // 面板还没建完
        if (_pc == null) _pc = PlayerController.Instance;
        var bp = _pc != null ? _pc.backpack : null;

        int cap = bp != null ? bp.Capacity : 20;
        int used = bp != null ? bp.inventory.Count : 0;

        // 标题计数 + 底部占用条
        if (_titleText != null) _titleText.text = "背包   " + used + "/" + cap;
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
                _slotMag[i].text = "弹 " + (isCurrent ? _pc.magAmmo : bp.slots[i].magAmmo) + "/" + w.magSize;
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
            var item = (bp != null && i < bp.inventory.Count) ? bp.inventory[i] : null;

            // 格子来源：有模板就实例化（样式全部来自 prefab），否则代码生成
            GameObject cell;
            Image cellImg;
            if (_cellTemplate != null)
            {
                cell = Instantiate(_cellTemplate, _invGrid.transform);
                cell.name = item != null ? "Inv_" + item.DisplayName : "InvEmpty";
                cell.SetActive(true);
                cellImg = cell.GetComponent<Image>();
                if (cellImg == null) { cellImg = cell.AddComponent<Image>(); cellImg.color = CellColor(); }
            }
            else
            {
                cell = new GameObject(item != null ? "Inv_" + item.DisplayName : "InvEmpty");
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
            cellImg.raycastTarget = item != null;   // 空格子不接点击

            if (item == null) { _invCells.Add(null); continue; }   // 空格子到此为止（只画个框）

            // 图标：优先复用格子里已有的 Icon 子节点（模板带的，位置/尺寸随你在 prefab 里调），没有才代码补一个。
            // 血包是「没有 WeaponData 的占格物品」→ 单独走红十字图标分支，绝不碰 item.weapon（那会是 null）
            bool isMedkit = item.IsMedkit;
            var icon = Comp<Image>(cell.transform, "Icon");
            if (icon == null)
            {
                icon = AddImage(cell.transform, "Icon", new Vector2(42f, 42f), Color.white);
                icon.rectTransform.anchoredPosition = new Vector2(0f, 5f);
            }
            icon.gameObject.SetActive(true);
            icon.sprite = isMedkit ? MedkitSprite() : WeaponIconSprite(item.weapon);
            icon.color = isMedkit ? MedkitIconColor : item.weapon.pickupColor;
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

            // 点击装备到空槽（满则替换当前槽，旧枪掉地上）。
            // ⚠️ 血包格**不挂装备事件**：它不是武器，点它不该进快捷槽（数量留在格子里，靠 Q 键消耗）
            var btn = cell.GetComponent<Button>();
            if (!isMedkit)
            {
                if (btn == null) btn = cell.AddComponent<Button>();
                btn.targetGraphic = cellImg;
                btn.onClick.RemoveAllListeners();
                var name = item.weaponName;
                btn.onClick.AddListener(() => { if (_pc != null) { _pc.EquipFromInventory(name); Sfx.Confirm(); } Refresh(); });
            }
            else if (btn != null) btn.onClick.RemoveAllListeners();

            _invCells.Add(rt);
        }

        // 血包汇总行（血包本体就在上面的库存格子里，这里只是汇总 + 操作提示）
        _medkitText.text = bp != null && bp.Medkits > 0
            ? "血包 ×" + bp.Medkits + "   （每个占 1 格 · 按 Q 使用，需未满血）"
            : "血包 ×0   （地图上的药箱按 E 拾取，每个占 1 格）";

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

    // ==================== Tooltip ====================

    void UpdateTooltip()
    {
        if (_pc == null) _pc = PlayerController.Instance;
        var bp = _pc != null ? _pc.backpack : null;
        Vector2 mouse = Input.mousePosition;

        WeaponData hover = null;
        // 快捷槽
        for (int i = 0; i < SlotCount; i++)
            if (_slotCell[i] != null && bp != null && bp.slots[i].weapon != null &&
                RectTransformUtility.RectangleContainsScreenPoint(_slotCell[i], mouse, null))
            { hover = bp.slots[i].weapon; break; }
        // 库存
        // 上界取两者较小值：_invCells（UI）与 bp.inventory（数据）是靠索引对齐的两个平行数组，
        // 只要有一边没同步（数据改了但没 Refresh）就会越界 → 这里兜住，最坏只是不显示 tooltip
        int invCount = bp != null ? Mathf.Min(_invCells.Count, bp.inventory.Count) : 0;
        if (hover == null && bp != null)
            for (int i = 0; i < invCount; i++)
                if (_invCells[i] != null && bp.inventory[i] != null && !bp.inventory[i].IsMedkit &&
                    RectTransformUtility.RectangleContainsScreenPoint(_invCells[i], mouse, null))
                { hover = bp.inventory[i].weapon; break; }

        if (hover != null)
        {
            // 内容只在悬停的武器变化时重建（鼠标不动不该每帧拼字符串）
            if (!ReferenceEquals(hover, _lastHoverWeapon))
            {
                _lastHoverWeapon = hover;
                _tooltipText.text = WeaponInfoString(hover);
            }
            _tooltipGo.SetActive(true);
            PlaceTooltip(mouse);
        }
        else if (_tooltipGo.activeSelf)
        {
            _tooltipGo.SetActive(false);
        }
    }

    /// <summary>
    /// 摆 tooltip：**默认出现在鼠标右侧**（靠屏幕右边放不下时自动翻到左侧），并整体夹在屏幕内。
    /// ⚠️ 位置必须用 `ScreenPointToLocalPointInRectangle` 换算 —— 面板锚点在**左侧**，
    ///    不能再按"相对屏幕中心的偏移"算（那样 tooltip 会跑到屏幕外，2026-09-21 修掉的 bug）。
    /// </summary>
    void PlaceTooltip(Vector2 mouse)
    {
        if (_tooltipRect == null || _rootRect == null) return;

        float sf = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
        Vector2 size = _tooltipRect.sizeDelta;                 // Canvas 单位
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
                _rootRect, new Vector2(leftPx, cyPx), null, out var local))
            _tooltipRect.anchoredPosition = local;
    }

    static string WeaponInfoString(WeaponData w)
    {
        if (w == null) return "";
        // 格式约定：**每行只放一项数据**（好扫读，以后加/删条目也不用重排整块）。
        // 散布用 0.##（最多 2 位小数）：瞄准散布可能很小（如 RPG7 的 0.15°），
        // 用 0.#（1 位）会把它显示成 0.2° —— 显示值与实际值不符会误导调参。
        // 行数/行高要和 tooltip 的尺寸（BuildUI 里的 sizeDelta）配套，加行时记得一起调。
        return string.Format("{0}\n伤害 {1}\n弹匣 {2}\n射速 {3:0.00}s\n腰射散布 {4:0.##}°\n瞄准散布 {5:0.##}°\n换弹 {6:0.0}s",
            w.weaponName, w.damage, w.magSize, w.fireInterval, w.hipSpread, w.aimSpread, w.reloadTime);
    }

    // ==================== 共享资源：贴图 / 图标 / 字体 ====================

    // ---- 资源 / 颜色：优先 GameAssets（拖了就替换），留空 / 默认 → 代码生成的占位 ----
    // ⚠️ 约定：背包的图请用**白色/浅色 + 透明背景**，颜色由代码控制
    //（因为要表达"当前槽高亮"等状态色；代码会把 Image.color 乘到图上）。
    static Sprite PanelSprite()
    {
        var a = GameAssets.I;
        if (a != null && a.backpackPanelSprite != null) return a.backpackPanelSprite;
        if (_sprPanel == null) _sprPanel = UIUtil.RoundedRectSprite(128, 18f, 0f);      // 圆角填充
        return _sprPanel;
    }
    static Sprite FrameSprite()
    {
        var a = GameAssets.I;
        if (a != null && a.backpackFrameSprite != null) return a.backpackFrameSprite;
        if (_sprFrame == null) _sprFrame = UIUtil.RoundedRectSprite(128, 14f, 4f);     // 圆角描边
        return _sprFrame;
    }
    static Sprite SlotFrameSprite()
    {
        var a = GameAssets.I;
        if (a != null && a.backpackSlotFrameSprite != null) return a.backpackSlotFrameSprite;
        if (_sprSlotFrame == null) _sprSlotFrame = UIUtil.RoundedRectSprite(128, 16f, 5f);
        return _sprSlotFrame;
    }
    static Sprite MedkitSprite()
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

    static Color PanelColor()      { var a = GameAssets.I; return a != null ? a.backpackPanelColor      : new Color(0.04f, 0.05f, 0.09f, 0.92f); }
    static Color SlotColor()       { var a = GameAssets.I; return a != null ? a.backpackSlotColor       : new Color(0.32f, 0.36f, 0.44f, 0.9f); }
    static Color SlotActiveColor() { var a = GameAssets.I; return a != null ? a.backpackSlotActiveColor : new Color(0.35f, 0.85f, 0.42f, 0.95f); }
    static Color CellColor()       { var a = GameAssets.I; return a != null ? a.backpackCellColor       : new Color(0.16f, 0.18f, 0.26f, 0.92f); }

    /// <summary>血包图标颜色（库存格子里的红十字；与面板底部那枚血包图标同色）</summary>
    static readonly Color MedkitIconColor = new Color(0.95f, 0.22f, 0.22f);

    /// <summary>
    /// 武器图标（数据驱动）：优先用 WeaponData.icon 里配置的 Sprite（可在 Inspector 拖入）；
    /// 没配置则回落程序生成的形状图标（兜底）。想全局换风格就在各武器资源里配 icon 即可，无需改代码。
    /// </summary>
    static Sprite WeaponIconSprite(WeaponData w)
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
        t.font = UIUtil.Font;
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