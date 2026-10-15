using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>
/// HUD：血条、弹药、波次、击杀、**快捷栏**、**拾取气泡**、中央公告、受伤红晕、低血量警示、死亡结算。
/// 需要在 Inspector 里拖入下面这些 UI 引用（漏拖的项会跳过，不会报错）：
///   hpText / hpFill / weaponText / modeText —— 左下角血条与武器信息
///   waveText / killText                     —— 顶部中央波次与击杀数
///   announceText                            —— 屏幕中央公告
///   deathPanel / deathStat / restartBtn     —— 死亡结算面板（面板里的 `DeathText` 标题节点无 Inspector 引用，由编辑器工具按名字找）
///   hotbar                                  —— 底部中央快捷栏（1/2/3 = 武器槽、E = 魔法格、Q = 血包；留空会自动生成）
/// </summary>
public class HUD : MonoBehaviour
{
    public static HUD Instance;

    [Header("左下角")]
    public Text hpText;
    public Image hpFill;
    public Text weaponText;
    public Text modeText;

    [Header("顶部中央")]
    public Text waveText;
    public Text killText;

    [Header("屏幕中央")]
    public Text announceText;

    [Header("死亡面板")]
    public GameObject deathPanel;
    public Text deathStat;
    public Button restartBtn;

    [Header("死亡面板内部元素（可选）")]
    public Text restartBtnLabel;

    [Header("HUD 风格（由菜单 Tools/游戏/优化 HUD 风格 生成并接线；留空 = 该块不做任何改动）")]
    [Tooltip("左下角「状态卡片」根节点（血条 + 血量 / 武器 / 模式）。\n配了它：卡片与描边颜色由代码从 GameAssets「HUD 风格」组驱动，血条描边做低血警示。\n留空 = 这一块完全不动（老样子）")]
    public RectTransform statusCard;
    [Tooltip("顶部「波次卡片」根节点（节点名约定：CardBg / CardFrame）。留空 = 不驱动它")]
    public RectTransform waveCard;
    [Tooltip("顶部「击杀卡片」根节点。留空 = 不驱动它")]
    public RectTransform killCard;
    [Tooltip("死亡结算面板的中央卡片根节点。留空 = 不驱动它")]
    public RectTransform deathCard;

    [Header("字体")]
    [Tooltip("找不到系统雅黑时用这个内置字体兜底")]
    public Font fallbackFont;

    // 中央公告
    string _announce = "";
    float _announceStart, _announceUntil;
    int _announceSize = 44;
    Color _announceColor = new Color(1f, 0.92f, 0.55f);

    // 受伤红晕 / 低血量警示
    Texture2D _vignette;
    Image _vignetteImage;   // 全屏显示渐变红晕（UGUI，代替原来的 OnGUI 绘制）
    float _damageAlpha;

    int _shownWave;
    Health _playerHealth;   // 订阅引用（OnDestroy 退订用）

    // ---- 受伤方向指示（红色箭头，指向伤害来源）----
    Image _damageArrow;
    Canvas _canvas;
    RectTransform _canvasRect;
    Camera _cam;
    Vector3 _damageFromDir;        // 从玩家指向伤害来源（世界方向）

    // ---- 拾取气泡（玩家靠近可拾取物时，在**物体旁边**显示"名称 + 按键圆标"）----
    // 实现思路：**单实例气泡 + 世界坐标投影** —— 只建一个气泡，每帧把目标物体的世界坐标投影成屏幕坐标摆过去。
    // 好处：不必给每个拾取物挂 World Space Canvas（省对象）、天然正对相机、不受物体旋转/缩放影响。
    // 投影写法与"受伤方向箭头"同一套：Camera.WorldToScreenPoint → RectTransformUtility.ScreenPointToLocalPointInRectangle。
    // 界面来源**双轨**（2026-09-21 起）：
    //   ① **场景预置**（推荐）：HUD 上那组「拾取气泡」引用拖了就用它们 —— 颜色 / 字号 / 图片 / 内部尺寸
    //      以**节点上的组件**为准，代码只填「名称 / 按键字母」+ 每帧屏幕位置（+ 下面几个布局数字）。
    //      于是**没进 Play、在编辑器里就能直接调**。场景里没有时用菜单 `Tools/游戏/生成拾取气泡到场景` 一键生成并接线。
    //   ② **代码生成（兜底）**：引用留空 / 不完整 → 运行时自建（与历史版本逐位一致），游戏始终能跑。
    RectTransform _bubbleRect;     // 气泡根（锚在 Canvas 中心）
    Image _bubbleEdge;             // 外圈描边（比气泡大 3px，压在底下）
    Image _bubbleBg;               // 内层填充（浅色圆角）
    Text _bubbleName;              // 物品名
    Image _bubbleKeyBg;            // 按键圆标底（圆形）
    Text _bubbleKeyText;           // 按键字母（E）
    Transform _bubbleTarget;       // 当前被标注的物体（null = 不显示）
    string _bubbleLabel;           // 当前名称（变了才重排宽度）
    string _bubbleKeyChar = "E";
    float _bubbleHeight = 0.5f;    // 本次显示的世界高度偏移（由 ShowPickupBubble 写入）
    float _bubbleUntil;            // 过期时刻（调用方每帧续期）
    bool _bubbleAuthored;          // true = 用的是场景预置节点（代码只填数据、**不改内部布局**）

    // 气泡样式（**只用于「代码生成」兜底路径**；场景预置时以节点上的组件为准）
    public static readonly Color BubbleBgColor      = new Color(0.93f, 0.94f, 0.96f, 0.97f);
    public static readonly Color BubbleEdgeColor    = new Color(0.55f, 0.58f, 0.63f, 0.9f);
    public static readonly Color BubbleTextColor    = new Color(0.11f, 0.13f, 0.17f);
    public static readonly Color BubbleKeyBgColor   = new Color(1f, 1f, 1f, 1f);
    public static readonly Color BubbleKeyTextColor = new Color(0.15f, 0.17f, 0.22f);
    public const int BubbleFontSize = 20;

    [Header("拾取气泡（留空 = 运行时自动生成；可用菜单 Tools/游戏/生成拾取气泡到场景 一键预置）")]
    [Tooltip("气泡根节点（场景预置，挂在 HUD 的 Canvas 下）。\n配了就用它 —— 颜色 / 字号 / 图片 / 内部尺寸以**节点上的组件**为准（编辑期就能调），代码只填「名称 / 按键字母」+ 每帧屏幕位置。\n留空 → 运行时用代码生成一个（与历史版本一致）")]
    public RectTransform pickupBubble;
    [Tooltip("气泡外圈描边（可留空，运行时自动补圆角图；配了真图不覆盖）")]
    public Image pickupBubbleEdge;
    [Tooltip("气泡内层填充（可留空，运行时自动补圆角图）")]
    public Image pickupBubbleBg;
    [Tooltip("物品名文本（配了气泡根节点时**必填**；缺了会自动退回代码生成）")]
    public Text pickupBubbleName;
    [Tooltip("按键圆标底（可留空，运行时自动补圆图）")]
    public Image pickupBubbleKeyBg;
    [Tooltip("按键字母文本（配了气泡根节点时**必填**）")]
    public Text pickupBubbleKeyText;
    [Tooltip("气泡高度（px，参考分辨率 1280×720）。宽度按名字长度自适应，不用手填")]
    public float pickupBubbleHeight = 38f;
    [Tooltip("气泡左右内边距（px）")]
    public float pickupBubblePadX = 16f;
    [Tooltip("物品名与按键圆标之间的间距（px）")]
    public float pickupBubbleGap = 8f;
    [Tooltip("按键圆标直径（px）")]
    public float pickupBubbleKeyDiameter = 24f;
    [Tooltip("气泡相对物体的**屏幕右移**距离（px，按 720 屏高等比缩放）—— 贴到物体右侧、不遮挡物体本身")]
    public float pickupBubbleScreenOffsetX = 78f;
    [Tooltip("气泡相对物体原点的**世界高度偏移**（往上抬一点，避免压住物体本身）")]
    public float pickupBubbleWorldHeight = 0.5f;

    [Header("快捷栏（留空 = 运行时自动生成；可用菜单 Tools/游戏/生成快捷栏到场景 一键预置）")]
    [Tooltip("快捷栏根节点（场景预置，挂在 HUD 的 Canvas 下）。\n" +
             "配了就用它 —— 布局 / 尺寸 / 图片 / 字号以**节点上的组件**为准（编辑期就能拖），\n" +
             "代码只写「图标 + 槽框状态色 + 血包数量」。\n" +
             "⚠️ 颜色由代码统一驱动（读 GameAssets 的「背包」那组颜色 + 拾取气泡的按键圆标配色）→ 节点上调色无效。\n" +
             "留空 → 运行时用代码生成一个（与预置版同构）。\n" +
             "⚠️ 节点名有约定：Slot1..Slot5，每格下面 Bg / Frame / Icon / KeyBg / KeyText，\n" +
             "   最后一格（Q = 血包）额外要 CountBg / CountText。\n" +
             "   （Slot4 = 魔法格，键位文字运行时会动态变成剩余冷却秒数或「被」；Slot5 = 血包）")]
    public RectTransform hotbar;

    // ---- 快捷栏（1/2/3 = 武器快捷槽，Q = 血包）----
    RectTransform _hotbarRect;
    readonly Image[] _hbFrame = new Image[HotbarSlotCount];
    readonly Image[] _hbIcon = new Image[HotbarSlotCount];
    readonly Image[] _hbBg = new Image[HotbarSlotCount];   // 槽底填充（颜色 = 背包的「库存格底色」，可选节点）
    /// <summary>键位文字（每格下方那个小圆角矩形里的字）。武器格恒为 1/2/3；
    /// **魔法格会动态变**（主动 = `E`、冷却中 = 剩余秒数、被动 = `被`）→ 所以必须缓存引用。</summary>
    readonly Text[] _hbKeyText = new Text[HotbarSlotCount];
    Image _hbCountBg;              // 血包数量圆标（只挂在血包格）
    Text _hbCountText;
    Image _hbThrCountBg;           // 投掷物数量圆标（只挂在投掷物格，2026-10-12）
    Text _hbThrCountText;
    int _hbSig = int.MinValue;     // 脏值签名：当前槽 + 血包数 + 前 3 格武器 + 魔法卡 → 变了才重刷（避免每帧重建 Canvas）

    // ---- 血条平滑显示（文字仍显示精确血量）----
    Vector2 _hpFullSize;
    float _hpShown = 1f;

    // ---- 脏值检查缓存：数值没变就不重建字符串（避免每帧产生 GC 垃圾）----
    int _sHp = int.MinValue, _sMaxHp = int.MinValue;
    bool _sHasWeapon, _sReloading;
    int _sMag = int.MinValue, _sMagSize = int.MinValue;
    int _sReserve = int.MinValue;   // 备弹（-1 = 无限：**仅**武器 `ammoKind == None`，如 RPG；0 = 该等级一颗都没有）
    int _sTier = int.MinValue;      // 弹匣当前等级（徽标用；0 = 不分级 / 拿不到卡）
    string _sWeaponName;
    bool _sRunning = true;
    int _sKills = int.MinValue, _sWave = int.MinValue, _sAlive = int.MinValue, _sClearTicks = int.MinValue;
    float _sAnnounceStart = -1f;

    // ---- 击杀反馈 ----
    float _killFlashUntil;
    bool _killFlashing;
    Color _killBaseColor = new Color(1f, 0.92f, 0f);

    // ---- HUD 容器化风格（由 ApplyHudStyle 写入；详见该方法注释）----
    bool _hpFillFilled;                                         // 血条是否走 Filled 模式（没跑过工具 = false → 自动退回旧的"改宽度"写法）
    Image _hpBarFrame;                                          // 血条描边（低血时脉动到警示色）
    Color _styleFrame = new Color(0.32f, 0.36f, 0.44f, 0.9f);   // 卡片 / 血条描边基色（= 快捷槽边框色）
    Color _styleLowHp = new Color(1f, 0.25f, 0.2f, 1f);         // 低血警示色
    Color _styleDim = new Color(0.7f, 0.7f, 0.7f);              // 次要文字色（空手提示等；默认 = 老样子）

    /// <summary>
    /// 显示"拾取气泡"：在**物体旁边**显示一个气泡（物品名 + 按键圆标，如 `AK74 ⓔ`）。
    /// 调用方**每帧调用续期**（超过 0.12s 不调即自动消失），语义与已被它取代的"底部一行提示"一致。
    /// 实现 = 单实例气泡 + 世界坐标投影（见 <see cref="BuildPickupBubble"/> / <see cref="UpdatePickupBubble"/>）；
    /// 投影之后还会**往屏幕右侧推一段**（`pickupBubbleScreenOffsetX`），所以气泡贴在物体右边、不遮挡物体。
    /// `worldHeight` = 相对物体原点的**世界高度偏移**（往上抬一点，避免压住物体本身）；
    /// 传负数（默认）则用 HUD 上的 `pickupBubbleWorldHeight` —— 于是它能在 Inspector 里调。
    /// </summary>
    public static void ShowPickupBubble(Transform target, string label, string key = "E", float worldHeight = -1f)
    {
        if (Instance == null || target == null) return;
        Instance._bubbleTarget = target;
        Instance._bubbleHeight = worldHeight >= 0f ? worldHeight : Instance.pickupBubbleWorldHeight;
        Instance._bubbleKeyChar = string.IsNullOrEmpty(key) ? "E" : key;
        if (!string.Equals(Instance._bubbleLabel, label))
        {
            Instance._bubbleLabel = label;
            Instance.ApplyBubbleText();   // 文字/按键变了才重排宽度（避免每帧算 preferredWidth）
        }
        Instance._bubbleUntil = Time.time + 0.12f;
    }

    /// 中央公告（波次提示 / 拾取反馈等），支持自定义字号和颜色
    public static void Announce(string s, float duration, int size = 44, Color? color = null)
    {
        if (Instance == null) return;
        Instance._announce = s;
        Instance._announceStart = Time.time;
        Instance._announceUntil = Time.time + duration;
        Instance._announceSize = size;
        Instance._announceColor = color ?? new Color(1f, 0.92f, 0.55f);
    }

    /// <summary>
    /// "背包已满"的统一提示。`WeaponPickup`（捡枪）与 `HealthPack`（捡血包）都用它 ——
    /// 文案与配色只有一份，改一处两边一起变（原来两处各写一份字面量，2026-09-22 收敛）。
    /// </summary>
    public static void AnnounceBackpackFull()
        => Announce("背包已满", 1.2f, 28, new Color(1f, 0.62f, 0.4f));

    /// <summary>
    /// 整块 HUD 的显隐（打开背包时用）。
    /// 为什么需要：背包面板是**贴屏幕左侧的竖排面板**（约 400×660），会和 HUD 的**左下角信息块**
    /// （血条 / 弹药 / 武器 / 模式）重叠；而那块信息在背包里本来就是**重复的**（面板自己会显示
    /// 当前武器 / 弹匣 / 血包数）。隐藏后画面也更像"一个独立的背包菜单"。
    /// ⚠️ HUD 组件就挂在**场景那块 Canvas 的根**上 → 这里切的就是那块 Canvas 根，所以整块 HUD
    ///   （含运行时 new 出来的红晕 / 受伤箭头 / 拾取气泡）会一起隐藏；
    ///   **准星 / 背包 / 击杀反馈 / 飘字各自有独立 Canvas，不受影响**。
    /// 恢复由背包关闭时调用；死亡时背包会自动关闭 → 会一并恢复，死亡结算面板照常显示。
    /// </summary>
    public static void SetContentVisible(bool visible)
    {
        if (Instance == null) return;
        var root = Instance._canvas != null ? Instance._canvas.gameObject : Instance.gameObject;
        if (root.activeSelf != visible) root.SetActive(visible);
    }

    void Awake()
    {
        // 单例防护：出现重复实例（如场景重载的极端时序）时丢弃后出现的那个
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // 确保有 EventSystem（UI 按钮需要）
        if (FindObjectOfType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
        }

        // 字体统一走 UIUtil 的**全局共用字体**：原来 HUD / 背包 / 飘字各造一份动态字体
        //（一份就带一张字形图集纹理，是最贵的一块），现在全项目只造一份，并由 UIUtil 负责释放。
        // Inspector 上的 fallbackFont 优先级更高（留空才用共用字体）。
        // ⚠️ 本组件**不要**去 Destroy 这个字体 —— 它不是"自己创建的"资源（见 §4 #25）。
        Font font = fallbackFont != null ? fallbackFont : UIUtil.Font;
        SetAllTextFont(font);
        // 预热字形图集：动态字体在"某个字第一次出现"时会重建图集，而该图集被全项目所有 Text 共享 →
        // 那一下所有文本会一起重建（运行中的偶发尖峰）。启动时先把会用到的字符请求进图集，
        // 把这次重建从"运行中"挪到"加载期"（实现见 UIUtil.Prewarm）。
        UIUtil.Prewarm(font);
        // HUD 用了 Inspector 指定的自定义兜底字体时，共用字体（背包 / 飘字在用）也要预热一次
        if (font != UIUtil.Font) UIUtil.Prewarm();

        // 缓存 Canvas / 血条满宽 / 击杀文字基色：血条填充宽度不再写死（改场景尺寸或换美术都不会画错）
        _canvas = GetComponentInParent<Canvas>();
        _canvasRect = _canvas != null ? _canvas.GetComponent<RectTransform>() : null;
        CheckCanvasScaler();   // 防漂移：场景这块画布是手搭的，核一次缩放参数（不一致只告警、不改）
        if (hpFill != null) _hpFullSize = hpFill.rectTransform.sizeDelta;
        if (killText != null) _killBaseColor = killText.color;

        _vignette = MakeVignette();
        BuildDamageVignette();   // 全屏红晕 Image（UGUI）
        BuildDamageArrow();      // 受伤方向指示箭头（默认隐藏）
        BuildPickupBubble();     // 拾取气泡（靠近可拾取物时显示，默认隐藏）
        BuildHotbar();           // 快捷栏（1/2/3 = 武器槽、Q = 血包；场景预置优先，留空则代码生成）
        ApplyHudStyle();         // HUD 容器化风格（颜色统一从 GameAssets「HUD 风格」组读；引用留空则完全不动）

        // 纯显示元素不需要接收点击：关掉 raycastTarget，每帧 UI 射线检测只剩重启按钮
        DisableDisplayRaycasts();

        // 统一 UI 层：HUD 里运行时 new 出来的红晕 / 受伤箭头默认落在 Default 层，
        // 这里把整块 HUD 子树设回 UI 层，保证"所有 UI 都在 UI 层"（与另外三个 Canvas 一致）。
        if (_canvas != null) UIUtil.SetLayerRecursively(_canvas.gameObject, UIUtil.UILayer);

        // 绑定重启按钮
        if (restartBtn != null) restartBtn.onClick.AddListener(Restart);

#if UNITY_EDITOR
        // 手拖引用自检（仅编辑器，不进包）：漏拖不会报错、只会静默降级，这里主动提示。
        // 其中 restartBtn 漏拖 = 死亡后无法重开（唯一入口），最值得提醒。
        CheckRef(hpFill, nameof(hpFill), "血条永远不会动");
        CheckRef(waveText, nameof(waveText), "看不到波次 / 剩余敌人数");
        CheckRef(deathPanel, nameof(deathPanel), "死亡后不会出现结算面板");
        CheckRef(restartBtn, nameof(restartBtn), "死亡后无法重开游戏");
#endif
    }

#if UNITY_EDITOR
    static void CheckRef(Object o, string field, string symptom)
    {
        if (o == null) Debug.LogWarning($"[HUD] Inspector 引用「{field}」没有拖 → {symptom}。");
    }
#endif

    /// <summary>
    /// **HUD 容器化风格**（唯一实现）：把「快捷栏 / 背包」那套视觉（深色半透明圆角底 + 细圆角描边 + 统一文字色）
    /// 应用到 HUD 的信息块上。颜色**只在开局读一次**（`GameAssets` 的「HUD 风格」组）——
    /// 与快捷栏同一条约定：**颜色由代码驱动，节点上调色无效**（想改色请改资源总表）。
    ///
    /// 三项职责：
    ///   ① 卡片配色：`statusCard` / `waveCard` / `killCard` / `deathCard` 的底与描边（按子节点名 `CardBg` / `CardFrame` 递归找）；
    ///   ② 血条：底色 + 描边（低血脉动在 `Update` 里）+ **探测能否走 Filled 模式**（跑过菜单 `Tools/游戏/优化 HUD 风格` 才会切成 Filled）；
    ///   ③ 文字：血量 / 波次 = 主文字色，模式 / 空手提示 = 次要文字色，死亡按钮文字 = 按钮文字色。
    ///
    /// ⚠️ 引用留空 = 这一块**一个字节都不碰**（老场景 / 没跑过工具的工程照常跑）。
    /// ⚠️ `GameAssets.hudUseNewStyle` 取消勾选 → 整个方法直接返回（= 一键回退开关）。
    /// </summary>
    void ApplyHudStyle()
    {
        var ga = GameAssets.I;
        if (ga != null && !ga.hudUseNewStyle) return;   // 回退开关：一个字节都不碰

        Color card  = ga != null ? ga.hudCardColor    : new Color(0.16f, 0.18f, 0.26f, 0.92f);
        Color frame = ga != null ? ga.hudFrameColor   : new Color(0.32f, 0.36f, 0.44f, 0.9f);
        Color barBg = ga != null ? ga.hudBarBgColor   : new Color(0.09f, 0.11f, 0.17f, 0.95f);
        Color text  = ga != null ? ga.hudTextColor    : new Color(0.9f, 0.93f, 0.98f, 1f);
        Color dim   = ga != null ? ga.hudDimTextColor : new Color(0.62f, 0.68f, 0.8f, 1f);
        _styleFrame = frame;
        _styleLowHp = ga != null ? ga.hudLowHpColor : new Color(1f, 0.25f, 0.2f, 1f);
        _styleDim   = dim;

        StyleCard(statusCard, card, frame);
        StyleCard(waveCard, card, frame);
        StyleCard(killCard, card, frame);
        StyleCard(deathCard, card, frame);

        if (hpText != null) hpText.color = text;
        if (waveText != null) waveText.color = text;
        if (killText != null) killText.color = _killBaseColor;   // 击杀保持自己的语义色（闪白动画的基准，只在 Awake 缓存一次）
        if (deathStat != null) deathStat.color = dim;

        // 血条：底 / 描边 / 填充模式探测
        if (hpFill != null)
        {
            var barRoot = hpFill.transform.parent;
            if (barRoot != null)
            {
                var bgImg = barRoot.GetComponent<Image>();
                if (bgImg != null) bgImg.color = barBg;
                var frameTr = FindDeep(barRoot, "HpFrame");
                _hpBarFrame = frameTr != null ? frameTr.GetComponent<Image>() : null;
                if (_hpBarFrame != null) _hpBarFrame.color = frame;
            }
            // 新风格走 Filled；Type 不是 Filled（= 没跑过工具）时自动退回旧的"改宽度"写法
            _hpFillFilled = hpFill.sprite != null && hpFill.type == Image.Type.Filled;
        }

        // 死亡按钮：底 / 描边 / 文字
        if (restartBtn != null)
        {
            if (restartBtn.targetGraphic is Image btnImg)
                btnImg.color = new Color(frame.r, frame.g, frame.b, 1f);
            var btnFrameTr = FindDeep(restartBtn.transform, "BtnFrame");
            var btnFrameImg = btnFrameTr != null ? btnFrameTr.GetComponent<Image>() : null;
            if (btnFrameImg != null) btnFrameImg.color = UIUtil.SlotActiveColor();
            if (restartBtnLabel != null)
                restartBtnLabel.color = ga != null ? ga.hudButtonTextColor : new Color(0.95f, 0.97f, 1f, 1f);
        }
    }

    /// <summary>
    /// 给一张卡片上色：`CardBg` / `CardFrame` 两个子节点按名字**递归**找（与快捷栏同一套"节点名契约"；
    /// `Transform.Find` 只找直接子节点，层级一变就会拿到 null → 与快捷栏一样用 `FindDeep`）。
    /// 找不到就跳过 —— 结构不完整时只失去这一块的配色，不会报错。
    /// </summary>
    static void StyleCard(RectTransform card, Color bg, Color frame)
    {
        if (card == null) return;
        var bgTr = FindDeep(card, "CardBg");
        var bgImg = bgTr != null ? bgTr.GetComponent<Image>() : null;
        if (bgImg != null) bgImg.color = bg;
        var frameTr = FindDeep(card, "CardFrame");
        var frameImg = frameTr != null ? frameTr.GetComponent<Image>() : null;
        if (frameImg != null) frameImg.color = frame;
    }

    /// <summary>
    /// 核对**主 HUD 这块画布**（手搭在场景里、参数写在场景 YAML 里）的 `CanvasScaler` 是否与全项目基准一致。
    /// 为什么需要：另外 4 块 UI 画布都是代码用 `UIUtil.NewOverlayCanvas` 建的（参考分辨率只有一份），
    /// 而**这一块是手搭的、代码改不到它** —— 一旦有人只改了代码侧或只改了场景侧，
    /// 就会出现"HUD 与其它 UI 缩放不一致"（观感不一致，且极难看出是哪边错了）。
    /// ⚠️ **只告警、不改场景**：不替用户改他摆好的画布，避免"悄悄改坏 UI"。
    /// </summary>
    void CheckCanvasScaler()
    {
        if (_canvas == null) return;
        var s = _canvas.GetComponent<CanvasScaler>();
        if (s == null)
        {
            Debug.LogWarning("[HUD] 主 HUD 画布上没有 CanvasScaler → 它不会随分辨率缩放（其它 UI 会）→ 观感会不一致。");
            return;
        }
        if (s.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize
            || s.screenMatchMode != CanvasScaler.ScreenMatchMode.MatchWidthOrHeight
            || s.referenceResolution != UIUtil.RefResolution
            || !Mathf.Approximately(s.matchWidthOrHeight, UIUtil.MatchWidthOrHeight))
            Debug.LogWarning($"[HUD] 主 HUD 画布的 CanvasScaler 与全项目基准不一致：期望 " +
                             $"ScaleWithScreenSize / MatchWidthOrHeight / {UIUtil.RefResolution} / {UIUtil.MatchWidthOrHeight}，" +
                             $"实际 {s.uiScaleMode} / {s.screenMatchMode} / {s.referenceResolution} / {s.matchWidthOrHeight} " +
                             $"→ 会与其余 4 块 UI 的缩放不一致。");
    }

    /// <summary>创建全屏受伤红晕：挂在 HUD 所在 Canvas 下并置底，用径向渐变贴图做 sprite（保留原中心透明效果）</summary>
    void BuildDamageVignette()
    {
        var canvas = _canvas != null ? _canvas : GetComponentInParent<Canvas>();
        if (canvas == null) return;

        var vgo = new GameObject("DamageVignette");
        vgo.transform.SetParent(canvas.transform, false);
        vgo.transform.SetAsFirstSibling();   // 最底层：死亡面板等 UI 能盖住它

        _vignetteImage = vgo.AddComponent<Image>();
        _vignetteImage.raycastTarget = false;   // 纯显示，不挡任何点击
        if (_vignette != null)
            _vignetteImage.sprite = Sprite.Create(_vignette, new Rect(0, 0, _vignette.width, _vignette.height), new Vector2(0.5f, 0.5f));
        _vignetteImage.color = new Color(1f, 1f, 1f, 0f);

        var rt = _vignetteImage.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;   // 铺满整个 Canvas
    }

    void Start()
    {
        // 受伤红晕改为事件驱动：订阅玩家的受伤事件，而不是每帧比较血量
        // （场景物体 Start 一定在所有 Awake 之后执行，此时 PlayerController.Instance 已就位）
        var pc = PlayerController.Instance;
        if (pc != null)
        {
            _playerHealth = pc.GetComponent<Health>();
            if (_playerHealth != null)
            {
                _playerHealth.OnDamaged += OnPlayerDamaged;
                _hpShown = Mathf.Clamp01(_playerHealth.hp / Mathf.Max(_playerHealth.maxHp, 1f));
            }
        }
    }

    /// <summary>玩家受伤回调：触发受伤红晕，并记录来向用于方向指示（fromDir 是伤害传播方向，取反后指向攻击者）</summary>
    void OnPlayerDamaged(float dmg, Vector3 fromDir)
    {
        _damageAlpha = 1f;
        _damageFromDir = -fromDir;
    }

    void OnDestroy()
    {
        if (_playerHealth != null) _playerHealth.OnDamaged -= OnPlayerDamaged;
        // 运行时生成的 Sprite / 贴图不会随物体销毁，必须手动释放，否则场景重载会累积。
        // ⚠️ **Sprite 与它的 texture 要成对销毁**（用 `UIUtil.DestroySprite`）—— 原来这里只销毁了 texture，
        //    Sprite 包装对象每次重载都漏一个（2026-09-22 审计发现，违反 §4 #25 自己的规则）。
        // （字体是 UIUtil 的共用资源，由它统一释放，这里不能碰）
        if (_vignetteImage != null && _vignetteImage.sprite != null)
            UIUtil.DestroySprite(_vignetteImage.sprite);   // 它用的纹理就是 `_vignette`，一起被销毁
        else if (_vignette != null)
            Destroy(_vignette);
        if (_damageArrow != null) UIUtil.DestroySprite(_damageArrow.sprite);
    }

    void SetAllTextFont(Font f)
    {
        // 「注入统一字体 + 强制不截断」的唯一实现在 `UIUtil.ApplyFont`（快捷栏 / 拾取气泡的预置节点也走它）
        foreach (var t in new Text[] { hpText, weaponText, modeText, waveText, killText, announceText, deathStat, restartBtnLabel })
            UIUtil.ApplyFont(t, f);
    }

    /// <summary>死亡后点「重新开始」：重载当前场景，开始新一局</summary>
    void Restart()
    {
        GameState.ClearPause();   // 保险：清掉所有暂停请求并恢复时间（重开时绝不能卡在暂停）
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    void Update()
    {
        var pc = PlayerController.Instance;
        if (pc == null) return;

        var health = _playerHealth;   // 缓存引用（Start 时已取），避免每帧 GetComponent
        float ratio = health != null ? Mathf.Clamp01(health.hp / health.maxHp) : 0f;

        // 受伤红晕消退（触发在订阅事件里，见 Start()）
        _damageAlpha = Mathf.MoveTowards(_damageAlpha, 0f, Time.deltaTime * 1.4f);

        // 红晕 alpha = 受伤红晕 + 低血量脉动（UGUI 全屏 Image，代替原 OnGUI 绘制）
        float lowHp = 0f;
        if (health != null && health.alive && ratio < 0.35f)
            lowHp = (0.35f - ratio) / 0.35f * 0.5f * (0.75f + 0.25f * Mathf.Sin(Time.time * 5f));
        if (_vignetteImage != null)
        {
            // 空闲（alpha≈0）时直接**禁用**这张全屏 Image：否则它会以 alpha=0 常驻提交，
            // 白白产生一层全屏 overdraw（实测它是 HUD 里唯一常驻激活的全屏图）。
            // 注意：重新启用时要在同一帧把 color 写回去（下面是 `if (vOn)`）。
            float va = Mathf.Clamp01(_damageAlpha * 0.9f + lowHp);
            bool vOn = va > 0.002f;
            if (_vignetteImage.enabled != vOn) _vignetteImage.enabled = vOn;
            if (vOn) _vignetteImage.color = new Color(1f, 1f, 1f, va);
        }

        // 受伤方向指示（有伤害来源时显示，随红晕一起淡出）
        UpdateDamageArrow(pc);

        // 拾取气泡：跟随"最近可拾取物"的屏幕位置（调用方每帧 ShowPickupBubble 续期）
        UpdatePickupBubble();

        // 快捷栏：图标 / 当前槽高亮 / 血包数量（内部有脏值检查，不变就早退）
        UpdateHotbar(pc);

        // 血条：显示值平滑追赶真实血量（避免瞬跳），文字仍显示精确值
        _hpShown = Mathf.MoveTowards(_hpShown, ratio, Time.deltaTime * 2.5f);
        if (hpFill != null)
        {
            // 新风格：血条走 Filled（圆角九宫格在低血量时不会被挤扁）；
            // 没跑过工具 / 把开关关掉时 `_hpFillFilled=false` → 自动退回旧的"改宽度"写法（逐位等于老样子）。
            if (_hpFillFilled) hpFill.fillAmount = _hpShown;
            else hpFill.rectTransform.sizeDelta = new Vector2(_hpFullSize.x * _hpShown, _hpFullSize.y);
            // ⚠️ 颜色**变了才写**：`fillAmount` 每帧必须写（是动画），但颜色在血量稳定后是常量，
            //    每帧无条件写会让整块 HUD 画布标脏重建（2026-10-15 审计）。
            var hpCol = Color.Lerp(new Color(0.9f, 0.2f, 0.15f), new Color(0.2f, 0.85f, 0.3f), _hpShown);
            if (hpFill.color != hpCol) hpFill.color = hpCol;
        }
        // 低血量：血条描边脉动到警示色（与受伤红晕同一条时间线；读的是"真实血量比例"而不是平滑值）
        if (_hpBarFrame != null)
        {
            float low = Mathf.Clamp01((0.35f - ratio) / 0.35f);
            var frameCol = low > 0f
                ? Color.Lerp(_styleFrame, _styleLowHp, low * (0.6f + 0.4f * Mathf.Sin(Time.time * 5f)))
                : _styleFrame;
            // 常态（血量 > 35%）颜色恒定 → 只写一次，不再每帧标脏
            if (_hpBarFrame.color != frameCol) _hpBarFrame.color = frameCol;
        }
        int hpInt = health != null ? Mathf.RoundToInt(health.hp) : 0;
        int maxHpInt = health != null ? Mathf.RoundToInt(health.maxHp) : 0;
        if (hpText != null && (hpInt != _sHp || maxHpInt != _sMaxHp))
        {
            _sHp = hpInt; _sMaxHp = maxHpInt;
            hpText.text = $"HP {hpInt}/{maxHpInt}";
        }

        // 武器信息：按武器着色（只在相关数值变化时重建字符串）
        if (weaponText != null)
        {
            bool hasWeapon = pc.weapon != null;
            int mag = pc.magAmmo;
            int magSize = hasWeapon ? pc.weapon.magSize : 0;
            // ⚠️ 「槽N」与「血包×N」已于 2026-09-22 **移出**这行：底部快捷栏（1/2/3/Q）已经在表达这两项，
            //    同屏重复反而更难扫读。想恢复就把它们加回下面的字符串。
            // 备弹：**弹匣当前这一级**的库存（分级系统 2026-09-27 起 —— 用户明确"各等级不能加在一起显示"）。
            // ⚠️ 弹匣等级未知（`magAmmoId == null`，如刚捡的新枪 / 旧数据）→ 预览"该口径**有货的最低等级**"，
            //    与换弹时 `ResolveAmmo` 的实际选择一致：玩家看到的数字就是下次装填会装的那一级。
            //
            // ⚠️⚠️ 【∞】**只能**在一种情况下出现：`ammoKind == None`（不吃弹药的枪，如 RPG）。
            //    以前这里把 `ammoCard == null` 也当成 -1（无限），于是【背包里一颗这种子弹都没有】时
            //    会误显示"备弹无限"（2026-09-29 报障 + 实测：开局捡任意一把枪，7 把普通枪全部显示 ∞）——
            //    玩家以为备弹无限，打空弹匣却发现换不了弹，像是卡了。
            //    现在 `ammoCard == null` 一律按 **0**（真的一颗都没有）处理。
            bool infiniteAmmo = hasWeapon && pc.weapon.ammoKind == AmmoKind.None;
            AmmoData ammoCard = null;
            if (!infiniteAmmo && hasWeapon && pc.backpack != null)
                ammoCard = pc.magAmmoId != null ? pc.magAmmoId : pc.backpack.LowestOwnedAmmo(pc.weapon.ammoKind);
            int reserve = infiniteAmmo ? -1 : (ammoCard != null ? pc.backpack.CountAmmo(ammoCard) : 0);   // -1 = 无限
            int tier = ammoCard != null ? ammoCard.tier : 0;
            string tierMark = ammoCard != null ? ammoCard.TierMark : "";

            if (hasWeapon != _sHasWeapon || pc.reloading != _sReloading || mag != _sMag || magSize != _sMagSize
                || reserve != _sReserve || tier != _sTier
                || (hasWeapon && !string.Equals(pc.weapon.weaponName, _sWeaponName)))
            {
                _sHasWeapon = hasWeapon; _sReloading = pc.reloading; _sMag = mag; _sMagSize = magSize;
                _sReserve = reserve; _sTier = tier;
                _sWeaponName = hasWeapon ? pc.weapon.weaponName : null;
                // 用户 2026-09-27 定的格式：`武器名  弹匣/当前等级备弹  等级徽标`（**不再显示弹匣容量**）
                weaponText.text = !hasWeapon ? "空手  ——  到地上捡一把武器吧"
                    // 换级与换弹的**表现**在这里分开（都是 `reloading`，靠 `switchingAmmo` 区分）：
                    // 否则玩家按 T 会看到"换弹中…"，分不清自己在装弹还是在换等级。
                    // ⚠️ 不需要额外的缓存字段：`reloading` 由 false→true 那一刻 `_switchTarget` 已定，
                    //    而缓存判断里的 `pc.reloading != _sReloading` 正好在那帧触发重建。
                    : (pc.reloading ? $"{pc.weapon.weaponName}  " + (pc.switchingAmmo ? "换弹药中..." : "换弹中...")
                                    : $"{pc.weapon.weaponName}  {mag}/" + (reserve < 0 ? "∞" : reserve.ToString())
                                      + (tierMark.Length > 0 ? "  " + tierMark : ""));
            }
            // 空手色走「次要文字色」（新风格下与卡片配色同族；未启用风格时 = 原来的灰 (0.7,0.7,0.7)）
            // ⚠️ 颜色只在换武器时变 → 变了才写（否则每帧标脏整块 HUD 画布；2026-10-15 审计）
            var wCol = hasWeapon ? pc.weapon.pickupColor : _styleDim;
            if (weaponText.color != wCol) weaponText.color = wCol;
        }

        // 模式（只在切换时改文本）
        if (modeText != null && pc.running != _sRunning)
        {
            _sRunning = pc.running;
            modeText.text = pc.running ? "奔跑模式  [Shift 切换]" : "行走模式  [Shift 切换]";
        }

        // 顶部：击杀 / 波次（+ 清场阶段的下一波倒计时）
        if (killText != null && GameState.Kills != _sKills)
        {
            int prev = _sKills;
            _sKills = GameState.Kills;
            killText.text = $"击杀 {_sKills}";
            if (prev >= 0 && _sKills > prev) _killFlashUntil = Time.time + 0.28f;   // 击杀反馈：放大 + 闪白一下
        }
        if (killText != null)
        {
            float k = Mathf.Clamp01((_killFlashUntil - Time.time) / 0.28f);
            if (k > 0f || _killFlashing)
            {
                _killFlashing = k > 0f;
                killText.rectTransform.localScale = Vector3.one * (1f + 0.22f * k);
                killText.color = Color.Lerp(_killBaseColor, Color.white, k);
            }
        }

        var wm = WaveManager.Instance;
        bool clearing = wm != null && wm.IsClearing;
        int clearTicks = clearing ? Mathf.CeilToInt(wm.ClearingCountdown * 10f) : -1;   // 量化到 0.1 秒，避免每帧重建字符串
        if (waveText != null && (GameState.Wave != _sWave || GameState.AliveEnemies != _sAlive || clearTicks != _sClearTicks))
        {
            _sWave = GameState.Wave; _sAlive = GameState.AliveEnemies; _sClearTicks = clearTicks;
            waveText.text = clearing
                ? $"第 {_sWave} 波 · 下一波 {Mathf.Max(clearTicks, 0) / 10f:0.0}s"
                : $"第 {_sWave} 波 · 剩余敌人 {_sAlive}";
        }

        // 进入新波次时中央公告
        if (GameState.Wave != _shownWave)
        {
            _shownWave = GameState.Wave;
            Announce($"—— 第 {GameState.Wave} 波 ——", 2.2f, 46);
        }

        // 中央公告淡入淡出（文本/字号只在公告开始时设一次，之后每帧只改 alpha）
        if (Time.time < _announceUntil && announceText != null)
        {
            float t = Time.time - _announceStart;
            float dur = _announceUntil - _announceStart;
            float a = Mathf.Min(Mathf.Clamp01(t / 0.25f), Mathf.Clamp01((dur - t) / 0.5f));
            if (_announceStart != _sAnnounceStart)
            {
                _sAnnounceStart = _announceStart;
                announceText.text = _announce;
                announceText.fontSize = _announceSize;
            }
            announceText.color = new Color(_announceColor.r, _announceColor.g, _announceColor.b, a);
        }
        else if (announceText != null && announceText.text != "")
        {
            announceText.text = "";
            _sAnnounceStart = -1f;
        }

        // 死亡结算
        if (GameState.GameOver && deathPanel != null && !deathPanel.activeSelf)
        {
            deathPanel.SetActive(true);
            if (deathStat != null)
                deathStat.text = $"击杀 {GameState.Kills} 人 · 坚持到第 {GameState.Wave} 波";
        }
    }

    /// <summary>受伤方向指示：在玩家位置外圈显示一个指向伤害来源的红色箭头，随红晕淡出</summary>
    void UpdateDamageArrow(PlayerController pc)
    {
        if (_damageArrow == null) return;

        bool show = _damageAlpha > 0.02f && !GameState.GameOver && _damageFromDir.sqrMagnitude > 0.0001f;
        if (_damageArrow.enabled != show) _damageArrow.enabled = show;
        if (!show) return;
        if (_canvasRect == null) return;

        if (_cam == null) _cam = CameraCache.Main;   // 统一走 CameraCache，全项目只有它一处直连 Camera.main
        if (_cam == null) return;

        // 世界方向 → 屏幕方向：拿「玩家位置」和「玩家沿来向偏移一点的位置」两次投影求差，
        // 这样相机怎么旋转/倾斜都不用自己换算
        const float radius = 80f;
        Vector3 p = pc.transform.position;
        Vector3 sp = _cam.WorldToScreenPoint(p);
        Vector3 spTo = _cam.WorldToScreenPoint(p + _damageFromDir * 1.5f);
        Vector2 dir = new Vector2(spTo.x - sp.x, spTo.y - sp.y);
        if (dir.sqrMagnitude < 0.0001f) return;
        dir.Normalize();

        Vector2 local;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, sp, null, out local)) return;

        var rt = _damageArrow.rectTransform;
        rt.anchoredPosition = local + dir * radius;
        rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f);
        _damageArrow.color = new Color(1f, 0.25f, 0.2f, Mathf.Clamp01(_damageAlpha) * 0.85f);
    }

    /// <summary>创建受伤方向指示箭头（程序生成三角形贴图，默认隐藏）</summary>
    void BuildDamageArrow()
    {
        if (_canvas == null) return;

        var go = new GameObject("DamageArrow");
        go.transform.SetParent(_canvas.transform, false);

        _damageArrow = go.AddComponent<Image>();
        _damageArrow.raycastTarget = false;
        _damageArrow.sprite = MakeArrowSprite(32);
        _damageArrow.color = new Color(1f, 0.25f, 0.2f, 0f);

        var rt = _damageArrow.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(26f, 26f);
        _damageArrow.enabled = false;   // 只在受伤后的淡出期间显示
    }

    // ==================== 快捷栏（1/2/3 = 武器快捷槽，Q = 血包） ====================

    // **双轨**（与「拾取气泡」同思路）：
    //   ① **场景预置**（推荐）：HUD 上 `hotbar` 拖了、且节点结构自检通过 → 就用它。
    //      布局 / 尺寸 / 图片 / 字号以**节点上的组件**为准（**没进 Play 也能在编辑器里拖**），
    //      代码只写「图标 + 槽框状态色 + 血包数量」。一键生成：菜单 `Tools/游戏/生成快捷栏到场景`。
    //   ② **代码生成（兜底）**：`hotbar` 留空 / 结构不完整 → 运行时自建（与预置版同构），游戏始终能跑。
    //
    // ⚠️ **颜色归代码、不跟节点**：槽底 = 背包的「库存格底色」、槽框 = 背包的「槽边框色 / 当前槽高亮色」、
    //    键位与数量 = 拾取气泡那对「按键圆标」配色 —— 全部读**同一份来源**，
    //    所以快捷栏和背包 / 拾取气泡**永远一致**。→ 想改颜色请改 `GameAssets`（Inspector 中文「背包」组），
    //    **别在快捷栏节点上调色**（会被覆盖）。
    // ⚠️ 图标与槽框 Sprite 复用 `BackpackPanel` 那一套（同一份实现，见那边的 internal 注释）；
    //    它们的生命周期归 `BackpackPanel.ResetStatics` → **HUD 不要 Destroy 它们**（§4 #25）。
    // ⚠️ 槽位顺序固定：前 3 格 ↔ `PlayerBackpack.slots[0..2]`（键盘 1/2/3），最后 1 格 = 血包（键盘 Q）。

    // 下面这些像素值**只给「代码生成」兜底路径**用（场景预置时以节点为准）。
    // ⚠️ `HotbarBuilder`（编辑器程序集）读不到 private const，那边有一份同名副本 → 改这里记得一起改。
    // ⚠️ 2026-09-28：4 → **5**（新增**魔法格**）；⚠️ 2026-10-12：5 → **6**（新增**投掷物格**）。槽位顺序固定为：
    //    前 3 格 = 武器（键盘 1/2/3）、第 4 格 = 魔法（键盘 E）、第 5 格 = 血包（键盘 Q）、
    //    第 6 格 = 投掷物（键盘 G：持弹模式 / 选择器 / 轨迹预览，见 `PlayerThrowable`）。
    //    索引由格数推导 → 以后要加武器格只改 WeaponSlotCount 一处。
    const int   WeaponSlotCount    = 3;                        // 武器格数
    const int   MagicSlotIndex     = WeaponSlotCount;          // 魔法格下标
    const int   MedkitSlotIndex    = WeaponSlotCount + 1;      // 血包格下标
    const int   ThrowableSlotIndex = WeaponSlotCount + 2;      // 投掷物格下标（2026-10-12）
    const int   HotbarSlotCount    = ThrowableSlotIndex + 1;   // 总格数（= 6）
    const float HotbarSlotSize   = 56f;   // 槽框边长
    const float HotbarGap        = 8f;    // 槽间距
    const float HotbarBottom     = 14f;   // 整组距屏幕底部
    const float HotbarKeyGap     = 4f;    // 槽框与下方「键位小矩形」的间距
    const float HotbarKeyW       = 22f;   // 键位小圆角矩形（宽 × 高）
    const float HotbarKeyH       = 18f;
    const float HotbarIconSize   = 40f;   // 槽内图标边长
    const float HotbarBadgeSize  = 15f;   // 血包数量圆标直径（2026-09-22 由 20 调小，用户要求）
    const int   HotbarKeyFont    = 12;
    const int   HotbarCountFont  = 10;    // 圆标变小了 → 字号跟着收

    /// <summary>取第 i 个快捷槽里的武器（越界 / 空槽一律返回 null）</summary>
    static WeaponData SlotWeapon(PlayerBackpack bp, int i)
        => (bp != null && bp.slots != null && i >= 0 && i < bp.slots.Length && bp.slots[i] != null)
            ? bp.slots[i].weapon : null;

    /// <summary>快捷栏初始化（双轨，见上面那段长注释）</summary>
    void BuildHotbar()
    {
        if (hotbar != null)
        {
            _hotbarRect = hotbar;
            if (BindHotbarNodes())
            {
                // 字体统一注入（含中文）+ 强制不截断（与拾取气泡预置路径同一处理）
                Font f = fallbackFont != null ? fallbackFont : UIUtil.Font;
                for (int i = 0; i < HotbarSlotCount; i++)
                    UIUtil.ApplyFont(FindTextDeep(_hotbarRect.Find("Slot" + (i + 1)), "KeyText"), f);
                UIUtil.ApplyFont(_hbCountText, f);
                UIUtil.ApplyFont(_hbThrCountText, f);

                // "运行时生成的图存不进场景" → 只给**还空着**的 Image 补占位图（节点上配了真图就不动它）
                for (int i = 0; i < HotbarSlotCount; i++)
                    UIUtil.FillSprite(_hbFrame[i], UIUtil.SlotFrameSprite(), true);

                _hbSig = int.MinValue;   // 强制第一帧刷一次状态色 / 图标
                return;
            }

            // ⚠️ 必须把那个**半成品预置节点关掉**，否则它会和下面代码生成的快捷栏**同时画出来**（两个快捷栏叠在一起）。
            //    拾取气泡的同款回退路径一直是这么做的（见 BuildPickupBubble），这里当初漏了（2026-09-22 审计发现）。
            hotbar.gameObject.SetActive(false);
            Debug.LogWarning("[HUD] 快捷栏节点结构不完整（需要 Slot1..Slot4，每格下含 Bg / Frame / Icon / KeyBg / KeyText；" +
                             "最后一格还要 CountBg / CountText）→ 已退回代码生成（并已把该节点关掉）。可重跑菜单 Tools/游戏/生成快捷栏到场景。");
        }
        else
        {
            // 引用留空 = "回到代码生成"。⚠️ 但场景里那个预置节点**还留在 Canvas 下、还是激活的**
            //（它是个场景物体，清空引用不会删掉它）→ 不关掉它就会和代码生成的版本**同时画出来**（两个快捷栏）。
            DisableLeftoverNode("Hotbar");
        }

        BuildHotbarByCode();
    }

    /// <summary>
    /// 关掉场景里**残留的预置节点**（用于"清空引用 → 回到代码生成"这条回退路径）。
    /// ⚠️ 为什么需要：清空 HUD 上的引用**不会删掉场景里那个节点**（它是场景物体、默认还是激活的）
    ///    → 代码又会生成一份 → **两份同时画出来**（两个快捷栏 / 两个拾取气泡）。
    ///    引用既然被清空了，代码手里就没有它了，所以只能**按节点名**找（这也是节点名属于"契约"的原因之一）。
    /// </summary>
    void DisableLeftoverNode(string nodeName)
    {
        if (_canvas == null) return;
        var leftover = _canvas.transform.Find(nodeName);
        if (leftover == null) return;
        leftover.gameObject.SetActive(false);
        Debug.LogWarning($"[HUD] 场景里有「{nodeName}」节点，但 HUD 上对应的引用是空的 → 已把它关掉，改用代码生成。" +
                         $"想用预置版就把该节点接回 HUD 的引用；想彻底回到代码生成就直接删掉这个节点。");
    }

    /// <summary>按「节点名约定」绑定场景预置的快捷栏；任一项缺失 → false（调用方退回代码生成）</summary>
    bool BindHotbarNodes()
    {
        for (int i = 0; i < HotbarSlotCount; i++)
        {
            var slot = _hotbarRect.Find("Slot" + (i + 1));
            _hbFrame[i] = FindImage(slot, "Frame");
            _hbIcon[i] = FindImage(slot, "Icon");
            _hbBg[i] = FindImage(slot, "Bg");          // 可选：没有就不管（只有 Frame / Icon 是必需的）
            // ⚠️ `KeyText` 挂在 `KeyBg` **底下** → 必须**递归**找。用直接查找会拿到 null，
            //    表现是"魔法格永远显示工具写入的占位字母、冷却秒数不更新"（与 CountText 同一类坑）。
            _hbKeyText[i] = FindTextDeep(slot, "KeyText");
            if (slot == null || _hbFrame[i] == null || _hbIcon[i] == null) return false;
        }
        var q = _hotbarRect.Find("Slot" + (MedkitSlotIndex + 1));
        _hbCountBg = FindImage(q, "CountBg");           // CountBg 是槽的直接子节点
        // ⚠️ `CountText` 挂在 **`CountBg` 底下** → 必须**递归**找。用直接查找会拿到 null
        //    → 血包数量**永远停在生成工具写的占位值**（踩过：一直显示 2）。
        _hbCountText = FindTextDeep(q, "CountText");

        // 投掷物格的圆标（2026-10-12；同样是"直接子节点 CountBg + 递归 CountText"）
        var g = _hotbarRect.Find("Slot" + (ThrowableSlotIndex + 1));
        _hbThrCountBg = FindImage(g, "CountBg");
        _hbThrCountText = FindTextDeep(g, "CountText");
        return true;
    }

    /// <summary>代码生成整套快捷栏（兜底路径；结构与场景预置版逐位一致）</summary>
    void BuildHotbarByCode()
    {
        if (_canvas == null) return;

        float w = HotbarSlotCount * HotbarSlotSize + (HotbarSlotCount - 1) * HotbarGap;
        float h = HotbarSlotSize + HotbarKeyGap + HotbarKeyH;

        var rootGo = new GameObject("Hotbar");
        rootGo.transform.SetParent(_canvas.transform, false);
        _hotbarRect = rootGo.AddComponent<RectTransform>();
        _hotbarRect.anchorMin = _hotbarRect.anchorMax = new Vector2(0.5f, 0f);   // 父节点（Canvas）底部中心
        _hotbarRect.pivot = new Vector2(0.5f, 0f);                              // 贴底：整组从底部往上长
        _hotbarRect.sizeDelta = new Vector2(w, h);
        _hotbarRect.anchoredPosition = new Vector2(0f, HotbarBottom);

        for (int i = 0; i < HotbarSlotCount; i++)
        {
            bool isMedkit = i == MedkitSlotIndex;
            bool isThrowable = i == ThrowableSlotIndex;
            float x = -w * 0.5f + HotbarSlotSize * 0.5f + i * (HotbarSlotSize + HotbarGap);
            var slot = NewChildRect(_hotbarRect, "Slot" + (i + 1), new Vector2(HotbarSlotSize, h), new Vector2(x, h * 0.5f));
            // 键位：前 3 格 = 武器（1/2/3）、第 4 格 = 魔法（E，运行时可能变成剩余秒数或「被」）、
            //       第 5 格 = 血包（Q）、第 6 格 = 投掷物（G）
            string keyLabel = isMedkit ? "Q"
                : (isThrowable ? "G" : (i == MagicSlotIndex ? "E" : (i + 1).ToString()));
            BuildHotbarSlotByCode(slot, i, keyLabel, isMedkit, isThrowable);
        }
    }

    /// <summary>代码生成一个快捷槽：槽底 + 描边 + 图标 + 下方键位小圆角矩形（血包格 / 投掷物格再加数量圆标）</summary>
    void BuildHotbarSlotByCode(RectTransform slot, int index, string keyLabel, bool medkitBadge, bool throwableBadge)
    {
        float h = slot.sizeDelta.y;
        float boxY = h - HotbarSlotSize * 0.5f;   // 槽框中心（贴顶，键位在下面）

        // 槽底（半透明深色）→ 复用背包的「库存格底色」
        var bg = NewHotbarImage(slot, "Bg", new Vector2(HotbarSlotSize, HotbarSlotSize), new Vector2(0f, boxY), UIUtil.CellColor());
        bg.sprite = UIUtil.RoundedRectSprite(64, 14f, 0f);
        bg.type = Image.Type.Sliced;

        // 槽框描边（状态色在 UpdateHotbar 里写：当前槽高亮 / 普通）
        var frame = NewHotbarImage(slot, "Frame", new Vector2(HotbarSlotSize, HotbarSlotSize), new Vector2(0f, boxY), UIUtil.SlotColor());
        frame.sprite = UIUtil.SlotFrameSprite();
        frame.type = Image.Type.Sliced;

        // 图标（sprite 由 UpdateHotbar 填；空槽时整体隐藏，只留框 + 键位）
        var icon = NewHotbarImage(slot, "Icon", new Vector2(HotbarIconSize, HotbarIconSize), new Vector2(0f, boxY), Color.white);
        icon.preserveAspect = true;
        icon.gameObject.SetActive(false);

        // 键位小圆角矩形（白底 + 深字，与拾取气泡的「按键圆标」同一对配色）
        var keyBg = NewHotbarImage(slot, "KeyBg", new Vector2(HotbarKeyW, HotbarKeyH), new Vector2(0f, HotbarKeyH * 0.5f), BubbleKeyBgColor);
        keyBg.sprite = UIUtil.RoundedRectSprite(64, 10f, 0f);
        keyBg.type = Image.Type.Sliced;
        // ⚠️ 文字必须**建成键位小矩形的子节点**再铺满 —— `StretchToParent` 只改锚点、**不重挂父子**；
        //    建成 Slot 的子节点就会铺满整格 → 数字跑到图标正中间（踩过）
        var keyText = NewHotbarText(keyBg.rectTransform, "KeyText", keyLabel, HotbarKeyFont, BubbleKeyTextColor);
        StretchToParent(keyText.rectTransform, keyBg.rectTransform);

        _hbFrame[index] = frame;
        _hbIcon[index] = icon;
        _hbBg[index] = bg;
        _hbKeyText[index] = keyText;   // 魔法格的键位文字会被运行时改写 → 必须留引用

        if (!medkitBadge && !throwableBadge) return;

        // 数量圆标（血包格 / 投掷物格各一个；贴在槽框的右下角内侧）
        float half = HotbarBadgeSize * 0.5f;
        var badge = NewHotbarImage(slot, "CountBg", new Vector2(HotbarBadgeSize, HotbarBadgeSize),
            new Vector2(HotbarSlotSize * 0.5f - half, boxY - HotbarSlotSize * 0.5f + half), BubbleKeyBgColor);
        badge.sprite = UIUtil.CircleSprite(48);
        var badgeText = NewHotbarText(badge.rectTransform, "CountText", "", HotbarCountFont, BubbleKeyTextColor);
        StretchToParent(badgeText.rectTransform, badge.rectTransform);
        badge.gameObject.SetActive(false);
        badgeText.gameObject.SetActive(false);
        if (medkitBadge) { _hbCountBg = badge; _hbCountText = badgeText; }
        if (throwableBadge) { _hbThrCountBg = badge; _hbThrCountText = badgeText; }
    }

    /// <summary>
    /// 刷新快捷栏：图标（武器图标 / 血包十字）、当前槽高亮、血包数量。
    /// **脏值检查**：当前槽 + 血包数 + 前 3 格武器都没变就直接返回 —— 每帧写 `Image.sprite` 会让 Canvas 重建。
    /// 空槽表现（用户 2026-09-22 定）：**槽框与键位留着、里面空**（固定 4 格、位置永不变动）。
    /// </summary>
    void UpdateHotbar(PlayerController pc)
    {
        if (_hotbarRect == null) return;

        var bp = pc.backpack;
        var thr = pc.throwables;
        bool throwing = thr != null && thr.Active;                       // 持弹模式：G 格高亮、武器格不高亮
        var thrDef = thr != null ? thr.Selected : null;                  // G 格显示的种类（没在持弹 = 上次选中 / 库存第一种）
        int thrCount = (thr != null && thrDef != null) ? thr.CountOf(thrDef) : 0;

        int cur = bp != null ? bp.currentSlot : -1;
        int med = bp != null ? bp.Medkits : 0;
        var magicCard = bp != null ? bp.CurrentMagic : null;   // 当前装备的魔法卡（null = 空槽）
        float cdRatio = pc.magic != null ? pc.magic.CooldownRatio : 0f;
        float cdLeft = pc.magic != null ? pc.magic.CooldownRemaining : 0f;

        unchecked
        {
            int sig = (cur + 2) * 397 ^ med;
            for (int i = 0; i < WeaponSlotCount; i++)
            {
                var w0 = SlotWeapon(bp, i);
                sig = sig * 31 + (w0 != null ? w0.GetInstanceID() : 0);
            }
            sig = sig * 31 + (magicCard != null ? magicCard.GetInstanceID() : 0);        // ⚠️ 换魔法必须重刷
            sig = sig * 31 + Mathf.CeilToInt(cdLeft * 10f);                             // ⚠️ 冷却倒数要重刷（0.1s 粒度）
            sig = sig * 31 + (thrDef != null ? thrDef.GetInstanceID() : 0);             // ⚠️ 换投掷物种类必须重刷
            sig = sig * 31 + thrCount;
            sig = sig * 31 + (throwing ? 7 : 0);                                        // ⚠️ 进出持弹模式要重刷高亮
            if (sig == _hbSig) return;
            _hbSig = sig;
        }

        for (int i = 0; i < HotbarSlotCount; i++)
        {
            bool isMedkit = i == MedkitSlotIndex;
            bool isMagic = i == MagicSlotIndex;
            bool isThrow = i == ThrowableSlotIndex;
            var w = (isMedkit || isMagic || isThrow) ? null : SlotWeapon(bp, i);

            // ---- 图标：武器图标 / 魔法图标 / 血包十字 / 投掷物图标；没有 → 不显示（槽框 + 键位留着）----
            Sprite spr = null;
            Color iconColor = Color.white;
            if (isMedkit)
            {
                spr = med > 0 ? ItemVisuals.MedkitSprite() : null;
                iconColor = ItemVisuals.MedkitIconColor;
            }
            else if (isMagic)
            {
                spr = magicCard != null ? ItemVisuals.IconOf(magicCard) : null;
                iconColor = magicCard != null ? ItemVisuals.ColorOf(magicCard) : Color.white;
                if (cdRatio > 0f) iconColor *= new Color(0.45f, 0.45f, 0.5f, 1f);   // 冷却中：压暗
            }
            else if (isThrow)
            {
                // G 格：显示"当前拿的种类"（没在持弹 = 上次选中的那种 / 库存里第一种）；一颗都没有 = 空框
                spr = (thrDef != null && thrCount > 0) ? ItemVisuals.IconOf(thrDef) : null;
                iconColor = thrDef != null ? ItemVisuals.ColorOf(thrDef) : Color.white;
            }
            else if (w != null)
            {
                spr = ItemVisuals.WeaponIconSprite(w);
                iconColor = w.pickupColor;
            }

            if (_hbIcon[i] != null)
            {
                _hbIcon[i].sprite = spr;
                if (spr != null) _hbIcon[i].color = iconColor;
                if (_hbIcon[i].gameObject.activeSelf != (spr != null)) _hbIcon[i].gameObject.SetActive(spr != null);
            }

            // ---- 槽底：与背包的「库存格底色」同源（改 GameAssets 两边一起变）----
            if (_hbBg[i] != null) _hbBg[i].color = UIUtil.CellColor();

            // ---- 槽框：武器格 = 当前手上那把高亮（持弹时不高亮）；魔法格冷却中压暗；投掷物格 = 持弹时高亮 ----
            if (_hbFrame[i] != null)
            {
                if (isThrow)
                    _hbFrame[i].color = (throwing && thrDef != null && thrCount > 0)
                        ? UIUtil.SlotActiveColor() : UIUtil.SlotColor();
                else if (!isMedkit && !isMagic && i == cur && !throwing)
                    _hbFrame[i].color = UIUtil.SlotActiveColor();
                else if (isMagic && cdRatio > 0f)
                    _hbFrame[i].color = UIUtil.SlotColor() * new Color(0.65f, 0.65f, 0.65f, 1f);
                else
                    _hbFrame[i].color = UIUtil.SlotColor();
            }

            // ---- 魔法格的键位文字：主动 = E；冷却中 = 剩余秒数；被动 = 被 ----
            // ⭐ 复用现有的 KeyText 节点，不额外加 UI 元素（改动面最小，也不用改场景节点结构）
            if (isMagic && _hbKeyText[i] != null)
            {
                string label;
                if (magicCard == null) label = "E";
                else if (magicCard.isPassive) label = "被";
                else if (cdRatio > 0f) label = Mathf.CeilToInt(cdLeft).ToString();
                else label = "E";
                if (_hbKeyText[i].text != label) _hbKeyText[i].text = label;
            }

            if (isMedkit)
            {
                // ---- 血包数量圆标 ----
                bool showMed = med > 0;
                if (_hbCountText != null)
                {
                    _hbCountText.text = med.ToString();
                    if (_hbCountText.gameObject.activeSelf != showMed) _hbCountText.gameObject.SetActive(showMed);
                }
                if (_hbCountBg != null && _hbCountBg.gameObject.activeSelf != showMed) _hbCountBg.gameObject.SetActive(showMed);
            }
            else if (isThrow)
            {
                // ---- 投掷物数量圆标（数量 = 当前种类共几颗，派生值）----
                bool showThr = thrDef != null && thrCount > 0;
                if (_hbThrCountText != null)
                {
                    _hbThrCountText.text = Mathf.Max(thrCount, 0).ToString();
                    if (_hbThrCountText.gameObject.activeSelf != showThr) _hbThrCountText.gameObject.SetActive(showThr);
                }
                if (_hbThrCountBg != null && _hbThrCountBg.gameObject.activeSelf != showThr) _hbThrCountBg.gameObject.SetActive(showThr);
            }
        }
    }

    // ---- 快捷栏的节点小工具（只给"代码生成"兜底路径用）----

    static RectTransform FindChildRect(Transform parent, string name)
        => parent != null ? parent.Find(name) as RectTransform : null;
    static Image FindImage(Transform parent, string name)
    {
        var rt = FindChildRect(parent, name);
        return rt != null ? rt.GetComponent<Image>() : null;
    }

    /// <summary>在子树里**递归**按名字找。
    /// ⚠️ `Transform.Find` 只找**直接子节点**，而 `KeyText` / `CountText` 挂在 `KeyBg` / `CountBg` **底下**
    /// （2026-09-22 改的层级）→ 用直接查找会拿到 null（踩过：血包数量永远停在生成工具写的占位值 `2`）。</summary>
    static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        for (int i = 0; i < root.childCount; i++)
        {
            var c = root.GetChild(i);
            if (c.name == name) return c;
            var hit = FindDeep(c, name);
            if (hit != null) return hit;
        }
        return null;
    }

    static Text FindTextDeep(Transform root, string name)
    {
        var t = FindDeep(root, name);
        return t != null ? t.GetComponent<Text>() : null;
    }

    /// <summary>建一个子节点：锚在父节点**底部中心**、pivot 居中、按 anchoredPosition 摆放</summary>
    static RectTransform NewChildRect(Transform parent, string name, Vector2 size, Vector2 pos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        return rt;
    }

    static Image NewHotbarImage(Transform parent, string name, Vector2 size, Vector2 pos, Color color)
    {
        var img = NewChildRect(parent, name, size, pos).gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;   // 纯显示：快捷栏明确不做点击（键盘 1/2/3/Q 才是入口）
        return img;
    }

    Text NewHotbarText(Transform parent, string name, string label, int size, Color color)
    {
        var t = NewChildRect(parent, name, new Vector2(HotbarKeyW, HotbarKeyH), Vector2.zero).gameObject.AddComponent<Text>();
        UIUtil.ApplyFont(t, fallbackFont != null ? fallbackFont : UIUtil.Font);   // 字体 + 强制不截断（唯一实现）
        t.text = label;
        t.fontSize = size;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = color;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        return t;
    }

    /// <summary>让子节点铺满父节点 —— ⚠️ `child` 必须**已经是** `parent` 的子节点（本函数只调锚点、**不重挂父子**；
    /// 父子关系传错 = 铺满的是别的节点，键位 / 数量文字会跑到槽中央）。</summary>
    static void StretchToParent(RectTransform child, RectTransform parent)
    {
        if (child == null || parent == null) return;
        child.anchorMin = Vector2.zero;
        child.anchorMax = Vector2.one;
        child.offsetMin = Vector2.zero;
        child.offsetMax = Vector2.zero;
    }

    // ==================== 拾取气泡 ====================

    /// <summary>
    /// 拾取气泡初始化（**双轨**）：
    ///   ① **场景预置**（推荐）：上面那组引用拖了就用它们 —— 颜色 / 字号 / 图片 / 尺寸以**节点上的组件为准**，
    ///      代码只填文本与位置（`ApplyBubbleText` 对预置节点**不改布局**）。于是**没进 Play 也能在编辑器里调**。
    ///   ② **代码生成（兜底）**：引用留空 / 不完整 → 运行时自建（与历史版本逐位一致），游戏始终能跑。
    /// 样式照参考图：浅色圆角气泡 + 深色名称 + 右侧圆形按键标 + 外圈淡描边 + UGUI `Shadow` 投影。
    /// </summary>
    void BuildPickupBubble()
    {
        // ---- ① 场景预置 ----
        if (pickupBubble != null && pickupBubbleName != null && pickupBubbleKeyText != null)
        {
            _bubbleAuthored = true;
            _bubbleRect = pickupBubble;
            _bubbleEdge = pickupBubbleEdge;
            _bubbleBg = pickupBubbleBg;
            _bubbleName = pickupBubbleName;
            _bubbleKeyBg = pickupBubbleKeyBg;
            _bubbleKeyText = pickupBubbleKeyText;

            // "运行时生成的图存不进场景" → 只给**还空着**的 Image 补占位图（你在节点上配了真图就不动它）
            UIUtil.FillSprite(_bubbleEdge, UIUtil.RoundedRectSprite(64, 12f, 2f), true);
            UIUtil.FillSprite(_bubbleBg, UIUtil.RoundedRectSprite(64, 12f, 0f), true);
            UIUtil.FillSprite(_bubbleKeyBg, UIUtil.CircleSprite(48), false);

            // 字体统一由代码注入（项目共用字体，保证中文正常）；节点上只调字号 / 颜色
            // （`UIUtil.ApplyFont` 内部同时强制 Vertical Overflow = Overflow，否则"按键字母 E"会被 Truncate 吞掉）
            var f = fallbackFont != null ? fallbackFont : UIUtil.Font;
            UIUtil.ApplyFont(_bubbleName, f);
            UIUtil.ApplyFont(_bubbleKeyText, f);

            pickupBubble.gameObject.SetActive(false);   // 运行时先隐藏，Update 按需显示
            _bubbleLabel = null;
            return;
        }

        if (pickupBubble != null)
        {
            // ⚠️ 同理：把这个"半配置"的节点也关掉，否则它会和下面代码生成的气泡**同时存在**
            //    （观感重复，而且很难看出多出来的那个是哪来的）
            pickupBubble.gameObject.SetActive(false);
            Debug.LogWarning("[HUD] 拾取气泡节点不完整（缺「物品名文本 pickupBubbleName」或「按键字母文本 pickupBubbleKeyText」）" +
                             "→ 已退回代码生成（并已把该节点关掉）。请检查 HUD 上「拾取气泡」那组引用。");
        }

        // ---- ② 代码生成（兜底，与历史版本一致）----
        if (pickupBubble == null) DisableLeftoverNode("PickupBubble");   // 同上：清空引用后残留的预置节点要关掉
        if (_canvas == null) return;

        var go = new GameObject("PickupBubble");
        go.transform.SetParent(_canvas.transform, false);
        _bubbleRect = go.AddComponent<RectTransform>();
        _bubbleRect.anchorMin = _bubbleRect.anchorMax = _bubbleRect.pivot = new Vector2(0.5f, 0.5f);
        _bubbleRect.sizeDelta = new Vector2(140f, pickupBubbleHeight);

        // 外圈描边（比气泡大 3px，压在底层）
        _bubbleEdge = AddBubbleImage(go.transform, "Edge", UIUtil.RoundedRectSprite(64, 12f, 2f), BubbleEdgeColor, 140f);
        // 内层填充（浅色圆角底）
        _bubbleBg = AddBubbleImage(go.transform, "Bg", UIUtil.RoundedRectSprite(64, 12f, 0f), BubbleBgColor, 140f);
        // 轻微投影（UGUI 自带的 Shadow 即可）
        var shadow = _bubbleBg.gameObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.28f);
        shadow.effectDistance = new Vector2(0f, -3f);

        // 物品名（靠左）
        _bubbleName = AddBubbleText(go.transform, "Name", BubbleFontSize, BubbleTextColor, TextAnchor.MiddleLeft);
        // 按键圆标（圆形底 + 字母，靠右）
        _bubbleKeyBg = AddBubbleImage(go.transform, "KeyBg", UIUtil.CircleSprite(48), BubbleKeyBgColor, pickupBubbleKeyDiameter);
        _bubbleKeyText = AddBubbleText(go.transform, "KeyText", BubbleFontSize - 2, BubbleKeyTextColor, TextAnchor.MiddleCenter);
        if (_bubbleKeyText != null) _bubbleKeyText.rectTransform.sizeDelta = new Vector2(pickupBubbleKeyDiameter, pickupBubbleKeyDiameter);

        go.SetActive(false);
        _bubbleLabel = null;
    }

    /// <summary>只给"图还空着"的 Image 补占位图（配了真图不覆盖）。场景预置节点用它补齐"运行时才生成的圆角图"。</summary>

    Image AddBubbleImage(Transform parent, string name, Sprite sprite, Color color, float w)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, pickupBubbleHeight);
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.type = Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = false;   // 纯显示，不挡任何点击
        return img;
    }

    Text AddBubbleText(Transform parent, string name, int size, Color color, TextAnchor anchor)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(80f, pickupBubbleHeight - 4f);
        var t = go.AddComponent<Text>();
        t.font = fallbackFont != null ? fallbackFont : UIUtil.Font;
        t.fontSize = size;
        t.alignment = anchor;
        t.color = color;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;   // 名称不换行（气泡宽度跟着名称变）
        return t;
    }

    /// <summary>按当前名称重排气泡（**只用于「代码生成」兜底路径**）。
    /// 气泡整体宽 = 左内边距 + 名称宽 + 间距 + 按键圆标直径 + 右内边距。
    /// ⚠️ **场景预置节点不走这里**：预置模式下布局以节点上的组件为准（用 `HorizontalLayoutGroup` + `ContentSizeFitter` 自动撑宽），
    ///    代码只填文字 —— 否则"你在编辑器里摆好的布局"会被每次改名字时覆盖掉。</summary>
    void ApplyBubbleText()
    {
        if (_bubbleRect == null || _bubbleName == null) return;

        _bubbleName.text = _bubbleLabel;
        if (_bubbleKeyText != null) _bubbleKeyText.text = _bubbleKeyChar;

        if (_bubbleAuthored) return;   // 场景预置：不改布局（位置 / 尺寸以节点为准）

        float nameW = Mathf.Max(_bubbleName.preferredWidth, 28f);
        float w = pickupBubblePadX + nameW + pickupBubbleGap + pickupBubbleKeyDiameter + pickupBubblePadX;
        _bubbleRect.sizeDelta = new Vector2(w, pickupBubbleHeight);
        if (_bubbleEdge != null) _bubbleEdge.rectTransform.sizeDelta = new Vector2(w + 3f, pickupBubbleHeight + 3f);
        if (_bubbleBg != null) _bubbleBg.rectTransform.sizeDelta = new Vector2(w, pickupBubbleHeight);

        _bubbleName.rectTransform.sizeDelta = new Vector2(nameW, pickupBubbleHeight - 4f);
        _bubbleName.rectTransform.anchoredPosition = new Vector2(-w * 0.5f + pickupBubblePadX + nameW * 0.5f, 0f);

        Vector2 keyPos = new Vector2(w * 0.5f - pickupBubblePadX - pickupBubbleKeyDiameter * 0.5f, 0f);
        if (_bubbleKeyBg != null) _bubbleKeyBg.rectTransform.anchoredPosition = keyPos;
        if (_bubbleKeyText != null) _bubbleKeyText.rectTransform.anchoredPosition = keyPos;
    }

    /// <summary>
    /// 每帧把气泡摆到目标物体的屏幕位置：世界坐标 → 屏幕像素 → Canvas 局部坐标。
    /// 过期（调用方停止续期）/ 游戏结束 / 物体跑到**相机后方**（`sp.z &lt;= 0`，投影无意义）时隐藏；
    /// 位置夹进屏幕，避免贴边被裁掉一半。
    /// </summary>
    void UpdatePickupBubble()
    {
        if (_bubbleRect == null) return;

        bool show = _bubbleTarget != null && Time.time < _bubbleUntil && !GameState.GameOver;
        if (_bubbleRect.gameObject.activeSelf != show) _bubbleRect.gameObject.SetActive(show);
        if (!show || _canvasRect == null) return;

        if (_cam == null) _cam = CameraCache.Main;
        if (_cam == null) return;

        Vector3 sp = _cam.WorldToScreenPoint(_bubbleTarget.position + Vector3.up * _bubbleHeight);
        if (sp.z <= 0f) { _bubbleRect.gameObject.SetActive(false); return; }   // 相机后方：投影无效

        // 贴到物体**右侧**：投影之后再加屏幕空间偏移（与相机朝向解耦，永远是屏幕右边）
        sp.x += pickupBubbleScreenOffsetX * (Screen.height / 720f);   // 按参考分辨率 720 等比缩放

        float sf = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
        float halfW = _bubbleRect.sizeDelta.x * 0.5f * sf + 8f;
        float halfH = _bubbleRect.sizeDelta.y * 0.5f * sf + 8f;
        sp.x = Mathf.Clamp(sp.x, halfW, Screen.width - halfW);
        sp.y = Mathf.Clamp(sp.y, halfH, Screen.height - halfH);

        Vector2 local;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, sp, null, out local)) return;
        _bubbleRect.anchoredPosition = local;
    }

    /// <summary>程序生成「朝上」的三角形箭头贴图（受伤方向指示用）</summary>
    Sprite MakeArrowSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        const float feather = 0.08f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)(size - 1);
                float v = y / (float)(size - 1);            // 0 = 底边，1 = 顶点
                float halfWidth = (1f - v) * 0.46f;         // 顶点在 (0.5, 1)，底边在 v = 0
                float a = Mathf.Clamp01((halfWidth - Mathf.Abs(u - 0.5f)) / feather);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    /// <summary>纯显示元素不需要接收点击：关掉 raycastTarget，省掉每帧无谓的 UI 射线检测（只保留重启按钮）</summary>
    void DisableDisplayRaycasts()
    {
        var keep = restartBtn;
        foreach (var g in GetComponentsInChildren<Graphic>(true))
        {
            if (g == null) continue;
            if (keep != null && (g == keep.targetGraphic || g.GetComponentInParent<Button>() == keep)) continue;
            g.raycastTarget = false;
        }
    }

    /// 程序生成径向红色渐变贴图（边缘不透明、中心透明）
    Texture2D MakeVignette()
    {
        int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x / (float)(size - 1)) * 2f - 1f;
                float dy = (y / (float)(size - 1)) * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / Mathf.Sqrt(2f);
                float alpha = Mathf.Clamp01((d - 0.45f) / 0.55f) * 0.95f;
                tex.SetPixel(x, y, new Color(0.8f, 0.05f, 0.05f, alpha));
            }
        tex.Apply();
        return tex;
    }
}
