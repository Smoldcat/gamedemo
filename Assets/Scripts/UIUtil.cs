using UnityEngine;

/// <summary>
/// UI 公共资源。目前只放**全项目共用的那一份动态字体**。
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
    /// 全局共用字体：优先系统微软雅黑（保证中文显示），失败则回落引擎内置字体。
    /// </summary>
    public static Font Font
    {
        get
        {
            if (_font != null) return _font;

            _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "微软雅黑", "SimHei", "Arial" }, 24);
            _created = _font != null;
            if (_font == null)
            {
                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                _created = false;   // 内置资源是共享的，只读不销毁
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
    /// ⚠️ 运行时生成的 Sprite 与其纹理是**两个独立对象**，调用方若要释放必须**成对销毁**（见 `BackpackPanel.DestroySprite`）。
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
                float ir = Mathf.Max(r - borderPx, 0.01f);
                float ix = Mathf.Clamp(x, ir, size - 1f - ir);
                float iy = Mathf.Clamp(y, ir, size - 1f - ir);
                float idx = x - ix, idy = y - iy;
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
