using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 箱子面板：屏幕**右上角一行 N 格**，风格照搬背包（同一套槽框 / 颜色 / 图标 / 字体）。
///
/// 与背包的关系（用户 2026-09-24 定）：**两个面板绑成一个整体** ——
///   打开箱子 → 自动把左侧背包面板也打开；关闭任意一个 → **两个一起关**。
///   "暂停 / 隐藏整块 HUD / 隐藏准星"三件事统一由 <see cref="UIState"/> 记账
///   （两个面板各申请一次，只有全部关闭才恢复），所以不会出现"关一个、另一个还开着但 HUD 冒出来了"。
///
/// 取东西：**点击格子 = 尽量把这一格装进背包**（装不下的留在箱子里 + 提示"背包已满"）；
///         另有「全部拿取」按钮（能拿多少拿多少，同样不丢东西）。
///
/// 搜刮（2026-10-26 新增）：打开时先只显示"有几样东西"的未知态（斜纹磨砂），放大镜逐格搜索，
///         搜完一格才显示那一格的内容；搜索期间不能拿未揭示的格子。详见下方「搜刮」区块。
/// ⚠️ **刻意不做鼠标拖拽**：UGUI 拖拽要另写一整套（IBeginDragHandler…）且容易出小 bug，
///    第一期就是"点击转移"；以后确实需要再加。
///
/// ⚠️ 布局是**代码常量**（和 `BackpackPanel` 现状一致：布局暂时不支持在编辑器里拖）。
///    **颜色 / 贴图 / 字体**全部复用 `BackpackPanel` 的 `public static` 访问器与 `UIUtil` →
///    与背包 / 快捷栏永远一致（改 `GameAssets` 的「背包」组，三处一起变）。
/// </summary>
public class ChestPanel : MonoBehaviour
{
    static ChestPanel _inst;

    /// <summary>唯一实例（可能为 null —— 还没被打开过）。</summary>
    public static ChestPanel Instance => _inst;

    // 布局常量（参考分辨率 1280×720，运行时由 CanvasScaler 等比缩放）
    const float Pad = 14f;          // 面板内边距
    const float TitleH = 26f;       // 标题行高
    const float Gap = 6f;           // 标题与格子、格子之间的间距
    const float CellW = InvGridUI.CellW, CellH = InvGridUI.CellH, CellGap = InvGridUI.CellGap;   // 统一度量见 InvGridUI
    const float BtnW = 84f, BtnH = 24f;
    const int CellIconSize = 42;

    Canvas _canvas;
    RectTransform _root;
    Image _bg;
    Text _title;
    RectTransform _grid;
    readonly List<RectTransform> _cells = new List<RectTransform>();

    // ---- 悬停 tooltip（内容 / 摆位都复用 `BackpackPanel` 的 public static 实现，见 UpdateTooltip）----
    GameObject _tipGo;
    RectTransform _tipRect;
    Text _tipText;
    ItemData _lastHoverDef;   // 上次显示 tooltip 的物品定义（悬停物品没换 → 不重建字符串）

    // ---- 搜刮（2026-10-26 新增）----
    // 打开箱子时：有物品但还没揭示的格子先显示"暗色 + 噪点"的未知态（不画图标 / 数量），
    // 放大镜在格子中间沿圆周移动、自身不转；按格子顺序一格一格揭示。
    // 已揭示状态存在 Chest 上（关掉再开保持）；每次打开只把"还没揭示的"重新排队。
    bool _searching;
    readonly List<int> _searchQueue = new List<int>();
    int _searchIndex;
    float _searchTimer;      // 当前格剩余搜索时间
    float _delayLeft;        // 打开后"还没开始搜"的剩余等待
    Image _magnifier;
    RectTransform _magnifierRt;
    float _magnifierAngle;
    float _fadeTimer = -1f;      // ≥0 = 放大镜正在淡出（已走过的秒数）
    Button _takeAllBtn;

    // 运行时生成的贴图（**必须成对销毁**：Sprite + Texture；见 StaticReset / 交接文档 §4 #25）
    static Sprite _unknownSprite;
    static Texture2D _unknownTex;
    static Sprite _magnifierSprite;
    static Texture2D _magnifierTex;

    Chest _chest;
    bool _opened;
    int _openedFrame = -1;   // 打开的帧号（防止"开箱的同一帧又被 F 关掉"，见 Update）

    /// <summary>面板当前是不是开着。</summary>
    public bool IsOpen => _opened;

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

    /// <summary>静态状态清理（由 <see cref="StaticReset"/> 在重开一局时调用）。
    /// ⚠️ 以前是漏登记的：靠 `OnDestroy` 兜底，但异常退出路径下会留下已销毁对象的死引用
    /// （表现 = 重开一局后箱子面板打不开 / 打开的是上一局的壳）。</summary>
    public static void ResetStatics()
    {
        _inst = null;
        DestroyGenerated(ref _unknownSprite, ref _unknownTex);
        DestroyGenerated(ref _magnifierSprite, ref _magnifierTex);
    }

    /// <summary>成对销毁运行时生成的 Sprite + Texture。⚠️ 只销毁 Sprite 会漏纹理、只销毁纹理会留死图 —— 两者必须一起。</summary>
    static void DestroyGenerated(ref Sprite sp, ref Texture2D tex)
    {
        if (sp != null) { if (Application.isPlaying) Destroy(sp); else DestroyImmediate(sp); }
        if (tex != null) { if (Application.isPlaying) Destroy(tex); else DestroyImmediate(tex); }
        sp = null;
        tex = null;
    }

    void Update()
    {
        if (!_opened) return;

        // 死亡时强制关闭（避免重开一局卡在暂停；同时把光标还给结算面板）
        if (GameState.GameOver) { Close(); return; }

        // 搜刮推进：⚠️ 开箱时游戏是暂停的（timeScale=0）→ 必须用 unscaledDeltaTime，
        // 否则计时器永远不动（表现 = 打开箱子后一直卡在未知态）。
        UpdateSearch(Time.unscaledDeltaTime);

        // 悬停描述（与背包同款）：放在最前面 —— 后面有"开箱同帧守卫"会提前 return，别让它把 tooltip 一起挡掉
        // 注意：UpdateSearch 可能刚重建过格子（揭示一格），这里必须用重建后的 _cells。
        UpdateTooltip();

        // 关闭键：**再按一次 F**（Tab / B 由 BackpackPanel 统一处理 —— 那两个面板是一体的）
        // ⚠️ **Esc 刻意不参与**（用户 2026-09-24 定）：两个面板绑成一体，Esc 还能关箱子 = 还能关背包。
        // ⚠️ 必须跳过"打开的那一帧"：开箱是 F、关箱也是 F，而本组件 Update 可能在同一帧**晚于**
        //    PlayerController.Update 执行 → 不加这个守卫就会出现"一按 F 立刻开了又关"。
        if (Time.frameCount == _openedFrame) return;
        if (Input.GetKeyDown(KeyCode.F)) Close();
    }

    // ==================== 打开 / 关闭 ====================

    /// <summary>打开箱子面板（会自动把背包面板也一起打开）。没有实例时按需创建 —— 不需要往场景 / prefab 里预挂。</summary>
    public static void Open(Chest chest)
    {
        if (chest == null) return;
        if (_inst == null)
        {
            var go = new GameObject("ChestPanel");
            go.AddComponent<ChestPanel>();      // AddComponent 会同步触发 Awake（BuildUI + 设 _inst）
        }
        if (_inst != null) _inst.OpenInternal(chest);
    }

    void OpenInternal(Chest chest)
    {
        _chest = chest;
        if (_opened) { Refresh(); BeginSearch(); return; }     // 已经开着（又开了另一个箱子）→ 只换内容并重新排队

        _opened = true;
        _openedFrame = Time.frameCount;          // 记下这一帧（见 Update 里的"同帧守卫"）
        UIState.Push();                         // 暂停 + 隐藏 HUD + 隐藏准星（唯一出口）

        // 与背包"绑成一个整体"：打开箱子 = 同时把背包也打开（不然东西没处放）
        if (BackpackPanel.Instance != null) BackpackPanel.Instance.Open();

        _root.gameObject.SetActive(true);
        Sfx.OpenInventory();
        Refresh();
        BeginSearch();
    }

    /// <summary>关闭（也会把背包一起关掉）。幂等：重复调用无副作用。</summary>
    public void Close()
    {
        if (!_opened) return;
        _opened = false;
        StopSearch();   // 搜索中断：已揭示的保持，未揭示的下次打开从 0 重新搜
        // 收起悬停提示：面板根节点虽然被隐藏了，但 tooltip 自己那个 activeSelf 标记还留着 true，
        // 下次开箱时会在 Update 跑到之前**闪一帧**在旧位置（2026-09-25 顺手修）。
        if (_tipGo != null) _tipGo.SetActive(false);
        _root.gameObject.SetActive(false);
        Sfx.CloseInventory();
        UIState.Pop();

        // ⚠️ 记下"这一帧的 F 已经用掉了"：关闭键与交互键同为 F，而交互焦点（在
        //    PlayerController.Update 末尾跑）与本组件 Update 的执行顺序不确定 ——
        //    不记这一笔，就会出现"按下 F 关箱的**同一帧**又被焦点重新打开"。
        GameState.InteractConsumedFrame = Time.frameCount;

        // 一起关（BackpackPanel.Close 内部也有幂等守卫 → 不会递归打转）
        if (BackpackPanel.Instance != null) BackpackPanel.Instance.Close();

        _chest = null;
    }

    // ==================== 内容刷新 ====================

    void Refresh()
    {
        if (_grid == null) return;

        int slots = _chest != null ? _chest.SlotCount : 0;
        int used = 0;

        // 格子每次重建（数量少，和背包库存格同一套做法）
        for (int i = _grid.childCount - 1; i >= 0; i--) Destroy(_grid.GetChild(i).gameObject);
        _cells.Clear();

        float w = Pad * 2f + slots * CellW + Mathf.Max(0, slots - 1) * CellGap;
        float h = TitleH + Gap + CellH + Pad;
        if (_root != null) _root.sizeDelta = new Vector2(w, h);
        if (_bg != null) _bg.rectTransform.sizeDelta = new Vector2(w, h);

        for (int i = 0; i < slots; i++)
        {
            var stack = _chest != null ? _chest.GetSlot(i) : null;
            bool has = stack != null && !stack.IsEmpty;
            if (has) used++;

            var cell = NewCell(i);
            _cells.Add(cell);
            if (!has) continue;

            // 还没搜出来 → 未知态：只告诉玩家"这格有东西"，不画图标 / 数量、也不挂点击
            // （用户 2026-10-26 定：点未揭示的格子**完全无反应**）
            if (_chest != null && !_chest.IsRevealed(i))
            {
                MarkUnknown(cell);
                continue;
            }

            // 图标 + 颜色：与背包 / 快捷栏同一份实现（改一处三处一起变）
            // ⚠️ 必须 **Center** 一下：`NewImage` 走的 `NewRect` 默认锚点在**格子左上角**，
            //    直接用会把图标摆到格子左上（2026-09-24 修的 bug）。
            var icon = NewImage(cell, "Icon", new Vector2(CellIconSize, CellIconSize), Vector2.zero, Color.white);
            Center(icon.rectTransform);
            icon.sprite = ItemVisuals.IconOf(stack.def);   // 与背包 / 快捷栏**共用同一份实现**
            icon.color = ItemVisuals.ColorOf(stack.def);
            icon.preserveAspect = true;

            // 数量角标（**格子右下角**，叠了多个才显示）
            if (stack.count > 1)
            {
                var cnt = NewText(cell, "Count", new Vector2(CellW - 8f, 16f),
                    Vector2.zero, 12, Color.white, TextAnchor.MiddleRight);
                var cntRt = cnt.rectTransform;
                cntRt.anchorMin = cntRt.anchorMax = new Vector2(1f, 0f);   // 锚在格子右下角（NewRect 的默认锚点在左上）
                cntRt.pivot = new Vector2(1f, 0f);
                cntRt.anchoredPosition = new Vector2(-3f, 3f);
                cnt.text = "×" + stack.count;
            }

            // 点击 = 尽量装进背包
            int idx = i;
            var btn = cell.gameObject.AddComponent<Button>();
            var cellImg = cell.GetComponent<Image>();
            btn.targetGraphic = cellImg;
            btn.onClick.AddListener(() => OnCellClick(idx));
        }

        if (_title != null)
            _title.text = _chest != null ? (_chest.promptLabel + "   " + used + "/" + slots) : "";

        Layers.SetRecursively(_canvas.gameObject, UIUtil.UILayer);
    }

    // ==================== 搜刮（2026-10-26 新增） ====================

    /// <summary>
    /// 开始一次搜刮：把"有东西、但还没揭示"的槽位按**格子顺序**排队。
    /// 空箱 / 已全部揭示 → 直接不排队（用户 2026-10-26 定：没物品就不搜索）。
    /// </summary>
    void BeginSearch()
    {
        StopSearch();
        if (_chest == null) return;

        int slots = _chest.SlotCount;
        for (int i = 0; i < slots; i++)
            if (_chest.GetSlot(i) != null && !_chest.IsRevealed(i))
                _searchQueue.Add(i);

        if (_searchQueue.Count == 0) return;

        _searching = true;
        _searchIndex = 0;
        _delayLeft = FirstDelay();   // 用户 2026-10-26：这个参数 = **打开容器后多久才开始搜刮**（默认 0 = 立刻）
        _searchTimer = 0f;
        _magnifierAngle = 0f;
        _fadeTimer = -1f;

        if (_magnifier != null) _magnifier.gameObject.SetActive(false);   // 还没开始搜 → 先不显示
        if (_takeAllBtn != null) _takeAllBtn.interactable = false;

        if (_delayLeft <= 0f) StartCurrentSlot();
    }

    /// <summary>每帧推进（dt 必须是 unscaledDeltaTime —— 开箱时 timeScale=0）。</summary>
    void UpdateSearch(float dt)
    {
        if (!_searching) { UpdateMagnifierFade(dt); return; }

        // ① 打开后"还没开始搜"的等待（默认 0 = 立刻开始第一格）
        if (_delayLeft > 0f)
        {
            _delayLeft -= dt;
            if (_delayLeft > 0f) return;
            StartCurrentSlot();
            return;
        }

        // ② 放大镜当前格内部的**小幅圆周移动**（自身不转）—— 用户 2026-10-26：它是"这一格正在被搜刮"的提示
        _magnifierAngle += OrbitDegPerSec() * dt;
        UpdateMagnifierPosition();

        // ③ 当前格计时（第一格也要走完整时长 —— 用户 2026-10-26 明确）
        _searchTimer -= dt;
        if (_searchTimer <= 0f) RevealCurrent();
    }

    /// <summary>开始搜"当前格"：放大镜**直接跳到这一格**的中心（用户选 1.b），按这一格的时长计时。</summary>
    void StartCurrentSlot()
    {
        if (!_searching || _chest == null || _searchIndex >= _searchQueue.Count) { EndSearch(); return; }

        if (_magnifier != null)
        {
            _magnifier.color = MagnifierColor();
            _magnifier.gameObject.SetActive(true);
        }
        _magnifierAngle = 0f;
        UpdateMagnifierPosition();
        _searchTimer = SearchSecondsFor(_searchQueue[_searchIndex]);
    }

    /// <summary>当前格搜完：揭示 + 音效 + 重画；然后跳到下一格（没有下一格就收尾）。</summary>
    void RevealCurrent()
    {
        if (!_searching || _chest == null || _searchIndex >= _searchQueue.Count)
        {
            EndSearch();
            return;
        }

        int slot = _searchQueue[_searchIndex];
        _chest.RevealSlot(slot);
        Sfx.ChestReveal();
        Refresh();

        _searchIndex++;
        if (_searchIndex >= _searchQueue.Count) { EndSearch(); return; }

        StartCurrentSlot();
    }

    /// <summary>搜完了：恢复「全部拿取」，放大镜进入淡出。</summary>
    void EndSearch()
    {
        if (!_searching) return;
        _searching = false;
        if (_takeAllBtn != null) _takeAllBtn.interactable = true;
        _fadeTimer = 0f;
    }

    /// <summary>中断搜索（关闭面板 / 换箱子）：隐藏放大镜、恢复按钮；已揭示的保持（存在 Chest 上）。</summary>
    void StopSearch()
    {
        _searching = false;
        _searchQueue.Clear();
        _searchIndex = 0;
        _searchTimer = 0f;
        _delayLeft = 0f;
        _fadeTimer = -1f;
        if (_magnifier != null) _magnifier.gameObject.SetActive(false);
        if (_takeAllBtn != null) _takeAllBtn.interactable = true;
    }

    void UpdateMagnifierFade(float dt)
    {
        if (_fadeTimer < 0f || _magnifier == null) return;

        _fadeTimer += dt;
        float dur = FadeTime();
        float k = dur <= 0f ? 1f : Mathf.Clamp01(_fadeTimer / dur);

        var c = MagnifierColor();
        c.a *= 1f - k;
        _magnifier.color = c;

        if (k >= 1f)
        {
            _magnifier.gameObject.SetActive(false);
            _fadeTimer = -1f;
        }
    }

    /// <summary>放大镜**跳到当前正在搜的那一格**（用户 2026-10-26 选 1.b），并在这一格内部做很小的圆周移动（自身不转）。</summary>
    void UpdateMagnifierPosition()
    {
        if (_magnifierRt == null || _searchQueue.Count == 0 || _searchIndex >= _searchQueue.Count) return;

        int slot = _searchQueue[_searchIndex];
        float cx = Pad + slot * (CellW + CellGap) + CellW * 0.5f;
        float cy = -(TitleH + Gap) - CellH * 0.5f;

        float radius = OrbitRadius();
        float rad = _magnifierAngle * Mathf.Deg2Rad;
        _magnifierRt.anchoredPosition = new Vector2(cx + Mathf.Cos(rad) * radius, cy + Mathf.Sin(rad) * radius);
    }

    /// <summary>未知态：圆角提亮底 + 细噪点（磨砂感），两者都**严格裁在内边框内**。空格子保持原样，两者一眼能分开。</summary>
    void MarkUnknown(RectTransform cell)
    {
        // 斜纹磨砂（用户 2026-10-26 给的参考图）：圆角浅色底 + 45° 斜条纹。
        // ⚠️ 格子边框 `FrameSprite()` 是**纯描边环**（内部 alpha=0）→ 直接改格子的 Image 颜色只会提亮那一圈边框，
        //    内部仍是面板底色。所以这里单独铺一层**圆角填充**（半径 10px = 描边内沿）来提亮内部。
        // ⚠️ **必须四周内缩 4px**（= 描边宽度）：铺满会把这圈淡描边盖住（用户 2026-10-26 指出）。
        const float Inset = 4f;   // = FrameSprite 的描边宽度（RoundedRectSprite(128, 14, 4)）

        var tint = NewImage(cell, "UnknownTint", new Vector2(CellW, CellH), Vector2.zero, new Color(1f, 1f, 1f, 0.10f));
        tint.sprite = UIUtil.RoundedRectSprite(128, 10f, 0f);
        tint.type = Image.Type.Sliced;
        tint.raycastTarget = false;
        StretchInset(tint.rectTransform, Inset);

        var noise = NewImage(cell, "Unknown", new Vector2(CellW, CellH), Vector2.zero, UnknownTint());
        noise.sprite = _unknownSprite;
        noise.raycastTarget = false;
        StretchInset(noise.rectTransform, Inset);
    }

    /// <summary>铺满父节点，但四周内缩 inset（用于避开格子的描边）。</summary>
    static void StretchInset(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    float SearchSecondsFor(int slot)
    {
        var stack = _chest != null ? _chest.GetSlot(slot) : null;
        if (stack != null && stack.def != null && stack.def.searchSeconds > 0f) return stack.def.searchSeconds;
        if (_chest != null && _chest.config != null && _chest.config.searchSecondsPerItem > 0f) return _chest.config.searchSecondsPerItem;
        var ga = GameAssets.I;
        return ga != null ? Mathf.Max(0.05f, ga.chestSearchSecondsPerItem) : 0.6f;
    }

    // ---- 参数（全部来自 GameAssets，运行中可调）----
    float FirstDelay() => GameAssets.I != null ? Mathf.Max(0f, GameAssets.I.chestSearchFirstDelay) : 0f;
    float OrbitDegPerSec() => GameAssets.I != null ? GameAssets.I.chestSearchOrbitDegPerSec : 300f;
    /// <summary>格子内部小圆的半径：保证「半径 + 放大镜半宽 ≤ 格子半高」→ 整枚放大镜始终不超出这一格。</summary>
    float OrbitRadius()
    {
        float ratio = GameAssets.I != null ? GameAssets.I.chestSearchOrbitRadiusRatio : 0.08f;
        float bySize = Mathf.Max(0f, (CellH - MagnifierSize()) * 0.5f);
        return Mathf.Min(Mathf.Max(0f, CellH * ratio), bySize);
    }
    float MagnifierSize()
    {
        float ratio = GameAssets.I != null ? GameAssets.I.chestSearchMagnifierSizeRatio : 0.5f;
        return Mathf.Clamp(CellH * ratio, 8f, CellH);
    }
    float FadeTime() => GameAssets.I != null ? Mathf.Max(0f, GameAssets.I.chestSearchFadeTime) : 0.2f;
    Color MagnifierColor() => GameAssets.I != null ? GameAssets.I.chestSearchMagnifierColor : new Color(1f, 1f, 1f, 0.9f);
    Color UnknownTint() => GameAssets.I != null ? GameAssets.I.chestSearchUnknownTint : new Color(1f, 1f, 1f, 0.40f);

    // ---- 运行时贴图（懒创建；ResetStatics 成对销毁）----

    void EnsureGeneratedSprites()
    {
        if (_unknownSprite == null)
        {
            // 128×128：噪点**自己带圆角**（半径 = 格子内边框的圆角）。
            // ⚠️ 不能用 `Mask`：格子边框 `FrameSprite()` 是**纯描边环**（内部 alpha=0），
            //    Mask 会把噪点（和图标）一起裁成透明 —— 2026-10-26 改之前已经确认过。
            const int N = 128;
            _unknownTex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            _unknownTex.name = "ChestUnknownNoise";
            _unknownTex.filterMode = FilterMode.Bilinear;

            // 描边宽 4px、外圆角 14px → 内沿约 10px。
            // ⚠️ 斜纹层会四周内缩 4px（避开描边），所以按"内缩后的显示尺寸"换算半径（60px @68 格子）。
            float r = N * (10f / (CellH - 8f));
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, r, N - r);
                    float cy = Mathf.Clamp(y + 0.5f, r, N - r);
                    float dx = x + 0.5f - cx;
                    float dy = y + 0.5f - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float shape = Mathf.Clamp01(r - d + 1f);   // 1px 抗锯齿边界
                    // 斜纹磨砂（用户 2026-10-26 给的参考图）：45° 硬边斜条纹、深/浅灰交替。
                    // 周期 24（128 贴图）→ 显示到 68px 格子约 12.7px 一条（深/浅各 6px 多）。
                    // 想更宽/更细：改 period；想对比更强/更弱：改 0.45 / 0.85 两档灰度。
                    float t = Mathf.Repeat((x + y) / 24f, 1f);
                    float shade = t < 0.5f ? 0.45f : 0.85f;
                    _unknownTex.SetPixel(x, y, new Color(shade, shade, shade, shape));
                }
            _unknownTex.Apply();
            _unknownSprite = Sprite.Create(_unknownTex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            _unknownSprite.name = "ChestUnknownNoiseSprite";
        }

        if (_magnifierSprite == null)
        {
            const int N = 128;
            _magnifierTex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            _magnifierTex.name = "ChestMagnifier";
            _magnifierTex.filterMode = FilterMode.Bilinear;
            var clear = new Color(1f, 1f, 1f, 0f);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    _magnifierTex.SetPixel(x, y, clear);

            // 镜片（圆环）+ 手柄（右下）：与用户参考图同向的固定倾斜（只移动、不旋转）
            // 2026-10-26 用户反馈：先"粗一点点"（7→9 / 9→11），后"圈圈再小一点点"（半径 40→36、线宽 9→8、手柄内收）
            DrawRing(_magnifierTex, 52f, 76f, 36f, 8f, Color.white);
            DrawLine(_magnifierTex, new Vector2(78f, 50f), new Vector2(106f, 22f), 10f, Color.white);
            _magnifierTex.Apply();

            _magnifierSprite = Sprite.Create(_magnifierTex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            _magnifierSprite.name = "ChestMagnifierSprite";
        }
    }

    static void DrawRing(Texture2D tex, float cx, float cy, float radius, float width, Color c)
    {
        float inner = radius - width * 0.5f;
        float outer = radius + width * 0.5f;
        for (int y = 0; y < tex.height; y++)
            for (int x = 0; x < tex.width; x++)
            {
                float dx = x + 0.5f - cx;
                float dy = y + 0.5f - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d >= inner && d <= outer) tex.SetPixel(x, y, c);
            }
    }

    static void DrawLine(Texture2D tex, Vector2 a, Vector2 b, float width, Color c)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        float r2 = width * width * 0.25f;
        for (int y = 0; y < tex.height; y++)
            for (int x = 0; x < tex.width; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float t = len2 > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
                Vector2 q = a + ab * t;
                if ((p - q).sqrMagnitude <= r2) tex.SetPixel(x, y, c);
            }
    }

    void OnCellClick(int i)
    {
        if (_chest == null) return;
        var pc = PlayerController.Instance;
        var bp = pc != null ? pc.backpack : null;
        if (bp == null) return;

        var stack = _chest.GetSlot(i);
        if (stack == null || stack.def == null) return;

        int put = bp.AddItems(stack.def, stack.count);      // 能收多少收多少（满了返回 0）
        if (put <= 0) { HUD.AnnounceBackpackFull(); return; }

        Sfx.Confirm();
        _chest.TakeFromSlot(i, put);
        AfterTransfer();
    }

    void OnTakeAllClick()
    {
        if (_chest == null || _searching) return;   // 搜索中按钮已禁用，这里再守一道
        var pc = PlayerController.Instance;
        var bp = pc != null ? pc.backpack : null;
        if (bp == null) return;

        int moved = 0;
        // 固定槽位（2026-10-26）：拿空一格只会把它置 null，**不会前移** → 正序 / 倒序都安全。
        // 仍然倒序，保持与旧实现一致（少一次"想顺序"的心智负担）。
        for (int i = _chest.SlotCount - 1; i >= 0; i--)
        {
            var stack = _chest.GetSlot(i);
            if (stack == null || stack.def == null) continue;
            int put = bp.AddItems(stack.def, stack.count);
            if (put <= 0) continue;
            moved += put;
            _chest.TakeFromSlot(i, put);
            if (_chest == null) break;      // 箱子"拿完消失"了
        }

        Sfx.Confirm();
        if (moved <= 0) HUD.AnnounceBackpackFull();
        else HUD.Announce("收进背包 " + moved + " 件", 1.0f, 26, new Color(0.6f, 1f, 0.7f));
        AfterTransfer();
    }

    /// <summary>转移之后的收尾：箱子没了（拿完消失）就关面板；否则刷新两个面板的格子。</summary>
    void AfterTransfer()
    {
        if (_chest == null) { Close(); return; }   // Unity 的 == 能识别"已销毁"

        // ⚠️ 背包面板是"另一个面板"，库存变了它不会自己知道 → 必须显式通知它重画。
        //    漏了这句的后果：东西确实已经进背包了，但左边那一堆格子要**关掉再打开**才更新（2026-09-24 修的 bug）。
        if (BackpackPanel.Instance != null) BackpackPanel.Instance.RefreshIfOpen();

        Refresh();
    }

    // ==================== 悬停描述（tooltip） ====================

    /// <summary>
    /// 光标停在箱子里某个物品上时弹出描述（用户 2026-09-25 提：**要像背包里一样**）。
    /// 为什么要它：箱子格子和背包格长得一样，但箱子里没有名字 / 属性可看 —— 玩家在"这个要不要拿"上
    /// 只能靠图标猜；箱子配方里可以放武器，那就更需要属性面板。
    ///
    /// 复用（**只有这一份实现，不要各写一套**）：
    ///   · 内容 → <see cref="ItemVisuals.ItemInfoString"/>（武器 = 详细属性；其它 = 名称 + ×N + 说明）
    ///   · 摆位 → <see cref="UIUtil.PlaceTooltipNearMouse"/>（鼠标右侧弹出、贴边自动翻面、夹在屏幕内）
    ///
    /// ⚠️ 鼠标位置必须走 `VirtualCursor.MousePosition`（全项目取鼠标位置的唯一入口）——
    ///    非游玩态这是**虚拟光标**的位置；直接读 `Input.mousePosition` 会按"被锁在屏幕中心的系统光标"判定，
    ///    结果就是"光标画在一处、tooltip 出现在另一处"（背包那边 2026-09-22 修过同一个坑）。
    /// </summary>
    void UpdateTooltip()
    {
        if (_tipGo == null || _tipText == null) return;

        Vector2 mouse = VirtualCursor.MousePosition;

        // 悬停命中判定用**矩形包含**（不是 EventSystem 射线）：tooltip 自己也是 UI，
        // 走射线的话光标一挪到 tooltip 上就会"悬停丢失 → tooltip 闪掉 → 又命中"来回抖（背包同款做法）。
        ItemStack hover = null;
        if (_chest != null)
            for (int i = 0; i < _cells.Count; i++)
            {
                if (!_chest.IsRevealed(i)) continue;                 // 未知态不泄露"是什么"
                var stack = _chest.GetSlot(i);                      // 越界返回 null
                if (stack == null || stack.IsEmpty || stack.def == null) continue;   // 空格子不给 tooltip
                if (_cells[i] != null && RectTransformUtility.RectangleContainsScreenPoint(_cells[i], mouse, null))
                { hover = stack; break; }
            }

        if (hover != null)
        {
            // 内容只在**悬停的物品变了**时才重建（鼠标不动不该每帧拼字符串）
            if (!ReferenceEquals(hover.def, _lastHoverDef))
            {
                _lastHoverDef = hover.def;
                _tipText.text = ItemVisuals.ItemInfoString(hover);   // ← 与背包库存格**同一份**内容
            }
            _tipGo.SetActive(true);
            UIUtil.PlaceTooltipNearMouse(_root, _tipRect, _canvas, mouse);   // ← 与背包**同一份**摆位
        }
        else if (_tipGo.activeSelf)
        {
            _tipGo.SetActive(false);
        }
    }

    // ==================== 构 UI ====================

    void BuildUI()
    {
        // 与背包同一层（sortingOrder 200），但**独立画布**（背包被换成 prefab 也不影响它）；
        // ⚠️ 必须低于 400（虚拟光标在最上层，否则开箱子时看不到光标）
        _canvas = UIUtil.NewOverlayCanvas("ChestCanvas", 200);
        _canvas.gameObject.AddComponent<GraphicRaycaster>();   // 面板可点击（额外件：其余 UI 不需要）

        _root = new GameObject("ChestPanel").AddComponent<RectTransform>();
        _root.SetParent(_canvas.transform, false);
        _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(1f, 1f);   // 右上角
        _root.anchoredPosition = new Vector2(-Pad, -Pad);

        // 面板底图（颜色 / 贴图与背包同源）
        _bg = NewImage(_root, "Bg", new Vector2(200f, 100f), Vector2.zero, UIUtil.PanelColor());
        _bg.sprite = UIUtil.PanelSprite();
        _bg.type = Image.Type.Sliced;
        _bg.raycastTarget = true;      // 拦住穿透点击

        // 标题（左上）
        _title = NewText(_root, "Title", new Vector2(200f, TitleH), new Vector2(Pad, -6f), 16,
            new Color(1f, 0.94f, 0.6f), TextAnchor.MiddleLeft);
        _title.fontStyle = FontStyle.Bold;

        // 「全部拿取」按钮（右上）
        var btnImg = NewImage(_root, "TakeAllBtn", new Vector2(BtnW, BtnH), Vector2.zero, UIUtil.SlotColor());
        btnImg.sprite = UIUtil.FrameSprite();
        btnImg.type = Image.Type.Sliced;
        btnImg.raycastTarget = true;
        var btnRt = btnImg.rectTransform;
        btnRt.anchorMin = btnRt.anchorMax = btnRt.pivot = new Vector2(1f, 1f);
        btnRt.anchoredPosition = new Vector2(-Pad, -6f);
        var btn = btnImg.gameObject.AddComponent<Button>();
        btn.targetGraphic = btnImg;
        btn.onClick.AddListener(OnTakeAllClick);
        _takeAllBtn = btn;
        var btnTxt = NewText(btnImg.rectTransform, "Txt", new Vector2(BtnW, BtnH), Vector2.zero, 13,
            new Color(0.9f, 0.94f, 1f), TextAnchor.MiddleCenter);
        btnTxt.text = "全部拿取";

        // 格子容器（一行）
        _grid = new GameObject("Grid").AddComponent<RectTransform>();
        _grid.SetParent(_root, false);
        _grid.anchorMin = _grid.anchorMax = new Vector2(0f, 1f);
        _grid.pivot = new Vector2(0f, 1f);
        _grid.anchoredPosition = new Vector2(Pad, -(TitleH + Gap));
        _grid.sizeDelta = Vector2.zero;

        // 悬停描述（默认隐藏；内容 / 摆位都复用背包的 public static 实现，见 UpdateTooltip）
        // ⚠️ 必须建在 `_grid` **之后**：uGUI 同层按**兄弟顺序**绘制，后建的盖在上面 →
        //    建在前面的话 tooltip 会被格子挡住（它在屏幕上是弹到鼠标旁的，实际也可能压在面板上）。
        // ⚠️ 尺寸取 `UIUtil.TipW/TipH`（**同一个值**）→ 箱子与背包的 tooltip 一样大、一样够高
        //    （武器那 12 行必须放得下，行数变了也只改背包那一处常量）。
        _tipGo = new GameObject("Tooltip");
        _tipGo.transform.SetParent(_root, false);
        _tipRect = _tipGo.AddComponent<RectTransform>();
        _tipRect.sizeDelta = new Vector2(UIUtil.TipW, UIUtil.TipH);
        // ⚠️ 锚点 / 轴心必须是 **(0, 0.5) = 「左边缘 + 垂直中心」**：`UIUtil.PlaceTooltipNearMouse`
        //    算出来的就是"左边缘 x + 垂直中心 y"（背包那边同款）。写成中心锚点 (0.5,0.5) 会让 tooltip
        //    整体右偏半个宽度、还可能顶出屏幕右边（它按左边缘判断过够不够放）。
        _tipRect.anchorMin = _tipRect.anchorMax = _tipRect.pivot = new Vector2(0f, 0.5f);
        var tipImg = _tipGo.AddComponent<Image>();
        tipImg.sprite = UIUtil.PanelSprite();
        tipImg.type = Image.Type.Sliced;
        tipImg.color = new Color(0.06f, 0.08f, 0.13f, 0.96f);   // 与背包 tooltip 同一底色
        tipImg.raycastTarget = false;   // ⚠️ 必须 false：tooltip 就弹在光标旁边，能挡射线的话格子会点不动
        var tipTxt = NewText(_tipGo.transform, "Txt",
            new Vector2(UIUtil.TipW - 16f, UIUtil.TipH - 12f),
            Vector2.zero, 12, Color.white, TextAnchor.MiddleLeft);
        Center(tipTxt.rectTransform);
        tipTxt.horizontalOverflow = HorizontalWrapMode.Wrap;    // 说明文字 / 属性行要能换行（NewText 默认不换）
        tipTxt.verticalOverflow = VerticalWrapMode.Overflow;
        _tipText = tipTxt;
        _tipGo.SetActive(false);

        // 搜刮放大镜（纯显示、不挡点击）。⚠️ 必须建在格子 / tooltip **之后**：
        // uGUI 同层按兄弟顺序绘制，后建的盖在上面 —— 建在前面会被格子挡住。
        // ⚠️ 朝向固定（用户 2026-10-26 明确：**不要**"钟表指针"那种自转，只沿圆周移动）。
        EnsureGeneratedSprites();
        float magSize = MagnifierSize();
        _magnifier = NewImage(_root, "Magnifier", new Vector2(magSize, magSize), Vector2.zero, MagnifierColor());
        _magnifier.sprite = _magnifierSprite;
        _magnifier.preserveAspect = true;
        _magnifier.raycastTarget = false;
        _magnifierRt = _magnifier.rectTransform;
        _magnifierRt.anchorMin = _magnifierRt.anchorMax = new Vector2(0f, 1f);
        _magnifierRt.pivot = new Vector2(0.5f, 0.5f);
        _magnifier.gameObject.SetActive(false);

        Layers.SetRecursively(_canvas.gameObject, UIUtil.UILayer);
    }

    /// <summary>建一个格子：边框（与背包库存格同一个 sprite / 颜色）+ 点击区域。内容由 Refresh 填。</summary>
    RectTransform NewCell(int index)
    {
        var rt = NewRect(_grid, "Cell" + index, new Vector2(CellW, CellH),
            new Vector2(index * (CellW + CellGap), 0f));
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = UIUtil.FrameSprite();
        img.type = Image.Type.Sliced;
        img.color = UIUtil.CellColor();
        img.raycastTarget = true;
        return rt;
    }

    // ---- 小工具（锚点统一：相对父节点的左上角；pivot 左上）----

    static RectTransform NewRect(Transform parent, string name, Vector2 size, Vector2 pos)
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

    /// <summary>把节点从「贴在父节点左上角」（<see cref="NewRect"/> 的默认锚点）改成**相对父节点居中**。
    /// 格子里的图标这类"内容"必须居中，否则会整体偏左上。</summary>
    static void Center(RectTransform rt)
    {
        if (rt == null) return;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
    }

    static Image NewImage(Transform parent, string name, Vector2 size, Vector2 pos, Color color)
    {
        var rt = NewRect(parent, name, size, pos);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;      // 纯显示（可点击的那几个由调用方再单独打开）
        return img;
    }

    static Text NewText(Transform parent, string name, Vector2 size, Vector2 pos, int fontSize, Color color, TextAnchor anchor)
    {
        var rt = NewRect(parent, name, size, pos);
        var t = rt.gameObject.AddComponent<Text>();
        UIUtil.ApplyFont(t, UIUtil.Font);   // 字体 + 强制不截断（唯一实现）
        t.fontSize = fontSize;
        t.alignment = anchor;
        t.color = color;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        return t;
    }
}
