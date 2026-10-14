#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// **调试详情面板**：按 `~`（或 `F1`）呼出/收起。终端风（纯色 + 1px 线，无贴图）。
///
/// 结构（2026-10-14 用户定：功能变多后一屏展示太乱 → 分 tab；调试 UI 以简洁与效率优先）：
///   · **顶部常驻信息区**（不随 tab 变）：HP / 移速 / 武器 / 进度 —— 两列，**左标签静态、右值节流刷新**；
///   · 中部 **5 个 tab**（战斗 / 波次 / 敌人 / 日志 / 杂项），**一次只激活当前页**（其余 SetActive(false) → UGUI 不处理）；
///   · 底部一行**提示**（动作结果）。
///
/// 交互约束（别改）：
///   · 打开走现成的 `UIState.Push()` → 暂停 + 隐藏 HUD + 隐藏准星 + **虚拟光标**（游玩态 `Cursor.lockState == Locked`
///     时 UGUI 会把指针强制成 (-1,-1)，UI 点不中 —— 见 `Crosshair.ApplyCursorModeNow`、交接文档 #31）；
///   · 因此代价是"打开即暂停"，**实时观察由 `DebugHud` 常驻条承担**；
///   · 死亡必须自关（`UIState` 记账约定）；
///   · 面板画布**必须**有 `GraphicRaycaster`（`UIUtil.NewOverlayCanvas` 只建 Canvas+Scaler）。
///
/// ⚠️ 本文件属于 `Assets/Debug/`：发布前整个文件夹删除，详见 README.md。
/// </summary>
public class DebugPanel : MonoBehaviour
{
    const int SortingOrder = 260;        // 面板(200)之上、飘字(300)/虚拟光标(400)之下
    const float RefreshInterval = 0.2f;  // 刷新节流
    const float SpeedStep = 0.5f;        // 移速微调步长
    const int LogCap = 60;               // 日志环形缓冲上限
    const int LogShow = 11;              // 日志页显示行数
    const int EnemyShow = 10;            // 敌人页显示行数

    const float W = 620f, H = 360f;      // 面板尺寸（高度按最大页固定 → 切 tab 不跳动）
    const float ContentH = 200f;         // 内容区高度

    [Tooltip("常驻条（由 DebugBootstrap 自动注入）。")]
    public DebugHud hud;

    Canvas _canvas;
    RectTransform _root;

    // 常驻信息区（值）
    Text _hpVal, _speedVal, _ammoVal, _runVal, _noteText;

    // tab 与页面
    readonly Button[] _tabBtn = new Button[5];
    readonly Text[] _tabLabel = new Text[5];
    readonly GameObject[] _page = new GameObject[5];
    int _tab;

    // 页面内容引用
    Text _enemyText, _logText;
    Text _invulLabel, _hudLabel;
    Text _godLabel;

    bool _open, _godMode, _logDirty;
    float _nextRefresh;
    float _desiredTimeScale = 1f;
    string _note = "";

    readonly List<string> _logs = new List<string>();

    // ==================== 生命周期 ====================

    void Awake()
    {
        BuildUI();
        _root.gameObject.SetActive(false);
        Application.logMessageReceived += OnLog;                    // ⚠️ 与 OnDestroy 的 -= 必须配对
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= OnLog;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        if (_canvas != null) Destroy(_canvas.gameObject);
    }

    /// <summary>场景重载（死亡重开 / 手动重载）时收起面板，并**恢复 `UIState` 的记账**。
    ///
    /// ⚠️ 这里**必须**调 `UIState.Pop()`（即"和正常关闭完全一样"）：
    ///    `StaticReset` 只在**进 Play 时**跑一次（`SubsystemRegistration`，见 `StaticReset.cs:22`），
    ///    **场景重载不会重置 UIState 的静态计数**；项目里其它面板靠"死亡自关（Pop）"维持
    ///    `count>0 ⇔ 暂停` 这个不变量。若这里只藏 UI 不 Pop，就会留下
    ///    `count=1 但没暂停` 的破状态 → 之后任何 `UIState.Push()` 都会被 `Apply()` 早退掉（**永远不再暂停**）。
    ///    （2026-10-14 实测踩到：重载后打开面板不暂停，反射读到 count=1/applied=true。）
    /// ⚠️ 安全性：`sceneLoaded` 触发时新场景的 HUD / 准星已经就绪；且 `Apply()` 内部对 Crosshair 做了空判断。</summary>
    void OnSceneLoaded(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m)
    {
        if (_open) Close();     // Close 内部会 Pop，恢复记账
        Debug.Log("[调试] 场景已重载，调试面板自动收起");
    }

    void Update()
    {
        if (_open && GameState.GameOver) { Close(); return; }   // 死亡自关

        // 自愈校验：面板显示着、但游戏既没停也没在结算 → 说明 `UIState` 的记账丢了
        // （场景重载 / 外部把暂停解掉）。只收起自己、**不 Pop**（此时 Pop 会把计数减到负数语义错乱）。
        if (_open && !GameState.Paused && !GameState.GameOver && Time.timeScale > 0.5f)
        {
            _open = false;
            _root.gameObject.SetActive(false);
            Debug.LogWarning("[调试] 检测到面板状态与暂停状态不一致，已自动收起");
        }

        if (Input.GetKeyDown(KeyCode.BackQuote) || Input.GetKeyDown(KeyCode.F1))
        {
            if (_open) Close();
            else Open();
        }

        // 回血式止血：**面板关着也要生效**（它的用途正是"游戏在跑、我懒得管血"）
        if (_godMode) DebugRead.FullHeal();

        if (!_open)
        {
            // 面板关着时保持"期望的时间缩放"：关面板时 UIState.Pop → ApplyPause(false) 会把它重置为 1
            ApplyTimeScale();
            return;
        }

        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + RefreshInterval;
        RefreshValues();
    }

    // ==================== 开关 ====================

    void Open()
    {
        // 死亡结算态不允许打开：否则本帧开（Push 暂停 + 切光标）、下帧就被 `GameOver` 自关 → 闪一下
        if (GameState.GameOver) return;

        _open = true;
        _root.gameObject.SetActive(true);
        UIState.Push();
        RefreshValues();
        Debug.Log("[调试] 面板已打开（再按 ~ 或 F1 关闭）");
    }

    void Close()
    {
        if (!_open) return;
        _open = false;
        _root.gameObject.SetActive(false);
        UIState.Pop();
        ApplyTimeScale();   // ⚠️ 必须在 Pop 之后
        Debug.Log("[调试] 面板已关闭");
    }

    /// <summary>把"期望的时间缩放"写回 `Time.timeScale`。
    /// ⚠️ `1x` 时**完全不写**——调试工具在常态下不该对 `Time.timeScale` 有任何副作用，
    ///    否则会持续覆盖将来可能加入的其它时间缩放（受击顿帧 / 慢动作等）；
    ///    只有用户显式设过非 1x（说明他就是要这个值）时才接管。</summary>
    void ApplyTimeScale()
    {
        if (Mathf.Approximately(_desiredTimeScale, 1f)) return;
        DebugRead.ApplyTimeScaleIfRunning(_desiredTimeScale);
    }

    // ==================== 刷新 ====================

    void RefreshValues()
    {
        // 信息区（常驻）：标签静态，只刷新值
        if (_hpVal != null) _hpVal.text = DebugRead.HpValue();
        if (_speedVal != null) _speedVal.text = DebugRead.SpeedValue();
        if (_ammoVal != null) _ammoVal.text = DebugRead.AmmoValue();
        if (_runVal != null) _runVal.text = DebugRead.RunValue();
        if (_noteText != null) _noteText.text = _note;
        if (_invulLabel != null)
        {
            bool on = DebugRead.PlayerInvulnerable();
            _invulLabel.text = on ? "无敌: 开" : "无敌: 关";
            _invulLabel.color = on ? DebugUI.Warn : DebugUI.Fg;
        }
        if (_godLabel != null) _godLabel.text = _godMode ? "止血: 开" : "止血: 关";
        if (_hudLabel != null && hud != null) _hudLabel.text = hud.visible ? "常驻条: 开" : "常驻条: 关";

        // 当前页才刷新（省开销）
        if (_tab == 2 && _enemyText != null) _enemyText.text = DebugRead.EnemyLines(EnemyShow);
        if (_tab == 3 && _logDirty && _logText != null) { _logDirty = false; _logText.text = BuildLogText(); }
    }

    string BuildLogText()
    {
        if (_logs.Count == 0) return "（暂无日志）";
        int from = Mathf.Max(0, _logs.Count - LogShow);
        var sb = new System.Text.StringBuilder();
        for (int i = from; i < _logs.Count; i++)
        {
            sb.Append(_logs[i]);
            if (i < _logs.Count - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    void OnLog(string message, string stackTrace, LogType type)
    {
        string tag = type == LogType.Error || type == LogType.Exception || type == LogType.Assert
            ? "<color=#FF7366>[错]</color>"
            : type == LogType.Warning ? "<color=#FFD95A>[警]</color>" : "";
        _logs.Add(tag + Truncate(message, 150));
        if (_logs.Count > LogCap) _logs.RemoveAt(0);
        _logDirty = true;
    }

    void SetNote(string message)
    {
        _note = Truncate(message, 46);
        _logs.Add("<color=#8FBF9F>[调试]</color>" + Truncate(message, 150));
        if (_logs.Count > LogCap) _logs.RemoveAt(0);
        _logDirty = true;
        if (_noteText != null) _noteText.text = _note;
    }

    static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ');
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    // ==================== tab ====================

    void SetTab(int i)
    {
        _tab = Mathf.Clamp(i, 0, _page.Length - 1);
        for (int k = 0; k < _page.Length; k++)
        {
            bool on = k == _tab;
            if (_page[k] != null) _page[k].SetActive(on);
            if (_tabLabel[k] != null) _tabLabel[k].color = on ? DebugUI.Fg : DebugUI.Dim;
            var img = _tabBtn[k] != null ? _tabBtn[k].targetGraphic as Image : null;
            if (img != null) img.color = on ? DebugUI.TabOnBg : DebugUI.BgSoft;
        }
        RefreshValues();
    }

    // ==================== 动作（全部走游戏已有的 public 入口） ====================

    void OnFullHeal() { DebugRead.FullHeal(); SetNote("已回满血"); }
    void OnDamage() { DebugRead.Damage(10f); SetNote("扣 10 血（测受伤反馈）"); }
    void OnAddGold() { DebugRead.AddGold(1000); SetNote("金币 +1000"); }

    void OnSpeedStep(int which, float delta)
    {
        var pc = DebugRead.Pc;
        if (pc == null) return;
        if (which == 0) pc.walkSpeed = Mathf.Max(0.5f, pc.walkSpeed + delta);
        else if (which == 1) pc.runSpeed = Mathf.Max(0.5f, pc.runSpeed + delta);
        else pc.aimSpeed = Mathf.Max(0.1f, pc.aimSpeed + delta);
        RefreshValues();
    }

    void OnTeleport()
    {
        SetNote(DebugRead.TeleportToCrosshair()
            ? "已传送到准星地面点（速度已清零）"
            : "传送失败：准星方向没打到地面（Ground 层）");
    }

    void OnToggleInvul()
    {
        bool on = DebugRead.ToggleInvulnerable();
        SetNote(on ? "无敌：开（完全免疫，含一击必杀）" : "无敌：关");
        RefreshValues();
    }

    void OnToggleGod()
    {
        _godMode = !_godMode;
        if (_godLabel != null) _godLabel.text = _godMode ? "止血: 开" : "止血: 关";
        SetNote(_godMode ? "回血式止血：开（每帧拉满血；挡不住一击必杀）" : "回血式止血：关");
    }

    void OnToggleHud()
    {
        if (hud == null) { SetNote("常驻条引用为空（Bootstrap 没注入？）"); return; }
        hud.SetVisible(!hud.visible);
        if (_hudLabel != null) _hudLabel.text = hud.visible ? "常驻条: 开" : "常驻条: 关";
        SetNote(hud.visible ? "常驻条：显示" : "常驻条：隐藏");
    }

    void OnClearLog() { _logs.Clear(); _logDirty = true; SetNote("日志已清空"); }

    void OnGiveItems() => SetNote(DebugRead.GrantAllItems(30));

    void OnKillAll()
    {
        int n = DebugRead.KillAllEnemies();
        // ⚠️ 面板打开时游戏暂停 → WaveManager.Update 早退、状态机不推进；进下一波要**关面板**让游戏跑
        SetNote(n > 0 ? ("已秒杀 " + n + " 个敌人；关面板后走完清场倒计时进下一波") : "场上没有活着的敌人");
    }

    void OnSkipWave() => SetNote(DebugRead.SkipClearingDelay());

    void OnTimeScale(float v)
    {
        _desiredTimeScale = v;
        ApplyTimeScale();
        SetNote("时间缩放：" + v.ToString("0.0") + "x" + (GameState.Paused ? "（关面板后生效）" : ""));
    }

    void OnScreenshot() => SetNote(DebugRead.CaptureScreenshot());

    // ==================== 建 UI ====================

    void BuildUI()
    {
        _canvas = UIUtil.NewOverlayCanvas("DebugPanelCanvas", SortingOrder);
        // ⚠️⚠️ 必须挂到本组件（常驻对象）下：`NewOverlayCanvas` 建的是**场景根节点**，
        //     而死亡重开 `LoadScene(Single)` 会销毁场景根 → 画布没了、组件还在 →
        //     再按 `~` 打开面板会抛 `MissingReferenceException`（2026-10-14 评审发现）。
        _canvas.transform.SetParent(transform, false);
        // ⚠️ 必须显式加 GraphicRaycaster：`UIUtil.NewOverlayCanvas` **只**建 Canvas + Scaler，
        //    没有它 UGUI 永远不把点击派发给本画布 → 所有按钮点了没反应（2026-10-14 踩过）。
        _canvas.gameObject.AddComponent<GraphicRaycaster>();

        _root = DebugUI.NewRect(_canvas.transform, "Panel", new Vector2(W, H), Vector2.zero);
        _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0f, 1f);   // 左上角
        _root.anchoredPosition = new Vector2(8f, -112f);                        // 紧贴常驻条(96+8+8)下方

        DebugUI.NewSolid(_root, "Bg", new Vector2(W, H), Vector2.zero, DebugUI.Bg);

        float y = -8f;

        // ---- 标题 ----
        DebugUI.NewText(_root, "Title", new Vector2(W - 24f, 18f), new Vector2(12f, y), 15, DebugUI.Fg, TextAnchor.MiddleLeft)
               .text = "调试面板    ~ / F1 开关    （打开即暂停；鼠标操作用虚拟光标）";
        y -= 20f;

        // ---- 常驻信息区（两列：左标签静态 / 右值刷新）----
        InfoRow("LblHp", "HP", y, out _hpVal); y -= 17f;
        InfoRow("LblSpd", "移速", y, out _speedVal); y -= 17f;
        InfoRow("LblAmmo", "武器", y, out _ammoVal); y -= 17f;
        InfoRow("LblRun", "进度", y, out _runVal); y -= 17f;

        DebugUI.NewHSep(_root, "Sep1", W, new Vector2(0f, y)); y -= 8f;

        // ---- 5 个 tab ----
        string[] titles = { "战斗", "波次", "敌人", "日志", "杂项" };
        for (int i = 0; i < titles.Length; i++)
        {
            int idx = i;
            var b = DebugUI.NewButton(_root, "Tab" + i, titles[i], new Vector2(110f, 24f),
                                      new Vector2(12f + i * 114f, y), 13, DebugUI.Dim, () => SetTab(idx));
            _tabBtn[i] = b;
            _tabLabel[i] = DebugUI.LabelOf(b);
        }
        y -= 28f;

        DebugUI.NewHSep(_root, "Sep2", W, new Vector2(0f, y)); y -= 8f;

        // ---- 内容区（5 页，一次只激活一页）----
        var content = DebugUI.NewRect(_root, "Content", new Vector2(W, ContentH), new Vector2(0f, y));
        BuildPageBattle(content, 0);
        BuildPageWave(content, 1);
        BuildPageEnemy(content, 2);
        BuildPageLog(content, 3);
        BuildPageMisc(content, 4);
        y -= ContentH + 4f;

        // ---- 底部提示行 ----
        _noteText = DebugUI.NewText(_root, "Note", new Vector2(W - 24f, 16f), new Vector2(12f, y), 12, DebugUI.Warn, TextAnchor.MiddleLeft);

        UIUtil.SetLayerRecursively(_canvas.gameObject, UIUtil.UILayer);
        SetTab(0);
    }

    /// <summary>信息区一行：左侧固定宽标签（静态）+ 右侧值（刷新）。</summary>
    void InfoRow(string name, string label, float y, out Text value)
    {
        DebugUI.NewText(_root, name, new Vector2(56f, 16f), new Vector2(12f, y), 12, DebugUI.Dim, TextAnchor.MiddleLeft).text = label;
        value = DebugUI.NewText(_root, name + "V", new Vector2(W - 84f, 16f), new Vector2(72f, y), 12, DebugUI.Bright, TextAnchor.MiddleLeft);
    }

    // ---------- 各页 ----------

    void BuildPageBattle(RectTransform content, int index)
    {
        var page = DebugUI.NewRect(content, "Page" + index, new Vector2(W, ContentH), Vector2.zero);
        _page[index] = page.gameObject;

        DebugUI.NewButton(page, "BtnHeal", "满血", _btn, new Vector2(0f, 0f), 13, DebugUI.Fg, OnFullHeal);
        DebugUI.NewButton(page, "BtnDmg", "扣 10 血", _btn, new Vector2(196f, 0f), 13, DebugUI.Fg, OnDamage);
        _invulLabel = DebugUI.LabelOf(DebugUI.NewButton(page, "BtnInvul", "无敌: 关", _btn, new Vector2(392f, 0f), 13, DebugUI.Fg, OnToggleInvul));

        _godLabel = DebugUI.LabelOf(DebugUI.NewButton(page, "BtnGod", "止血: 关", _btn, new Vector2(0f, -30f), 13, DebugUI.Fg, OnToggleGod));
        DebugUI.NewButton(page, "BtnGive", "给武器弹药", _btn, new Vector2(196f, -30f), 13, DebugUI.Fg, OnGiveItems);
        DebugUI.NewButton(page, "BtnTp", "传送到准星", _btn, new Vector2(392f, -30f), 13, DebugUI.Fg, OnTeleport);

        // 移速微调（放在战斗页底部两行）
        DebugUI.NewText(page, "LblWalk", new Vector2(40f, 22f), new Vector2(0f, -68f), 12, DebugUI.Dim, TextAnchor.MiddleLeft).text = "走";
        DebugUI.NewButton(page, "WalkD", "−", new Vector2(30f, 22f), new Vector2(40f, -68f), 13, DebugUI.Fg, () => OnSpeedStep(0, -SpeedStep));
        DebugUI.NewButton(page, "WalkU", "+", new Vector2(30f, 22f), new Vector2(74f, -68f), 13, DebugUI.Fg, () => OnSpeedStep(0, SpeedStep));
        DebugUI.NewText(page, "LblRun", new Vector2(40f, 22f), new Vector2(120f, -68f), 12, DebugUI.Dim, TextAnchor.MiddleLeft).text = "跑";
        DebugUI.NewButton(page, "RunD", "−", new Vector2(30f, 22f), new Vector2(160f, -68f), 13, DebugUI.Fg, () => OnSpeedStep(1, -SpeedStep));
        DebugUI.NewButton(page, "RunU", "+", new Vector2(30f, 22f), new Vector2(194f, -68f), 13, DebugUI.Fg, () => OnSpeedStep(1, SpeedStep));
        DebugUI.NewText(page, "LblAim", new Vector2(40f, 22f), new Vector2(240f, -68f), 12, DebugUI.Dim, TextAnchor.MiddleLeft).text = "瞄";
        DebugUI.NewButton(page, "AimD", "−", new Vector2(30f, 22f), new Vector2(280f, -68f), 13, DebugUI.Fg, () => OnSpeedStep(2, -SpeedStep));
        DebugUI.NewButton(page, "AimU", "+", new Vector2(30f, 22f), new Vector2(314f, -68f), 13, DebugUI.Fg, () => OnSpeedStep(2, SpeedStep));
    }

    void BuildPageWave(RectTransform content, int index)
    {
        var page = DebugUI.NewRect(content, "Page" + index, new Vector2(W, ContentH), Vector2.zero);
        _page[index] = page.gameObject;

        DebugUI.NewButton(page, "BtnKillAll", "秒杀全场敌人", _btn, new Vector2(0f, 0f), 13, DebugUI.Fg, OnKillAll);
        DebugUI.NewButton(page, "BtnSkip", "跳过清场倒计时", _btn, new Vector2(196f, 0f), 13, DebugUI.Fg, OnSkipWave);
        DebugUI.NewButton(page, "BtnGold", "金币 +1000", _btn, new Vector2(392f, 0f), 13, DebugUI.Warn, OnAddGold);
    }

    void BuildPageEnemy(RectTransform content, int index)
    {
        var page = DebugUI.NewRect(content, "Page" + index, new Vector2(W, ContentH), Vector2.zero);
        _page[index] = page.gameObject;

        DebugUI.NewText(page, "Hdr", new Vector2(W - 16f, 16f), new Vector2(8f, 0f), 12, DebugUI.Dim, TextAnchor.MiddleLeft)
               .text = "名字   行为   血量   状态   距离";
        _enemyText = DebugUI.NewText(page, "List", new Vector2(W - 16f, 172f), new Vector2(8f, -18f), 12, DebugUI.Bright, TextAnchor.UpperLeft);
        _enemyText.text = "（场上无敌人）";
    }

    void BuildPageLog(RectTransform content, int index)
    {
        var page = DebugUI.NewRect(content, "Page" + index, new Vector2(W, ContentH), Vector2.zero);
        _page[index] = page.gameObject;

        _logText = DebugUI.NewText(page, "List", new Vector2(W - 16f, 168f), new Vector2(8f, 0f), 12, DebugUI.Bright, TextAnchor.UpperLeft);
        _logText.text = "（暂无日志）";
        DebugUI.NewButton(page, "BtnClear", "清空日志", new Vector2(90f, 24f), new Vector2(8f, -172f), 12, DebugUI.Fg, OnClearLog);
    }

    void BuildPageMisc(RectTransform content, int index)
    {
        var page = DebugUI.NewRect(content, "Page" + index, new Vector2(W, ContentH), Vector2.zero);
        _page[index] = page.gameObject;

        DebugUI.NewText(page, "LblTime", new Vector2(44f, 24f), new Vector2(0f, 0f), 12, DebugUI.Dim, TextAnchor.MiddleLeft).text = "时间";
        DebugUI.NewButton(page, "T01", "0.1x", _small, new Vector2(48f, 0f), 12, DebugUI.Fg, () => OnTimeScale(0.1f));
        DebugUI.NewButton(page, "T05", "0.5x", _small, new Vector2(100f, 0f), 12, DebugUI.Fg, () => OnTimeScale(0.5f));
        DebugUI.NewButton(page, "T10", "1x", _small, new Vector2(152f, 0f), 12, DebugUI.Fg, () => OnTimeScale(1f));
        DebugUI.NewButton(page, "T20", "2x", _small, new Vector2(204f, 0f), 12, DebugUI.Fg, () => OnTimeScale(2f));

        DebugUI.NewButton(page, "BtnShot", "一键截图", _small, new Vector2(0f, -34f), 12, DebugUI.Fg, OnScreenshot);
        _hudLabel = DebugUI.LabelOf(DebugUI.NewButton(page, "BtnHud", "常驻条: 开", new Vector2(110f, 26f), new Vector2(100f, -34f), 12, DebugUI.Fg, OnToggleHud));
    }

    // 按钮尺寸（实例只读字段：不引入任何静态状态）
    readonly Vector2 _btn = new Vector2(190f, 26f);   // 战斗/波次页按钮
    readonly Vector2 _small = new Vector2(48f, 26f);  // 杂项页小按钮
}
#endif
