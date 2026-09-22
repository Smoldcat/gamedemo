using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 鼠标准星：隐藏系统光标，用 UGUI 自绘十字（4 条线 + 中心点，跟随鼠标）。
/// 命中敌人时闪白；按瞄准状态自动变色（未瞄准=白 / 指着身体=红 / 指着头部=绿，颜色与判定范围都可在 Inspector 调）；
/// 换弹时整块准星替换为「圆形换弹进度环」。
/// 死亡 / 背包等非游玩态隐藏准星并切换到**虚拟光标**（位置继承准星 → 不会从屏幕中心冒出来，见 `VirtualCursor`）。
/// 完全代码生成：运行时自建一个 Overlay Canvas（带与主 HUD 同参数的 CanvasScaler），不需要手动搭 UI。
/// </summary>
public class Crosshair : MonoBehaviour
{
    const float ArmClearance = 3f;       // 十字与散布圈之间的最小余量（像素）：扩张时十字始终保持在圈外

    // 静态：供 Bullet 命中时调用（不需要拿到实例）
    static float _hitMarkUntil;
    static PlayerController.AimState _aimState = PlayerController.AimState.None;   // 缓存的瞄准状态（10Hz 刷新）

    /// 供背包面板等隐藏/恢复准星（打开背包时要临时隐藏准星并显示光标）
    public static Crosshair InstanceUI;   // 实例引用（背包面板用它切换准星显隐）

    /// <summary>静态状态清理（供 StaticReset 调用）：关闭 Reload Domain 时防止命中闪白/索敌状态残留</summary>
    internal static void ResetStatics()
    {
        _hitMarkUntil = 0f;
        _aimState = PlayerController.AimState.None;
    }

    [Header("外观")]
    [Tooltip("十字每条线段的长度（像素）。换用自定义图片时，它同时是那张图片的显示尺寸")]
    public float size = 14f;
    [Tooltip("十字中心留空半径（像素）。有散布圈十字会自动扩张，始终保持在圈外")]
    public float gap = 5f;
    [Tooltip("十字线段粗细（像素）")]
    public float thickness = 2f;
    [Tooltip("换弹进度环直径（像素，与准星同一套缩放）")]
    public float reloadRingSize = 40f;
    [Tooltip("自定义准星图片（Sprite）。留空时依次回落：「资源总表 GameAssets.crosshairSprite」→「代码生成的默认十字」")]
    public Sprite crosshairSprite;

    [Header("准星颜色（按瞄准状态自动切换）")]
    [Tooltip("【未瞄准】准星没指着任何敌人时的颜色（默认白）。\n" +
             "⚠️ 想拿颜色校准受击区域时看这个：白=没指着，红=指着身体，绿=指着头部。")]
    public Color colorIdle = new Color(0.95f, 0.95f, 0.95f, 0.9f);
    [Tooltip("【指着身体】准星指在敌人身体上时的颜色（默认红）。\n" +
             "范围由 PlayerController 的「身体瞄准半径 bodyAimRadius」（世界米数，默认 0.9）决定 —— 调它就能校准红圈。\n" +
             "⚠️ 它比真实命中范围（碰撞体半径 0.4）大，所以旧版存在「红了却打不中」的环带；想一致就把它调到 0.4~0.5。")]
    public Color colorBody = new Color(1f, 0.25f, 0.2f);
    [Tooltip("【指着头部】准星指在敌人头部上时的颜色（默认绿）。\n" +
             "范围 = PlayerController 的「爆头判定圈」：默认就是**屏幕上画出来的那个头球**\n" +
             "（headshotGateScale = 1，所见即所判）—— 想让绿圈比看到的头更小/更大，调 PlayerController.headshotGateScale。\n" +
             "绿色 = 此刻开枪必定爆头（2 倍伤害），所以这个颜色就是「爆头可用」的提示。")]
    public Color colorHead = new Color(0.35f, 1f, 0.45f);
    [Tooltip("【命中闪白】打中敌人瞬间的反馈色（默认纯白，约 0.08 秒）。\n" +
             "⚠️ 它和「未瞄准=白」会撞色，所以单独做成一个字段：嫌看不清就换成别的（比如淡蓝 0.6,0.85,1）。")]
    public Color colorHitFlash = Color.white;

    [Header("散布圈（动态）")]
    [Tooltip("散布圈透明度（0~1）。圈 = 当前武器散布角在瞄准距离上的实际落点范围（散布楔形的内切圆），越远越大；散布=0 时隐藏")]
    [Range(0f, 1f)] public float spreadRingAlpha = 0.55f;

    [Header("光标（非游玩态：背包 / 死亡结算）")]
    [Tooltip("【虚拟光标】非游玩态用一个自绘光标代替系统光标，且**位置继承准星**（打开背包那一刻准星在哪，光标就从哪出现）。\n" +
             "关掉 = 回到旧行为：解锁 + 显示系统光标（光标从屏幕中心出现，与准星无关）。\n" +
             "⚠️ 这是逃生开关：万一虚拟光标让 UI 点不了（背包打开时游戏是暂停的），在 Inspector 里当场关掉它、下一帧即恢复可点。")]
    public bool useVirtualCursor = true;

    Canvas _canvas;
    RectTransform _root;
    Image[] _bars;                 // 0-3 四根线 + 4 中心点
    Image _reloadRing;             // 换弹圆环（Image.Type.Filled / Radial360）
    Image _spreadRing;             // 散布圈（散布楔形的内切圆投影）
    Texture2D _spreadTex, _reloadTex;   // 运行时生成的圆环贴图（OnDestroy 必须释放，否则每局累积一组）
    float _lastArmGap = -1f;       // 上一帧十字留空（脏值检查：没变就不重排四根线）
    float _nextOverCheck;          // 索敌检测降频计时（不必每帧遍历敌人 + 打射线）
    Health _playerHealth;          // 玩家生命组件（订阅受伤，做准星受击抖动）
    float _hitShakeUntil;          // 受击抖动结束时间
    bool _cursorGameplay = true;   // 当前光标是否处于"游玩"状态（只在变化时切换，见 ApplyCursorMode）

    void Awake()
    {
        // 单例防护：出现重复实例（如场景重载的极端时序）时丢弃后出现的那个
        if (InstanceUI != null && InstanceUI != this)
        {
            Destroy(gameObject);
            return;
        }
        InstanceUI = this;
        // 准星图片优先级：组件上拖的（本地覆盖）> GameAssets 资源表（全局默认）> 代码生成的十字
        if (crosshairSprite == null && GameAssets.I != null)
            crosshairSprite = GameAssets.I.crosshairSprite;
        ApplyCursorModeNow(true);  // 游玩：锁定 + 隐藏系统光标（_cursorGameplay 初值即 true，这里直接设一次）
                                   // 顺带 `VirtualCursor.Deactivate()` —— 覆盖"重开场景后静态状态还留着"的情况
        BuildUI();

        // 受击反馈：玩家受伤时准星短暂抖动（Crosshair 与 Health 同挂在 Player 上）
        _playerHealth = GetComponent<Health>();
        if (_playerHealth != null) _playerHealth.OnDamaged += OnPlayerHit;
    }

    void OnPlayerHit(float dmg, Vector3 fromDir)
    {
        _hitShakeUntil = Time.time + 0.15f;
    }

    void OnDestroy()
    {
        if (_playerHealth != null) _playerHealth.OnDamaged -= OnPlayerHit;
        if (InstanceUI == this) InstanceUI = null;
        // 运行时生成的 Sprite / 贴图不会随物体销毁：不释放会「每局 +1 组」
        //（散布圈 128²≈64KB + 换弹环 64²≈16KB，RGBA32 未压缩）
        // ⚠️ **Sprite 与它的 texture 要成对销毁**（`UIUtil.DestroySprite`）—— 原来只销毁了 texture，
        //    Sprite 包装对象每次都漏（2026-09-22 审计发现，违反 §4 #25 自己的规则）。
        if (_spreadRing != null) UIUtil.DestroySprite(_spreadRing.sprite);
        else if (_spreadTex != null) Destroy(_spreadTex);
        if (_reloadRing != null) UIUtil.DestroySprite(_reloadRing.sprite);
        else if (_reloadTex != null) Destroy(_reloadTex);
    }

    /// 背包面板调用：切换"准星显示 / 隐藏"（**参数是"准星是否可见"，不是"是否要显示光标"**）
    /// ⚠️ 背包 Open 调的是 `SetVisible(false)`（隐藏准星）、Close 调 `SetVisible(true)` —— 传反会导致
    /// "开背包时光标被锁住+隐藏、完全点不了 UI"（接过这个 bug，现场表现就是"打开背包没有任何光标"）。
    public void SetVisible(bool crosshairVisible)
    {
        if (_root != null) _root.gameObject.SetActive(crosshairVisible);
        // 准星可见 = 游玩态 → 锁定 + 隐藏系统光标；准星隐藏 = 背包/结算态 → 解锁 + 显示（否则 UI 点不了）
        ApplyCursorMode(crosshairVisible);
    }

    /// <summary>统一切换光标状态（**唯一实现**，别在别处再写 `Cursor.lockState / Cursor.visible`）：
    /// **游玩** = 锁定 + 隐藏（锁定后鼠标增量不受光标位置限制 = 无限行程；也没有可见光标可跟准星"错位"）；
    /// **非游玩**（背包 / 死亡结算）= 解锁 + **仍然隐藏系统光标** + 显示**虚拟光标**（`VirtualCursor`）
    ///   —— 虚拟光标的位置**继承准星**，于是"准星消失、光标在同一处出现"，不再是"无论准星在哪都从屏幕中心冒出来"。
    /// ⚠️ `useVirtualCursor = false` 时回落旧行为（解锁 + 显示系统光标）：那是出问题时的逃生开关。</summary>
    void ApplyCursorModeNow(bool gameplay)
    {
        if (gameplay)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            VirtualCursor.Deactivate();
        }
        else if (useVirtualCursor)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = false;      // 系统光标也藏起来：否则会出现"两个光标"（屏幕中心一个 + 准星处一个）
            VirtualCursor.Activate();    // 位置 = 当前准星屏幕坐标（连续感的关键）
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;       // 逃生路径：回到 2026-09-22 之前的行为
            VirtualCursor.Deactivate();
        }
    }

    /// <summary>只在状态**变化**时切换光标模式。
    /// ⚠️ 不能每帧强制锁定：那会跟"编辑器里按 Esc 主动解锁"打架，把用户困在游戏里出不来。
    /// 而且"无限行程"靠的是 PlayerController 改用鼠标轴增量，与光标是否真的锁住无关 —— 锁定只是顺带让画面干净。</summary>
    void ApplyCursorMode(bool gameplay)
    {
        if (_cursorGameplay == gameplay) return;
        // 非游玩 → 游玩：把"玩家在背包里把指针放到了哪"继承给准星
        //（对称于打开背包时"光标继承准星"；用户 2026-09-22 选定 = **无条件继承**）
        if (gameplay) InheritCursorPosToCrosshair();
        _cursorGameplay = gameplay;
        ApplyCursorModeNow(gameplay);
    }

    /// <summary>关闭背包（含"死亡时背包自动关闭"那一下）时，把准星搬到虚拟光标当前所在的位置
    /// —— 于是"光标消失、准星在同一处出现"，开关两个方向都有连续感。
    /// ⚠️ **只挂在这个"状态变化"包装里，绝不能放进 `ApplyCursorModeNow`**：`Awake` 也调后者，
    ///    而场景重载那一刻把"上一局的光标位置"灌进新准星是错的（那一帧的准星位置该由 `PlayerController.Awake` 决定）。
    /// ⚠️ 逃生开关关掉 / 光标未激活时 `VirtualCursor.Active == false` → 自动跳过，无需额外判断。</summary>
    void InheritCursorPosToCrosshair()
    {
        if (!VirtualCursor.Active) return;
        var pc = PlayerController.Instance;
        if (pc != null) pc.SetCrosshairScreenPos(VirtualCursor.ScreenPos);
    }

    /// <summary>运行时创建准星 UI：独立 Overlay Canvas（带缩放器）+ 4 条线 + 中心点 + 换弹圆环</summary>
    void BuildUI()
    {
        // 独立 Overlay Canvas：准星永远在最上层。加 CanvasScaler 并与主 HUD 同参数，
        // 准星/圆环才会随分辨率一起缩放（否则高分屏上会明显偏小，与 HUD 比例不一致）
        _canvas = UIUtil.NewOverlayCanvas("CrosshairCanvas", 100);

        var root = new GameObject("Crosshair");
        root.transform.SetParent(_canvas.transform, false);
        _root = root.AddComponent<RectTransform>();
        // 锚点显式设在屏幕中心：鼠标坐标（左下原点）换算成「相对屏幕中心的偏移」，再除以缩放系数
        _root.anchorMin = new Vector2(0.5f, 0.5f);
        _root.anchorMax = new Vector2(0.5f, 0.5f);
        _root.pivot = new Vector2(0.5f, 0.5f);
        _root.sizeDelta = Vector2.zero;

        // ---- 散布圈：当前武器散布角在瞄准距离上的"实际落点范围"（散布楔形的内切圆，与两条射线相切）----
        // 半径 = 瞄准距离 × sin(散布角)，随距离线性变大；散布=0（狙击/空手）时隐藏，换弹时让位给换弹环。
        // 先于十字创建 → 渲染在十字下面（不遮挡中心）。
        var spreadGo = new GameObject("SpreadRing");
        spreadGo.transform.SetParent(_root, false);
        _spreadRing = spreadGo.AddComponent<Image>();
        _spreadRing.raycastTarget = false;
        _spreadRing.sprite = MakeRingSprite(128, 0.90f, 1f, 0.02f);   // 细线圆环（略粗一点，近距小圈时才看得清）
        _spreadTex = _spreadRing.sprite.texture;                      // 记下贴图，OnDestroy 释放
        var spreadRt = _spreadRing.rectTransform;
        spreadRt.anchorMin = spreadRt.anchorMax = spreadRt.pivot = new Vector2(0.5f, 0.5f);
        spreadRt.anchoredPosition = Vector2.zero;
        spreadRt.sizeDelta = Vector2.zero;
        spreadGo.SetActive(false);

        _bars = crosshairSprite != null
            ? new Image[1] { MakeBar("CrosshairImg", new Vector2(size, size), Vector2.zero) }
            : new Image[5];

        if (crosshairSprite != null)
        {
            // 自定义准星：单张图片替换代码生成的十字，尺寸用 size 调整
            _bars[0].sprite = crosshairSprite;
            _bars[0].preserveAspect = true;
        }
        else
        {
            _bars[0] = MakeBar("Up",     new Vector2(thickness, size), new Vector2(0f, gap + size * 0.5f));
            _bars[1] = MakeBar("Down",   new Vector2(thickness, size), new Vector2(0f, -(gap + size * 0.5f)));
            _bars[2] = MakeBar("Left",   new Vector2(size, thickness), new Vector2(-(gap + size * 0.5f), 0f));
            _bars[3] = MakeBar("Right",  new Vector2(size, thickness), new Vector2(gap + size * 0.5f, 0f));
            _bars[4] = MakeBar("Center", new Vector2(2f, 2f), Vector2.zero);
        }

        // 换弹圆环：与准星同一个中心点，换弹时显示它、隐藏十字
        var ringGo = new GameObject("ReloadRing");
        ringGo.transform.SetParent(_root, false);
        var ringRt = ringGo.AddComponent<RectTransform>();
        ringRt.sizeDelta = new Vector2(reloadRingSize, reloadRingSize);
        ringRt.anchoredPosition = Vector2.zero;
        _reloadRing = ringGo.AddComponent<Image>();
        _reloadRing.raycastTarget = false;
        _reloadRing.sprite = MakeRingSprite(64, 0.62f, 0.95f);
        _reloadTex = _reloadRing.sprite.texture;                      // 记下贴图，OnDestroy 释放
        _reloadRing.type = Image.Type.Filled;
        _reloadRing.fillMethod = Image.FillMethod.Radial360;
        _reloadRing.fillOrigin = (int)Image.Origin360.Top;   // 从 12 点方向开始转
        _reloadRing.fillClockwise = true;
        _reloadRing.fillAmount = 0f;
        ringGo.SetActive(false);

        // 统一放到 UI 层（与 HUD 一致；Overlay 下不影响渲染）
        UIUtil.SetLayerRecursively(_canvas.gameObject, UIUtil.UILayer);
    }

    Image MakeBar(string name, Vector2 sizeDelta, Vector2 anchorPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root, false);
        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = sizeDelta;
        rt.anchoredPosition = anchorPos;
        var img = go.AddComponent<Image>();
        img.raycastTarget = false;   // 纯显示，不挡任何点击
        return img;
    }

    /// <summary>圆环 Sprite（换弹进度环 / 散布圈用）：innerRatio/outerRatio 为相对半径占比。
    /// featherScale 控制边缘羽化（默认 0.06；细环需要更小的羽化，否则整圈 alpha 都会被削淡）。
    /// ⚠️ 贴图算法收敛到 `UIUtil.RingTexture`（与爆炸圈共用同一份实现，2026-09-22 合并）。</summary>
    Sprite MakeRingSprite(int texSize, float innerRatio, float outerRatio, float featherScale = 0.06f)
    {
        // ⚠️ 别在这里再写一遍圆环算法：全项目只有 `UIUtil.RingTexture` 那一份
        var tex = UIUtil.RingTexture(texSize, innerRatio, outerRatio, featherScale);
        return Sprite.Create(tex, new Rect(0, 0, texSize, texSize), new Vector2(0.5f, 0.5f));
    }

    // 用 LateUpdate 而不是 Update：准星位置由 PlayerController.Update 推进，
    // LateUpdate 保证本帧读到的是**当前帧**的准星坐标，不会因脚本执行顺序产生一帧延迟（输入跟手性）。
    void LateUpdate()
    {
        // 暂停（背包打开）时：准星显隐由背包面板控制，本 Update 不干预（否则会强制重激活准星、隐藏光标）
        if (GameState.Paused) return;

        // 死亡后：隐藏自绘准星 + 解锁并恢复系统光标（结算面板需要鼠标指针去点「重新开始」）
        if (GameState.GameOver)
        {
            if (_root != null && _root.gameObject.activeSelf) _root.gameObject.SetActive(false);
            // 解锁 + 显示光标（此时 _cursorGameplay 可能是 true，必须无条件设置一次）
            // → 走虚拟光标：位置继承准星，结算面板上的「重新开始」按钮照样能点
            _cursorGameplay = false;
            ApplyCursorModeNow(false);
            return;
        }
        if (_root != null && !_root.gameObject.activeSelf) _root.gameObject.SetActive(true);
        ApplyCursorMode(true);   // 游玩：锁定 + 隐藏（只在状态变化时切换，避免和编辑器按 Esc 解锁打架）

        // 准星位置 = PlayerController 的准星屏幕坐标（**唯一真相**）：鼠标只贡献"增量"，后坐直接把准星推走。
        // 所以准星与"锚点"天然永远重合，不存在任何错位（不需要再做世界→屏幕换算）。
        // Canvas 带缩放器：屏幕像素要转成"相对屏幕中心的偏移 ÷ scaleFactor"
        float sf = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
        var pco = PlayerController.Instance;
        Vector2 pos = pco != null ? pco.CrosshairScreenPos : (Vector2)Input.mousePosition;
        // 受击反馈：受伤后短暂随机偏移（抖动），很快回中
        Vector2 hitOffset = Time.time < _hitShakeUntil ? Random.insideUnitCircle * 6f : Vector2.zero;
        _root.anchoredPosition = new Vector2(pos.x - Screen.width * 0.5f, pos.y - Screen.height * 0.5f) / sf + hitOffset;

        // 换弹中：整块准星替换成圆形进度环（进度 = 已换弹时间 / 总换弹时间）
        var pc = PlayerController.Instance;
        bool reloading = pc != null && pc.reloading && pc.weapon != null;
        if (_reloadRing != null)
        {
            if (_reloadRing.gameObject.activeSelf != reloading) _reloadRing.gameObject.SetActive(reloading);
            if (reloading)
            {
                _reloadRing.fillAmount = pc.ReloadProgress01;
                var c = pc.weapon.pickupColor;
                _reloadRing.color = new Color(c.r, c.g, c.b, 0.95f);
            }
        }
        if (_bars != null)
            foreach (var b in _bars)
                if (b != null) b.enabled = !reloading;

        // 换弹时让位给换弹环：避免两个圈重叠（关闭时下一帧会按散布重新显示）
        if (reloading && _spreadRing != null && _spreadRing.gameObject.activeSelf)
            _spreadRing.gameObject.SetActive(false);

        if (reloading) return;   // 换弹时准星已隐藏，不必再索敌/变色

        // 准星变色不需要每帧精确：每 0.1 秒刷新一次瞄准状态，省大量遍历和投影。
        // 判定走 PlayerController.GetAimState —— 与开火的爆头判定是**同一份实现**（看到的 = 打出的）。
        if (Time.time >= _nextOverCheck)
        {
            _nextOverCheck = Time.time + 0.1f;
            var pcAim = PlayerController.Instance;
            if (pcAim != null)
            {
                EnemyAI aimTmp;
                _aimState = pcAim.GetAimState(out aimTmp);
            }
            else _aimState = PlayerController.AimState.None;
        }

        // 颜色优先级：命中闪白 → 指着头部（绿）→ 指着身体（红）→ 未瞄准（白）
        Color col;
        if (Time.time < _hitMarkUntil) col = colorHitFlash;
        else if (_aimState == PlayerController.AimState.Head) col = colorHead;
        else if (_aimState == PlayerController.AimState.Body) col = colorBody;
        else col = colorIdle;

        if (_bars != null)
            foreach (var b in _bars)
                if (b != null) b.color = col;

        UpdateSpreadRing(pc, col);   // 散布圈 + 十字扩张（随武器散布角与瞄准距离）
    }

    /// <summary>
    /// 散布圈 + 十字扩张。
    /// 圈 = 散布楔形的内切圆（与两条从玩家射出的射线相切）：半径 = 瞄准距离 × sin(当前散布角)，越远越大。
    /// 「当前散布角」= `PlayerController.CurrentSpread`（腰射/瞄准各一个值）—— 按住右键瞄准时圈会自动收小。
    /// 世界半径换成屏幕像素用 WorldToScreenPoint 投影差分（与后坐偏移显示同一套技巧），
    /// 沿相机的右/前两个地面方向各投影一次 → 横向/纵向半径，自动适配相机俯角（当前 60° 俯角下纵向只轻微压扁）。
    /// 十字的留空随圈扩张（始终保持在圈外 ArmClearance 像素）；散布=0 时圈隐藏、十字回到最小间距 gap。
    /// </summary>
    void UpdateSpreadRing(PlayerController pc, Color col)
    {
        var cam = CameraCache.Main;
        float rWorld = 0f;
        if (pc != null && pc.weapon != null && pc.CurrentSpread > 0f && cam != null)
        {
            Vector3 origin = pc.transform.position;
            Vector3 aim = pc.AimPoint;
            float dist = new Vector2(aim.x - origin.x, aim.z - origin.z).magnitude;
            rWorld = dist * Mathf.Sin(pc.CurrentSpread * Mathf.Deg2Rad);
        }

        float rX = 0f, rY = 0f;
        if (rWorld > 0.0001f)
        {
            Vector3 center = pc.AimPoint;
            Vector3 sp0 = cam.WorldToScreenPoint(center);
            Vector3 rightG = cam.transform.right; rightG.y = 0f;
            Vector3 fwdG = cam.transform.forward; fwdG.y = 0f;
            if (rightG.sqrMagnitude > 0.0001f)
                rX = (cam.WorldToScreenPoint(center + rightG.normalized * rWorld) - sp0).magnitude;
            if (fwdG.sqrMagnitude > 0.0001f)
                rY = (cam.WorldToScreenPoint(center + fwdG.normalized * rWorld) - sp0).magnitude;
        }

        float sf = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;

        if (_spreadRing != null)
        {
            bool show = rX > 2f && rY > 2f;   // 太小（散布≈0 / 贴脸瞄准）不显示
            if (_spreadRing.gameObject.activeSelf != show) _spreadRing.gameObject.SetActive(show);
            if (show)
            {
                _spreadRing.rectTransform.sizeDelta = new Vector2(rX * 2f / sf, rY * 2f / sf);
                _spreadRing.color = new Color(col.r, col.g, col.b, spreadRingAlpha);   // 颜色跟随准星（命中闪白/指敌变红）
            }
        }

        // 十字随圈扩张：留空 = max(最小间距, 圈半径 + 余量)；散布=0 时就是原来的十字
        float gapPx = Mathf.Max(gap, Mathf.Max(rX, rY) / sf + ArmClearance);
        if (Mathf.Abs(gapPx - _lastArmGap) > 0.01f)
        {
            _lastArmGap = gapPx;
            if (_bars != null && _bars.Length == 5)   // 默认十字模式才有四根可扩张的线（自定义图片模式跳过）
            {
                float off = gapPx + size * 0.5f;
                _bars[0].rectTransform.anchoredPosition = new Vector2(0f, off);
                _bars[1].rectTransform.anchoredPosition = new Vector2(0f, -off);
                _bars[2].rectTransform.anchoredPosition = new Vector2(-off, 0f);
                _bars[3].rectTransform.anchoredPosition = new Vector2(off, 0f);
            }
        }
    }

    /// <summary>命中反馈：准星闪白一下并播放命中音（由 Combat 调用）。
    /// headshot=true 时播放「爆头命中反馈音」而不是普通命中音（命中头部即播，不必击杀）。</summary>
    public static void HitMark(bool headshot = false)
    {
        _hitMarkUntil = Time.time + 0.08f;
        if (headshot) Sfx.HeadshotHit();
        else Sfx.Hit();
    }
}
