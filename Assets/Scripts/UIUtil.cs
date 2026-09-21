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
    /// ⚠️ 运行时生成的 Sprite 与其纹理是**两个独立对象**，调用方若要释放必须**成对销毁**（`UIUtil.DestroySprite`）。
    /// </summary>
    public static Sprite RoundedRectSprite(int size = 64, float radiusPx = 14f, float borderPx = 0f)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float r = Mathf.Clamp(radiusPx, 1f, size * 0.5f - 1f);

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float cx = Mathf.Clamp(x, r, size - 1f - r);
            float cy = Mathf.Clamp(y, r, size - 1f - r);
            float dx = x - cx, dy = y - cy;
            bool outer = dx * dx + dy * dy <= r * r;

            float a = 0f;
            if (borderPx > 0f)
            {
                // ⚠️ 内圈必须用**同一个 core 矩形**（`cx` / `cy`），只把半径缩到 `r - borderPx` —— 这样才是"外圈内缩 borderPx 的环"。
                //    🐞 曾经这里把内圈的 clamp 边界也一起缩成 `ir` → 内圈反而**整个包住**外圈 → `outer && !inner` 永远为假
                //       → **描边模式生成出来的图整张全透明**（2026-09-22 实测：borderPx>0 时 0/4096 个不透明像素）。
                //       后果：所有"框 / 描边"图（背包快捷槽框、库存格底、整理按钮、拾取气泡外圈、HUD 快捷栏槽框）
                //       一直不可见 —— 表现为"当前槽高亮怎么调都看不到"。
                float ir = Mathf.Max(r - borderPx, 0.01f);
                float idx = x - cx, idy = y - cy;
                bool inner = idx * idx + idy * idy <= ir * ir;
                if (outer && !inner) a = 1f;
            }
            else if (outer) a = 1f;

            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, new Vector4(r, r, r, r));
    }

    /// <summary>生成圆形 Sprite（白色 / 透明底）—— 用于"按键圆标"这类圆形底。</summary>
    public static Sprite CircleSprite(int size = 32)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - c, dy = y - c;
            bool inside = dx * dx + dy * dy <= c * c;
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, inside ? 1f : 0f));
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
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

    /// <summary>把整棵子树设到 UI 层，保持"所有 UI 都在 UI 层"的一致性。
    /// （`ScreenSpaceOverlay` 下层不影响渲染，纯粹是将来想按层统一管理 / 统计 UI 时更整齐。）</summary>
    internal static void SetLayerRecursively(GameObject go, int layer)
    {
        if (go == null) return;
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++)
            SetLayerRecursively(go.transform.GetChild(i).gameObject, layer);
    }
}
