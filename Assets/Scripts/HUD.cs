using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>
/// HUD：血条、弹药、波次、击杀、**拾取气泡**、中央公告、受伤红晕、低血量警示、死亡结算。
/// 需要在 Inspector 里拖入下面这些 UI 引用（漏拖的项会跳过，不会报错）：
///   hpText / hpFill / weaponText / modeText —— 左下角血条与武器信息
///   waveText / killText                     —— 顶部中央波次与击杀数
///   announceText                            —— 屏幕中央公告
///   deathPanel / deathText / deathStat / restartBtn —— 死亡结算面板
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

    // ---- 血条平滑显示（文字仍显示精确血量）----
    Vector2 _hpFullSize;
    float _hpShown = 1f;

    // ---- 脏值检查缓存：数值没变就不重建字符串（避免每帧产生 GC 垃圾）----
    int _sHp = int.MinValue, _sMaxHp = int.MinValue;
    bool _sHasWeapon, _sReloading;
    int _sMag = int.MinValue, _sMagSize = int.MinValue, _sMedkits = int.MinValue;
    string _sWeaponName;
    bool _sRunning = true;
    int _sKills = int.MinValue, _sWave = int.MinValue, _sAlive = int.MinValue, _sClearTicks = int.MinValue;
    float _sAnnounceStart = -1f;

    // ---- 击杀反馈 ----
    float _killFlashUntil;
    bool _killFlashing;
    Color _killBaseColor = new Color(1f, 0.92f, 0f);

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
        if (hpFill != null) _hpFullSize = hpFill.rectTransform.sizeDelta;
        if (killText != null) _killBaseColor = killText.color;

        _vignette = MakeVignette();
        BuildDamageVignette();   // 全屏红晕 Image（UGUI）
        BuildDamageArrow();      // 受伤方向指示箭头（默认隐藏）
        BuildPickupBubble();     // 拾取气泡（靠近可拾取物时显示，默认隐藏）

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
        // 运行时创建的贴图不会随物体销毁，必须手动释放，否则场景重载会累积
        // （字体是 UIUtil 的共用资源，由它统一释放，这里不能碰）
        if (_vignette != null) Destroy(_vignette);
        if (_damageArrow != null && _damageArrow.sprite != null) Destroy(_damageArrow.sprite.texture);
    }

    void SetAllTextFont(Font f)
    {
        if (f == null) return;
        foreach (var t in new Text[] { hpText, weaponText, modeText, waveText, killText, announceText, deathStat, restartBtnLabel })
            if (t != null) t.font = f;
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

        // 血条：显示值平滑追赶真实血量（避免瞬跳），文字仍显示精确值
        _hpShown = Mathf.MoveTowards(_hpShown, ratio, Time.deltaTime * 2.5f);
        if (hpFill != null)
        {
            hpFill.rectTransform.sizeDelta = new Vector2(_hpFullSize.x * _hpShown, _hpFullSize.y);
            hpFill.color = Color.Lerp(new Color(0.9f, 0.2f, 0.15f), new Color(0.2f, 0.85f, 0.3f), _hpShown);
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
            int med = pc.backpack != null ? pc.backpack.Medkits : 0;
            if (hasWeapon != _sHasWeapon || pc.reloading != _sReloading || mag != _sMag || magSize != _sMagSize
                || (hasWeapon && !string.Equals(pc.weapon.weaponName, _sWeaponName)) || med != _sMedkits)
            {
                _sHasWeapon = hasWeapon; _sReloading = pc.reloading; _sMag = mag; _sMagSize = magSize;
                _sWeaponName = hasWeapon ? pc.weapon.weaponName : null; _sMedkits = med;
                weaponText.text = !hasWeapon ? "空手  ——  到地上捡一把武器吧"
                    : (pc.reloading ? $"槽{(pc.backpack != null ? pc.backpack.currentSlot + 1 : 1)} {pc.weapon.weaponName}  换弹中...  血包×{med}"
                                    : $"槽{(pc.backpack != null ? pc.backpack.currentSlot + 1 : 1)} {pc.weapon.weaponName}  {mag}/{magSize} ∞  血包×{med}");
            }
            weaponText.color = hasWeapon ? pc.weapon.pickupColor : new Color(0.7f, 0.7f, 0.7f);
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
            FillSprite(_bubbleEdge, UIUtil.RoundedRectSprite(64, 12f, 2f), true);
            FillSprite(_bubbleBg, UIUtil.RoundedRectSprite(64, 12f, 0f), true);
            FillSprite(_bubbleKeyBg, UIUtil.CircleSprite(48), false);

            // 字体统一由代码注入（项目共用字体，保证中文正常）；节点上只调字号 / 颜色
            var f = fallbackFont != null ? fallbackFont : UIUtil.Font;
            if (_bubbleName != null) _bubbleName.font = f;
            if (_bubbleKeyText != null) _bubbleKeyText.font = f;

            pickupBubble.gameObject.SetActive(false);   // 运行时先隐藏，Update 按需显示
            _bubbleLabel = null;
            return;
        }

        if (pickupBubble != null)
            Debug.LogWarning("[HUD] 拾取气泡节点不完整（缺「物品名文本 pickupBubbleName」或「按键字母文本 pickupBubbleKeyText」）" +
                             "→ 已退回代码生成。请检查 HUD 上「拾取气泡」那组引用。");

        // ---- ② 代码生成（兜底，与历史版本一致）----
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
    static void FillSprite(Image img, Sprite fallback, bool sliced)
    {
        if (img == null || img.sprite != null || fallback == null) return;
        img.sprite = fallback;
        if (sliced) img.type = Image.Type.Sliced;
    }

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
