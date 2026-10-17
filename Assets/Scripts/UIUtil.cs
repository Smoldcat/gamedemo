using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// UI 公共资源：**全项目共用的那一份动态字体** + 运行时生成 Sprite 的工具 + 三个"每个 UI 类都要做"的小动作。
///
/// 为什么要共用：`HUD` / `BackpackPanel` / `DamageText` 原来各自 `Font.CreateDynamicFontFromOSFont(...)` 造一份 ——
/// 一份动态字体内部带一张字形图集纹理，是这套 UI 里最贵的一块资源，三份就是三倍（而且每局都要重建）。
/// 现在统一从这里取。
///
/// ⚠️ 字体由本类**持有并负责释放**（`StaticReset` 会调 `ResetStatics`）。
///   其它组件**不要**去 `Destroy` 它 —— 它不是"你自己创建的"资源（见 §4 #25 的教训）。
/// </summary>
public static class UIUtil
{
    static Font _font;
    static bool _created;   // true = 我们自己创建的动态字体（可释放）；false = 引擎内置兜底（共享资源，绝不能销毁）

    /// <summary>
    /// 全局共用字体，优先级：
    ///   ① **资源总表里配的工程字体**（`GameAssets.uiFont`，推荐）—— 编辑器与运行时用同一份，
    ///      于是"编辑期看到的 UI"就是"运行时的样子"（解决"编辑期用内置占位字体、运行时用系统雅黑，
    ///      两个字体基线不同 → 文字垂直位置对不上"的问题）；
    ///   ② 系统「微软雅黑」（保证中文显示）；
    ///   ③ 引擎内置字体（无中文，仅最后兜底）。
    /// </summary>
    public static Font Font
    {
        get
        {
            if (_font != null) return _font;

            // ① 工程字体（在 GameAssets 里拖入 .ttf/.otf）
            var assets = GameAssets.I;
            if (assets != null && assets.uiFont != null)
            {
                _font = assets.uiFont;
                _created = false;   // 工程资产：绝不能销毁
                return _font;
            }

            // ② 系统字体（保证中文）
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "微软雅黑", "SimHei", "Arial" }, 24);
            _created = _font != null;
            if (_font == null)
            {
                // ③ 引擎内置字体（共享资源，只读不销毁；注意它没有中文字形）
                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                _created = false;
            }
            return _font;
        }
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）：**只销毁自己创建的**那份动态字体。</summary>
    internal static void ResetStatics()
    {
        if (_created && _font != null) Object.Destroy(_font);
        _font = null;
        _created = false;

        // 界面皮肤 / 快捷栏 / 拾取气泡用的那些运行时生成图。
        // ⚠️ 2026-10-17：它们现在**全部收在 `_genSprites` 缓存里** → 这里**统一销毁缓存**即可。
        //    （改之前只销毁 3 个手写字段 `_sprPanel/_sprFrame/_sprSlotFrame`，而 `HUD` 等在别处
        //     直接调 `RoundedRectSprite/CircleSprite` 生成的图**没有任何人销毁** —— 关闭 Reload Domain
        //     后会跨会话累积，是本项目唯一违反"谁创建谁销毁"的地方。）
        foreach (var kv in _genSprites)
            if (kv.Value != null) DestroySprite(kv.Value);
        _genSprites.Clear();

        DestroySprite(_sprRect);      _sprRect = null;
    }

    // ==================== 界面皮肤：贴图 / 颜色 / tooltip 摆位（2026-10-15 从 `BackpackPanel` 搬来） ====================
    // 为什么搬：这些是**全项目共用的格子/面板外观**（背包、HUD 快捷栏、箱子、商店、编辑器工具用的是同一套）——
    // 原来住在 `BackpackPanel` 里，等于"想要一张格子边框图，得先认识整个背包面板"（9 个文件、46 处这么引用）。
    // 现在归 `UIUtil`（UI 公共基座，本来就负责 UI 贴图生成），依赖方向变成"各处 → UIUtil"✅。
    // ⚠️ 约定：图请用**白色/浅色 + 透明背景**，颜色由代码控制（因为要表达"当前槽高亮"等状态色）。
    // ⚠️ 这些必须是 public：编辑器程序集（`HotbarBuilder` / `HudStyleBuilder`）也要用（internal 跨程序集不可见）。
    // ⚠️ 生命周期归**本类** `ResetStatics`（成对销毁 Sprite 与 texture）；调用方**不要**去 Destroy（§4 #25）。

    /// <summary>tooltip 的基准尺寸（背包 / 箱子共用；商店那份比它高一档，见 `ShopUI.TipH`）。
    /// ⚠️ 行数要和 tooltip 高度配套：改这里时 prefab 侧 `Backpack/Tooltip` 的 sizeDelta 一起改。</summary>
    public const float TipW = 260f, TipH = 216f;

    static Sprite _sprRect;

    /// <summary>运行时生成图**缓存**（键 = 参数，例如 `r|128|18.00|0.00` / `c|48`）。
    ///
    /// ⚠️ 为什么要缓存（2026-10-17）：`RoundedRectSprite` / `CircleSprite` 以前**每次调用都 new 一张
    ///    `Texture2D` + 一个 `Sprite`**（HUD 快捷栏 6 个槽就各生成一张**完全相同**的图），
    ///    而它们**没有任何调用方负责销毁** ⇒ 每局都在 `ResetStatics` 清理范围之外，
    ///    关掉 Reload Domain 后会**跨 Play 会话累积**。现在同一组参数只生成一次。
    /// ⚠️ **缓存里的是共享实例** —— 调用方**绝对不能** `Destroy` / `DestroySprite` 它（会连累所有使用者）。
    ///    生命周期归本类 `ResetStatics`。
    /// 📌 键用字符串（含两位小数）而不是元组：可读、且避免浮点相等比较的坑。</summary>
    static readonly System.Collections.Generic.Dictionary<string, Sprite> _genSprites
        = new System.Collections.Generic.Dictionary<string, Sprite>();

    /// <summary>面板底图：优先资源总表（`GameAssets.backpackPanelSprite`），留空 → 圆角填充图。</summary>
    public static Sprite PanelSprite()
    {
        var a = GameAssets.I;
        if (a != null && a.backpackPanelSprite != null) return a.backpackPanelSprite;
        return RoundedRectSprite(128, 18f, 0f);      // 圆角填充（已按参数缓存，重复调用不再生成）
    }

    /// <summary>格子 / 按钮边框：优先资源总表（`GameAssets.backpackFrameSprite`），留空 → 圆角描边图。
    /// ⚠️ 它同时被"丢弃 / 整理按钮"和箱子格子复用（故意的统一描边风格）。</summary>
    public static Sprite FrameSprite()
    {
        var a = GameAssets.I;
        if (a != null && a.backpackFrameSprite != null) return a.backpackFrameSprite;
        return RoundedRectSprite(128, 14f, 4f);     // 圆角描边（已按参数缓存）
    }

    /// <summary>快捷槽边框：优先资源总表（`GameAssets.backpackSlotFrameSprite`），留空 → 圆角描边图。</summary>
    public static Sprite SlotFrameSprite()
    {
        var a = GameAssets.I;
        if (a != null && a.backpackSlotFrameSprite != null) return a.backpackSlotFrameSprite;
        return RoundedRectSprite(128, 16f, 5f);     // 已按参数缓存
    }

    /// <summary>纯色小方块 sprite（占用条用）：圆角图拉到细条上会把圆角压变形，所以条改用这个。</summary>
    public static Sprite RectSprite()
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

    /// <summary>面板底色（背包 / 箱子 / 商店面板共用）：优先资源总表，缺省 → 深蓝黑。</summary>
    public static Color PanelColor()      { var a = GameAssets.I; return a != null ? a.backpackPanelColor      : new Color(0.04f, 0.05f, 0.09f, 0.92f); }
    /// <summary>槽位边框色（普通态）。</summary>
    public static Color SlotColor()       { var a = GameAssets.I; return a != null ? a.backpackSlotColor       : new Color(0.32f, 0.36f, 0.44f, 0.9f); }
    /// <summary>槽位**当前选中**的高亮色（背包与 HUD 快捷栏共用）。
    /// ⚠️ 兜底色必须与 `GameAssets.backpackSlotActiveColor` 的**默认值**一致（两处是"同一份默认"的两个副本；
    ///    2026-09-22 审计发现过一次不一致：资源表那边已改亮蓝、这里还是旧绿色 → 资源缺失时高亮莫名变绿）。</summary>
    public static Color SlotActiveColor() { var a = GameAssets.I; return a != null ? a.backpackSlotActiveColor : new Color(0.30f, 0.65f, 1f, 1f); }
    /// <summary>库存格子底色。</summary>
    public static Color CellColor()       { var a = GameAssets.I; return a != null ? a.backpackCellColor       : new Color(0.16f, 0.18f, 0.26f, 0.92f); }

    /// <summary>金币显示色（背包标题行、商店面板 / 库存面板的金币都用它 —— 只留一份，改这里三处一起变）。</summary>
    public static readonly Color GoldColor = new Color(1f, 0.84f, 0.45f);

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
    /// （见 `BackpackPanel` 里 `rootRt.anchorMin = … pivot = new Vector2(0f, 0.5f)`）→ 本函数恒返回 `(0,0)`。
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

    /// <summary>把 tooltip 摆到鼠标旁（背包 / 箱子 / 商店共用同一份摆位）：
    /// 优先鼠标右侧，右边放不下翻到左侧，最后整体夹进屏幕内。</summary>
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
    /// 启动时预热的字符集（见 <see cref="Prewarm"/>）。
    /// 组成：ASCII（8 把武器名多为 ASCII，如 M1911 / Uzi / AK74 / M4_8 / RPG7 / Bennelli_M4 / M249）
    /// + 常用中文标点 + 全项目 UI 文案里出现的中文（含单位：度 / 秒 / 波，含武器名里的「手枪 / 狙击」等）。
    /// 漏掉某个字不会报错，只是它第一次出现时仍会触发一次图集重建 —— 想彻底消除就往这里补字。
    /// </summary>
    const string PrewarmChars =
        " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~"
        + "∞×°·—…（）【】“”、。？！：；"
        + "WASD移动鼠标瞄准左键射击拾取换弹切枪用血包跑走空手到地上捡一把武器吧槽中"
        + "奔跑模式行走切换击杀第波下剩余敌人生命值已满没了坚持人"
        + "背点击装备丢再确认游戏暂停快捷空按匣库存是图收进这里需未药箱"
        + "伤害射速散布腰度秒重新开始神"
        + "近战兵远程狙击自爆坦克";   // 击杀反馈里的敌人名字（EnemyConfig.displayName）

    /// <summary>预热使用的字号：与项目里承载中文文案的字号对齐（标签 14 / 面板 24 / 中央公告 44）。</summary>
    static readonly int[] PrewarmSizes = { 14, 24, 44 };

    /// <summary>
    /// 预热字形图集：把这些字符在不同字号下一次性请求进字体图集。
    /// 为什么需要：**动态字体图集被全项目所有 Text 共享** —— 任何一个新字被渲染都会重建整张图集，
    /// 于是所有正在显示的文本一起重建（一次性尖峰，常见于公告 / 首次出现的武器名 / 首次爆头飘字）。
    /// 启动时先请求一遍，就把这个尖峰从"运行中"挪到了"加载期"。
    /// ⚠️ 预热会占用图集空间（图集随用字增长）；若将来发现字体图集内存偏大，优先减小 <see cref="PrewarmSizes"/> 的字号数量。
    /// </summary>
    internal static void Prewarm(Font f = null)
    {
        if (f == null) f = Font;
        if (f == null) return;
        for (int i = 0; i < PrewarmSizes.Length; i++)
            f.RequestCharactersInTexture(PrewarmChars, PrewarmSizes[i], FontStyle.Normal);
    }

    // ==================== 公共 Sprite 工具（运行时生成） ====================

    /// <summary>
    /// 生成圆角矩形 Sprite（白色 / 透明底，颜色交给 `Image.color`）。
    /// `borderPx &gt; 0` → 描边（中间挖空）；`= 0` → 实心填充。
    /// ⚠️ 返回的 Sprite **自带九宫格 border = radiusPx** → 把 `Image.type` 设成 `Sliced` 后，拉到任意尺寸圆角都不变形。
    /// ⚠️ **返回值是共享缓存实例**（2026-10-17 起按参数缓存）—— 调用方**绝对不要** `Destroy` / `DestroySprite` 它，
    ///    否则会连累所有使用者；生命周期归 `UIUtil.ResetStatics`。
    /// </summary>
    /// <summary>输出分辨率倍数（**配合 `pixelsPerUnit` 同步放大 ⇒ 视觉尺寸一点不变，只是更细腻**）。
    ///
    /// 为什么要它（2026-10-17 用户两次反馈"边缘毛糙/锯齿"）：画布是 `ScaleWithScreenSize`，
    /// 在 2560×1440 下 `scaleFactor = 2`。
    /// ⚠️ **必须严格大于 `scaleFactor` 才有意义**：第一版只把 `ResMult` 设成 **2** —— 而画布缩放**恰好也是 2**
    ///    ⇒ 1 纹素 = 1 物理像素 ⇒ **等于没有超采样**，圆角仍被量化到像素格子上（用户当场指出"还是有很明显的锯齿"）。
    ///    现在取 **4**：每 1 个画布单位 = 4 个纹素 = **2 个物理像素** ⇒ 圆弧与斜边才有真正的中间灰阶。
    /// ⚠️ 改这个值**必须同时**保证 `pixelsPerUnit = 100 × ResMult`、`border` 也按同倍数放大，否则整张图会变大。
    /// 📌 代价：最大的那几张（128 基础尺寸）会变成 512×512 ≈ 1 MB/张，全套约 **3~4 MB** 显存 —— 桌面端可接受。</summary>
    const int ResMult = 4;

    /// <summary>抗锯齿过渡宽度（**输出像素**，单侧）：`alpha` 在边界两侧各 `AaPx` 个纹素内线性过渡。
    /// ⚠️ 原来这里是**二值阈值**（`d <= r ? 1 : 0`）—— 零抗锯齿，是"毛糙感"的直接来源。
    /// 📌 现在 `ResMult = 4` ⇒ 一个物理像素 = 2 个纹素 ⇒ `AaPx = 2` 正好对应**约 1 个物理像素**的过渡带
    ///    （太窄会重新变成台阶，太宽圆角会发虚）。</summary>
    const float AaPx = 2f;

    public static Sprite RoundedRectSprite(int size = 64, float radiusPx = 14f, float borderPx = 0f)
    {
        // ⚠️ 按参数缓存：同一组 (size, radius, border) 只生成一次（见 `_genSprites`）
        string key = "r|" + size + "|" + radiusPx.ToString("F2") + "|" + borderPx.ToString("F2");
        if (_genSprites.TryGetValue(key, out var cached) && cached != null) return cached;

        // 生成用的实际像素尺寸（判定/尺寸全部在"输出像素"空间算）
        int px = size * ResMult;
        var tex = new Texture2D(px, px, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        float r = Mathf.Clamp(radiusPx * ResMult, 1f, px * 0.5f - 1f);
        float bd = borderPx * ResMult;

        for (int y = 0; y < px; y++)
        for (int x = 0; x < px; x++)
        {
            float cx = Mathf.Clamp(x, r, px - 1f - r);
            float cy = Mathf.Clamp(y, r, px - 1f - r);
            float dx = x - cx, dy = y - cy;
            float d = Mathf.Sqrt(dx * dx + dy * dy);   // 到"最近角圆心"的距离；直边区域即到边的距离

            // 覆盖率法抗锯齿：d < r-AaPx → 1；d > r+AaPx → 0；中间线性（边缘因此是平滑灰阶，不是台阶）
            float a = Mathf.Clamp01(AaPx + (r - d));
            if (bd > 0f)
            {
                // ⚠️ 内圈必须用**同一个 core 矩形**（`cx` / `cy`），只把半径缩到 `r - bd` —— 这样才是"外圈内缩 bd 的环"。
                //    🐞 曾经这里把内圈的 clamp 边界也一起缩成 `ir` → 内圈反而**整个包住**外圈 → 描边图整张全透明
                //       （2026-09-22 实测：borderPx>0 时 0/4096 个不透明像素），所有"框 / 描边"图都不可见。
                //    ⭐ 现在用"外缘覆盖率 − 内缘覆盖率"，**内外两条边都有抗锯齿**。
                float ir = Mathf.Max(r - bd, 0.01f);
                a -= Mathf.Clamp01(AaPx + (ir - d));
            }

            tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(a)));
        }
        tex.Apply();
        // ⚠️ `pixelsPerUnit` 必须 ×ResMult、`border` 用放大后的 r → 画布上的尺寸与九宫格切分**与改动前完全一致**
        var sprite = Sprite.Create(tex, new Rect(0, 0, px, px), new Vector2(0.5f, 0.5f), 100f * ResMult, 0,
            SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        _genSprites[key] = sprite;
        return sprite;
    }

    /// <summary>生成圆形 Sprite（白色 / 透明底）—— 用于"按键圆标"这类圆形底。</summary>
    public static Sprite CircleSprite(int size = 32)
    {
        string key = "c|" + size;
        if (_genSprites.TryGetValue(key, out var cached) && cached != null) return cached;

        int px = size * ResMult;
        var tex = new Texture2D(px, px, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        float c = (px - 1) * 0.5f;
        for (int y = 0; y < px; y++)
        for (int x = 0; x < px; x++)
        {
            float dx = x - c, dy = y - c;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            // 覆盖率法抗锯齿（同 `RoundedRectSprite`）：原来这里是 `inside ? 1 : 0` 的二值阈值 ⇒ 圆周一圈台阶
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(AaPx + (c - d))));
        }
        tex.Apply();
        // ⚠️ 与 `RoundedRectSprite` 同理：PPU ×ResMult ⇒ 画布上的尺寸与改动前一致
        var sprite = Sprite.Create(tex, new Rect(0, 0, px, px), new Vector2(0.5f, 0.5f), 100f * ResMult);
        _genSprites[key] = sprite;
        return sprite;
    }

    // ==================== 全项目 UI 都要做的三件小事（原来在 6~7 处各抄了一份，2026-09-22 收敛到这里） ====================

    /// <summary>
    /// 给一个 Text 注入统一字体，并**强制 `Vertical Overflow = Overflow`**。
    /// ⚠️ 必须 Overflow 的原因（踩过两次）：`Text` 默认 `Truncate`，而"行高"是随**字体**变的
    ///    （思源黑体等中文大行高字体明显大于内置 Arial）→ 一旦「行高 &gt; 文本框高度」，
    ///    Truncate 会把**整段文字一个字都不画**（症状：部分文字整条消失、另一些正常 —— 因为那些框的高度
    ///    是按旧字体摆的，正好卡在临界）。
    /// </summary>
    public static void ApplyFont(Text t, Font f)
    {
        if (t == null) return;
        if (f != null) t.font = f;
        t.verticalOverflow = VerticalWrapMode.Overflow;
    }

    /// <summary>
    /// 只给"图还空着"的 Image 补占位图（**配了真图就不覆盖**）。
    /// 场景预置的节点用它补齐"运行时才能生成的那种图"（生成期做的图存不进场景）。
    /// </summary>
    public static void FillSprite(Image img, Sprite fallback, bool sliced)
    {
        if (img == null || img.sprite != null || fallback == null) return;
        img.sprite = fallback;
        if (sliced) img.type = Image.Type.Sliced;
    }

    /// <summary>
    /// 销毁运行时生成的 Sprite **及其 texture**。
    /// ⚠️ Sprite 与 texture 是**两个独立对象**，必须**成对销毁**（见 §4 #25）：
    ///    只销毁 texture → Sprite 包装对象泄漏；只销毁 Sprite → 纹理泄漏（更贵）。
    /// </summary>
    public static void DestroySprite(Sprite s)
    {
        if (s == null) return;
        var tex = s.texture;
        Object.Destroy(s);
        if (tex != null) Object.Destroy(tex);
    }

    /// <summary>
    /// 生成圆环贴图（白色 + alpha）—— 换弹进度环 / 散布圈 / 爆炸范围圈都用它。
    /// `innerRatio` / `outerRatio` 是相对半径占比；`featherScale` 控制边缘羽化
    /// （细环要给更小的羽化，否则整圈 alpha 会被削淡）。
    /// ⚠️ 2026-09-22 收敛：原来 `Crosshair.MakeRingSprite` 与 `Effects.RingTexture` 是**同一份算法写了两遍**
    ///    （只是参数化程度不同，公式逐字相同）→ 想调"圆环粗细/羽化风格"得改两个文件、还容易改歪一边。
    ///    现在只有这一份，两个调用方共用。
    /// </summary>
    public static Texture2D RingTexture(int size, float innerRatio, float outerRatio, float featherScale = 0.06f)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        float c = (size - 1) * 0.5f;
        float outer = c * outerRatio;
        float inner = c * innerRatio;
        float feather = Mathf.Max(1f, c * featherScale);   // 边缘一点羽化，避免锯齿

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
            float a = Mathf.Clamp01((outer - d) / feather) * Mathf.Clamp01((d - inner) / feather);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        return tex;
    }

    // ==================== 全项目 UI 的统一基准（参考分辨率 / 标准画布） ====================

    /// <summary>
    /// 全项目 UI 的**参考分辨率**：所有 UI 像素参数（字号 / 尺寸 / 间距 / 位移）都按这个基准填，
    /// 运行时由 `CanvasScaler` 等比缩放 → 换分辨率观感不变。
    /// ⚠️ **只有一份**：改这里就是改全项目的基准。
    /// ⚠️ 但**场景里的主 HUD 画布是手搭的**、参数写在场景 YAML 里，代码改不到它 →
    ///    `HUD.CheckCanvasScaler()` 会在进入 Play 时核对一次并告警（防"改了代码侧、忘了场景侧"）。
    /// </summary>
    public static readonly Vector2 RefResolution = new Vector2(1280f, 720f);

    /// <summary>`ScaleWithScreenSize` 的屏幕匹配策略：0.5 = 宽高各占一半（全项目统一）。</summary>
    public const float MatchWidthOrHeight = 0.5f;

    /// <summary>
    /// 建一个**标准 Overlay Canvas**：`ScreenSpaceOverlay` + 指定 `sortingOrder` + 全项目统一的 `CanvasScaler`。
    /// ⚠️ 全项目**所有代码生成的 UI 画布**都必须走这里 —— 2026-09-22 收敛前，这套参数在 4 个文件里各写了一遍，
    ///    改一次要改 4 处、漏一个就是"某块 UI 与其它 UI 缩放不一致"（最难查的那一类）。
    /// ⚠️ 本函数**只**建 Canvas + Scaler；额外件由各调用方自己加（背包要 `GraphicRaycaster`、飘字要设 layer）。
    /// </summary>
    public static Canvas NewOverlayCanvas(string name, int sortingOrder)
    {
        var go = new GameObject(name);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = RefResolution;
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = MatchWidthOrHeight;

        return canvas;
    }

    /// <summary>UI 层（Unity 内置第 5 层）。取不到时回落 5。</summary>
    internal static int UILayer
    {
        get { int l = LayerMask.NameToLayer("UI"); return l >= 0 ? l : 5; }
    }

    // 说明：`SetLayerRecursively` 已于 2026-10-15 **收敛到 `Layers.SetRecursively(go, layer)`** ——
    // 原先"UI 侧一份 + 游戏侧 9 份"，且"是否含未激活子物体 / 层没定义怎么办"各有各的答案。
    // UI 调用点现在写 `Layers.SetRecursively(go, UIUtil.UILayer)`；`UILayer` 仍在本类保留。
}
