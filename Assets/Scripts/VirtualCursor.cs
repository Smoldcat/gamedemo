using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// **虚拟光标（软件光标）**：非游玩态（背包打开 / 死亡结算）显示的那一个光标。
///
/// 解决的问题（用户 2026-09-22 提出"打开背包时光标能不能出现在准星的位置"）：
///   游玩时是 `Cursor.lockState = Locked` —— 系统光标被**钉死在窗口中心且不可见**，而且
///   **不提供它的绝对位置**。所以"解锁那一刻"光标必然从屏幕中心冒出来，与准星毫无关系。
///   → 不是"错位"，是**两套坐标本来就无关**；而 Unity 没有"设置光标位置"的核心 API。
///
/// 做法（沿用项目"准星屏幕坐标 = 唯一真相"这条铁律，见 §4 #20）：
///   ① 非游玩态**不显示系统光标**，改由本类自绘一个光标；
///      ⚠️ 并且把系统光标设为 **`CursorLockMode.Confined`**（2026-09-24 起）—— 那枚真光标被
///      `ClipCursor` **夹在游戏窗口内**，于是**永远出不了窗口**（不夹的话它会跟本类的光标各走各的：
///      起点 / 夹取 / 换算都不同 → 跑出窗口后系统箭头由桌面画出来，在窗口外点击直接让游戏失焦，见 §4 #31）。
///      ⚠️ **不能改用 `Locked`**：UGUI 在 `Locked` 下会把指针位置强制成 `(-1,-1)` → 任何 UI 都点不中
///      （源码依据见 `Crosshair.ApplyCursorModeNow` 里那段；2026-09-24 踩过一次）。
///   ② 它出现时的位置 **= 打开那一刻的准星位置** → 准星消失、光标在同一处出现（连续感）；
///   ③ 之后用与 `PlayerController.TakeMouseDelta` **完全相同**的鼠标轴增量驱动它
///      → 手感 / 灵敏度天然一致，而且**行程无限**（轴增量不受窗口边界限制，见 §4 #21；
///        也**不受锁定影响** —— 游玩态那套锁定模型读的就是同一条轴，见 `PlayerController.TakeMouseDelta`）；
///   ④ 位置必须**注入 EventSystem**（`BaseInputModule.inputOverride`），否则 UGUI 的射线
///      仍按 `Input.mousePosition` 算 → 光标画在一处、点击命中另一处；
///   ⑤ 外观走 `GameAssets.virtualCursorSprite`（留空 = 程序生成的箭头），
///      **热点取 Sprite 自己的 pivot**（在 Sprite Editor 里拖 pivot 即可指定"指针尖在哪"，无需额外字段）。
///
/// ⚠️ 本类跨界：**视觉那一半是 UI**（走 `UIUtil` 那套规范：`NewOverlayCanvas` / UI 层 / 成对销毁 Sprite），
///    **位置来源与注入 EventSystem 属于输入层**。所以它是「UI 里 sortingOrder 最高的一块画布」
///    （sortingOrder=400，盖住 HUD 0 / 准星 100 / 击杀反馈 150 / 背包、箱子、商店、商店库存 200 / 飘字 300）。
///    ⚠️ 别写"第 N 块画布"这种序号：画布数量会随新面板增加
///       （2026-09-29 注释审查时已经不止 6 块了，而"400 是最高的"这条永远成立）。
///
/// ⚠️ 为什么必须是**独立根画布**、不能塞进现有任何一块：
///    ① 它要盖在最上层；② 打开背包时 `HUD.SetContentVisible(false)` 会把 HUD 那块画布**整个关掉**
///    → 光标绝不能是 HUD 的子物体（否则"一开背包它自己先没了"）；③ 它还得横跨"背包"与"死亡结算"两种状态。
///
/// ⚠️ 逃生开关：`Crosshair.useVirtualCursor = false` → 完全回到 2026-09-22 之前的行为
///    （解锁 + 显示系统光标，光标从屏幕中心出现）。背包打开时游戏是暂停的，
///    可以在 Inspector 里当场关掉、下一帧即恢复可点，不必改代码重编译。
///    ⚠️ 但注意：现在**鼠标是锁在窗口中心的**，要先让鼠标出来才能点 Inspector ——
///    编辑器里按 **Esc**（Unity 会自动释放锁定）或 Alt+Tab 切出去再回来。
/// </summary>
public static class VirtualCursor
{
    static Canvas _canvas;
    static RectTransform _icon;
    static Sprite _sprite;          // 当前用的光标图
    static bool _spriteCreated;     // true = 我们自己生成的箭头（**可**销毁）；false = GameAssets 里的工程资产（**绝不能**销毁，§4 #25）
    static Vector2 _pos;            // 光标屏幕坐标（左下角原点，像素）—— 与准星同一套坐标系
    static bool _active;

    // EventSystem 注入（场景重载后 EventSystem/模块会重建，所以每次都重新核对）
    static BaseInputModule _mod;
    static VirtualInput _vin;

    /// <summary>当前是否处于"虚拟光标"状态。</summary>
    public static bool Active { get { return _active; } }

    /// <summary>光标屏幕坐标（左下角原点，像素）。仅在 <see cref="Active"/> 时有意义。</summary>
    public static Vector2 ScreenPos { get { return _pos; } }

    /// <summary>
    /// **"当前该把哪个位置当成鼠标位置"** —— 全项目 UI 取鼠标位置的唯一入口。
    /// 虚拟光标激活时用它，否则回落真实光标（逃生开关关掉时就是后者）。
    /// ⚠️ 背包的悬停判定 / tooltip 定位必须走这里，别直接读 `Input.mousePosition`，
    ///    否则"光标画在一处、tooltip 出现在另一处"。
    /// </summary>
    public static Vector2 MousePosition
    {
        get { return _active ? _pos : (Vector2)Input.mousePosition; }
    }

    /// <summary>进入非游玩态：显示虚拟光标。**位置继承准星**（这是"连续感"的来源）。
    /// ⚠️ 已激活时不重新播种 —— 否则任何重复调用都会把玩家已经挪走的光标拽回准星位置。</summary>
    public static void Activate()
    {
        EnsureOverride();
        if (_active) return;

        var pc = PlayerController.Instance;
        _pos = pc != null ? pc.CrosshairScreenPos
                          : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        ClampPos();
        _active = true;

        EnsureUI();
        if (_canvas != null)
        {
            _canvas.gameObject.SetActive(true);
            ApplyPos();
        }
    }

    /// <summary>回到游玩态：隐藏虚拟光标（系统光标也保持隐藏，见 Crosshair.ApplyCursorModeNow）。</summary>
    public static void Deactivate()
    {
        if (!_active) return;
        _active = false;
        if (_canvas != null) _canvas.gameObject.SetActive(false);
    }

    /// <summary>每帧驱动（由 <see cref="VirtualCursorDriver"/> 调用，执行顺序早于 EventSystem）。
    /// 换算与 `PlayerController.TakeMouseDelta` 锁定态那一行**完全一致**（`axis × mousePixelScale`）
    /// —— 所以"背包里挪光标"和"游玩时挪准星"是同一个手感，不存在快/慢的落差。
    /// ⚠️ 驱动期间系统光标处于 `CursorLockMode.Confined`（见类注释 ①）：轴增量与光标夹不夹无关，
    ///    所以"真光标被夹在窗口里"和"本类光标自由移动"这两件事互不干扰。</summary>
    public static void Tick()
    {
        if (!_active) return;
        EnsureOverride();   // 场景重载后 EventSystem 会重建，这里兜住（内部有廉价早退）

        var pc = PlayerController.Instance;
        float scale = pc != null ? pc.mousePixelScale : 10f;
        _pos += new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * scale;
        ClampPos();
        ApplyPos();
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）。</summary>
    internal static void ResetStatics()
    {
        _active = false;
        _canvas = null;
        _icon = null;
        _mod = null;
        _vin = null;
        // ⚠️ 只销毁"自己创建的"那张箭头：`GameAssets.virtualCursorSprite` 是**工程资产**，
        //    销毁它等于删资源（§4 #25，与 `Sfx` 不销毁 AudioClip 同一条规矩）。
        if (_spriteCreated) UIUtil.DestroySprite(_sprite);
        _sprite = null;
        _spriteCreated = false;
    }

    // ==================== 内部 ====================

    static void ClampPos()
    {
        _pos.x = Mathf.Clamp(_pos.x, 0f, Screen.width);
        _pos.y = Mathf.Clamp(_pos.y, 0f, Screen.height);
    }

    static void ApplyPos()
    {
        if (_icon == null) return;
        // 与 Crosshair 的定位算法一致：屏幕像素 → "相对屏幕中心的偏移 ÷ scaleFactor"
        float sf = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
        _icon.anchoredPosition = new Vector2(_pos.x - Screen.width * 0.5f,
                                             _pos.y - Screen.height * 0.5f) / sf;
    }

    static void EnsureUI()
    {
        if (_canvas != null) return;

        // sortingOrder 最高的一块画布：必须独立、且在最上层（见类注释里的三条理由）
        _canvas = UIUtil.NewOverlayCanvas("VirtualCursorCanvas", 400);

        // ---- 外观：**资源优先 + 代码兜底**（与 Hotbar / 拾取气泡同一套路）----
        var assets = GameAssets.I;
        var custom = assets != null ? assets.virtualCursorSprite : null;
        if (custom != null) { _sprite = custom; _spriteCreated = false; }   // 工程资产：不销毁
        else { _sprite = MakeArrowSprite(); _spriteCreated = true; }

        var go = new GameObject("VirtualCursor");
        go.transform.SetParent(_canvas.transform, false);
        _icon = go.AddComponent<RectTransform>();
        // 锚点固定在屏幕中心（位置 = "相对屏幕中心的偏移"）；**pivot = 热点**
        _icon.anchorMin = _icon.anchorMax = new Vector2(0.5f, 0.5f);
        // 🔑 热点 = **Sprite 自己的 pivot** → 在 Sprite Editor 里拖 pivot 就能指定"指针尖在哪"，无需额外字段。
        //    程序生成的箭头已把 pivot 定在尖端 → "箭头的尖"正好落在准星原来的位置上（连续感的关键）。
        //    ⚠️ 为什么必须自己读 pivot：UGUI 的 `Image` **不读** Sprite 的 pivot（它只把贴图铺满 RectTransform
        //       的矩形，位置由 **RT 自己的 pivot** 决定）→ 2026-09-22 之前这里是"中心锚点"，点击位置会偏半格。
        _icon.pivot = HotspotOf(_sprite);

        // 尺寸：宽度取配置值，高度按图片宽高比算 → 非正方形的图不会变形
        //（刻意不用 `preserveAspect`：那会让"缩放"与"热点映射"变成两套逻辑，容易对不上）
        float w = Mathf.Max(1f, assets != null ? assets.virtualCursorSize : IconSize);
        float aspect = _sprite.rect.height / Mathf.Max(1f, _sprite.rect.width);
        _icon.sizeDelta = new Vector2(w, w * aspect);

        var img = go.AddComponent<Image>();
        img.raycastTarget = false;      // 纯显示：绝不能挡住任何点击（虽然别的画布本来就射线不到它）
        img.preserveAspect = false;     // 上面已按宽高比算过 sizeDelta
        img.sprite = _sprite;

        // 驱动：`[DefaultExecutionOrder(-1000)]` 保证在 EventSystem 之前跑，
        // 否则本帧的射线会用上一帧的位置（1 帧延迟，快速拖拽时能感觉出来）
        _canvas.gameObject.AddComponent<VirtualCursorDriver>();

        UIUtil.SetLayerRecursively(_canvas.gameObject, UIUtil.UILayer);
    }

    /// <summary>把当前虚拟位置交给 EventSystem（UGUI 的射线才会跟着光标走）。
    /// ⚠️ 只在"模块换了 / 还没挂"时才重挂 —— 场景重载会换掉整个 EventSystem。
    /// ⚠️ **必须用 `AddComponent`，不能用 `new`**：`VirtualInput` 继承 `BaseInput : UIBehaviour : MonoBehaviour`，
    ///    而 `new` 出来的 MonoBehaviour **native 指针为空** → 在 Unity 的 `== null` 检查里**就等于 null**。
    ///    症状极具迷惑性：`mod.inputOverride = xxx` 这行明明执行了，但读回来是 null、
    ///    UGUI 仍按真实光标算 → "光标画在一处、点击命中另一处"，而且**不报任何错**。
    ///    （2026-09-22 实现本轮时用 Play 探针实测到：`mod.input.mousePosition` 返回的是真实光标位置。）</summary>
    static void EnsureOverride()
    {
        var es = EventSystem.current;
        if (es == null) return;
        var mod = es.currentInputModule;
        if (mod == null) return;                                    // 模块还没就绪（EventSystem.OnEnable 尚未跑）
        if (_mod == mod && mod.inputOverride != null) return;        // 已挂好且模块未更换

        if (_vin == null) _vin = es.gameObject.AddComponent<VirtualInput>();   // 挂在 EventSystem 上：随场景一起重建/销毁
        mod.inputOverride = _vin;
        _mod = mod;
    }

    /// <summary>画一个"经典鼠标箭头"（白色 + 深色描边，纯程序生成、零美术资源）。
    /// ⚠️ **pivot 定在箭头尖端** → 激活那一刻，箭头尖正好落在准星原来的位置上（连续感的关键）。</summary>
    static Sprite MakeArrowSprite(int size = 32)
    {
        // 经典指针多边形（y 轴向下、32×32 空间内）
        var pts = new[]
        {
            new Vector2(2f, 2f), new Vector2(2f, 21f), new Vector2(7f, 16.5f),
            new Vector2(11f, 24f), new Vector2(15f, 22f), new Vector2(11f, 14.5f), new Vector2(17.5f, 14.5f)
        };

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        var body = new Color(1f, 1f, 1f, 1f);
        var edge = new Color(0.08f, 0.09f, 0.13f, 1f);   // 与项目深色 UI（backpackCellColor）同一族
        const float r = 1.3f;                             // 描边宽度（约 1px）

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float px = x + 0.5f;
            float py = (size - 1 - y) + 0.5f;             // 贴图 y 向上 → 把多边形翻过来
            Color c = new Color(1f, 1f, 1f, 0f);

            if (InPolygon(pts, px, py)) c = body;
            else
            {
                // 描边：只要"邻域内有多边形"就画深色（比做真正的距离场简单，效果足够）
                if (InPolygon(pts, px + r, py) || InPolygon(pts, px - r, py) ||
                    InPolygon(pts, px, py + r) || InPolygon(pts, px, py - r) ||
                    InPolygon(pts, px + r * 0.7f, py + r * 0.7f) || InPolygon(pts, px - r * 0.7f, py + r * 0.7f) ||
                    InPolygon(pts, px + r * 0.7f, py - r * 0.7f) || InPolygon(pts, px - r * 0.7f, py - r * 0.7f))
                    c = edge;
            }
            tex.SetPixel(x, y, c);
        }
        tex.Apply();

        const float tipX = 2f, tipY = 2f;   // 多边形第一个点 = 箭头尖端（y 向下）
        return Sprite.Create(tex, new Rect(0, 0, size, size),
            new Vector2(tipX / size, (size - 1f - tipY) / size));
    }

    /// <summary>取 Sprite 的**归一化热点**（0~1，相对图片左下角）—— 也就是"指针尖 / 点击生效点在哪"。
    /// ⚠️ `Sprite.pivot` 是"这张图自己 rect 空间里的像素值"，所以要除以 `rect.size` 才归一化
    ///    （Unity 社区通用算法；这里再夹一次 [0,1]，兜住"pivot 被设到图片外"这类异常导入设置）。
    /// 📌 于是换美术时**不需要任何新字段**：用户只要在 Sprite Editor 里把 pivot 拖到箭头尖端。</summary>
    static Vector2 HotspotOf(Sprite s)
    {
        if (s == null) return new Vector2(0.5f, 0.5f);
        var r = s.rect;
        if (r.width <= 0f || r.height <= 0f) return new Vector2(0.5f, 0.5f);
        return new Vector2(Mathf.Clamp01(s.pivot.x / r.width), Mathf.Clamp01(s.pivot.y / r.height));
    }

    /// <summary>奇偶规则判断点是否在多边形内（不受凹凸/绕向影响，箭头这种凹多边形也能用）。</summary>
    static bool InPolygon(Vector2[] p, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
        {
            if (((p[i].y > y) != (p[j].y > y)) &&
                (x < (p[j].x - p[i].x) * (y - p[i].y) / (p[j].y - p[i].y) + p[i].x))
                inside = !inside;
        }
        return inside;
    }

    /// <summary>光标显示宽度的**兜底默认值**（`GameAssets` 里没配时用它；按 1280×720 参考分辨率填 → 随 UI 等比缩放）。
    /// 正式取值是 `GameAssets.virtualCursorSize`（Inspector 可调）。</summary>
    const float IconSize = 24f;
}

/// <summary>虚拟光标的每帧驱动。执行顺序设为很早，**必须早于 EventSystem**，
/// 否则 UGUI 本帧的射线会用上一帧的光标位置（1 帧延迟）。</summary>
[DefaultExecutionOrder(-1000)]
public class VirtualCursorDriver : MonoBehaviour
{
    void Update() { VirtualCursor.Tick(); }
}

/// <summary>
/// 把"虚拟光标位置"喂给 UGUI 的适配器。
/// ⚠️ 依据（已核对 UGUI 1.0.0 源码，见 `Library/PackageCache/com.unity.ugui@1.0.0/Runtime/EventSystem/`）：
///    `BaseInputModule.inputOverride` 是**公开可写**的，其注释写明"可以借此绕开 Input 类、喂自己的输入给 UI"；
///    `StandaloneInputModule`（第 180 / 251 / 252 / 281 行）与 `PointerInputModule`（279 / 281 行）
///    取位置时走的都是 `input.mousePosition`、判定鼠标存在走 `input.mousePresent`
///    → 所以只覆盖这两项就够了；**点击不用管**（`input.GetMouseButtonDown/Up` 仍指向真实硬件，鼠标按钮本来就是同一个）。
/// </summary>
public class VirtualInput : BaseInput
{
    public override Vector2 mousePosition
    {
        get { return VirtualCursor.Active ? VirtualCursor.ScreenPos : base.mousePosition; }
    }

    // 游玩时系统光标被锁+隐藏，某些平台上 mousePresent 会变成 false（那样 UI 完全不收鼠标事件）→ 恒为 true
    public override bool mousePresent { get { return true; } }
}
