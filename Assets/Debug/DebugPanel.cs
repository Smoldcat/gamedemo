#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// **调试详情面板**：按 `~`（或 `F1`）呼出/收起。终端风（纯色 + 1px 线，无贴图）。
///
/// 结构（2026-10-14 用户定：功能变多后一屏展示太乱 → 分 tab；调试 UI 以简洁与效率优先）：
///   · **顶部常驻信息区**（不随 tab 变）：HP / 移速 / 武器 / 进度 —— 两列，**左标签静态、右值节流刷新**；
///   · 中部 **7 个 tab**（战斗 / 波次 / 敌人 / 物品 / 日志 / 杂项 / 视觉），**一次只激活当前页**（其余 SetActive(false) → UGUI 不处理）；
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

    const float W = 620f, H = 380f;      // 面板尺寸（高度按最大页固定 → 切 tab 不跳动）
    const float ContentH = 200f;         // 内容区高度
    const int TabCount = 7;              // 战斗 / 波次 / 敌人 / 物品 / 日志 / 杂项 / 视觉（2026-10-16 加「视觉」）

    [Tooltip("常驻条（由 DebugBootstrap 自动注入）。")]
    public DebugHud hud;

    RectTransform _content;              // 可滚动的"内容容器"（滚轮改它的 anchoredPosition.y）
    float _scrollY;                      // 当前滚动偏移（0 = 顶部）
    const float ScrollStep = 60f;        // 滚轮一格滚多少像素

    Canvas _canvas;
    RectTransform _root;

    // 常驻信息区（值）
    Text _hpVal, _speedVal, _ammoVal, _magicVal, _runVal, _noteText;

    // tab 与页面
    readonly Button[] _tabBtn = new Button[TabCount];
    readonly Text[] _tabLabel = new Text[TabCount];
    readonly GameObject[] _page = new GameObject[TabCount];
    int _tab;

    // 页面内容引用
    Text _enemyText, _logText;
    Text _invulLabel, _hudLabel;
    Text _godLabel, _noMagicCdLabel, _noAmmoLabel;

    // 物品页状态（可刷物品清单：打开时构建一次，之后只在换 tab 时读）
    List<ItemData> _items;
    int _itemIdx;
    int _itemCount = 5;
    Text _itemNameText, _itemCountText;

    // 杂项页：相机参数当前值
    Text _camFollowVal, _camLeadVal, _camLeadMaxVal, _camFovVal;

    // 视觉页（2026-10-16）：可视化开关（纯显示）+ 注入的可视化组件
    [Tooltip("可视化组件（由 DebugBootstrap 自动注入）。")]
    public DebugOverlay overlay;
    [Tooltip("效果生效范围指示器（由 DebugBootstrap 自动注入）。")]
    public DebugRangeIndicator rangeIndicator;
    Text _coneLabel, _seenLabel, _rangeLabel, _rangeIndLabel;

    // 敌人页：测试辅助状态（实例字段，不引入静态）
    bool _enemyFrozen;
    Text _frozenLabel;
    float _barsNext;                                                               // 冻结时血条代刷的节流
    readonly Dictionary<Health, float> _origMaxHp = new Dictionary<Health, float>();   // 被增血的敌人的原 maxHp

    bool _open, _godMode, _logDirty, _noMagicCd, _noAmmoUse;
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
        // 提醒易遗忘的状态：冻结开关跨场景重开保持开启（这是有意的"开关保持"语义）
        if (_enemyFrozen) SetNote("场景已重载；敌人冻结仍生效（新场景的敌人也会被自动冻住）");
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

        // 敌人冻结：**每帧压制**（新刷出的敌人下一帧也会被冻住；面板关着也生效 —— 用户此时正在打输出测试）
        if (_enemyFrozen) FreezeEnemyTick();

        // 魔法无冷却：**每帧压制**（面板关着也要生效 —— 用户此时正在连续放技能）。
        // 与「清魔法冷却」按钮的区别：那个是**一次性**归零，这个是**持续**的（放完立刻清 → 等于没有 CD）。
        // ⚠️ 主动 / 被动都覆盖：两者读的都是同一个 `PlayerMagic.CooldownRemaining`（`PassiveReady` 也是它）。
        if (_noMagicCd) SuppressMagicCdTick();

        // 射击不消耗子弹：**每帧压制**（面板关着也要生效 —— 用户此时正在打靶测试手感）。
        // ⚠️ 覆盖的是"弹匣"（`magAmmo`）；备弹不动，但弹匣始终满 → 打不光、也永远轮不到"空弹自动换弹"。
        if (_noAmmoUse) SuppressAmmoTick();

        if (!_open)
        {
            // 面板关着时保持"期望的时间缩放"：关面板时 UIState.Pop → ApplyPause(false) 会把它重置为 1
            ApplyTimeScale();
            return;
        }

        // ---- 滚轮滚动内容区（2026-10-17 新增）----
        // ⚠️ 放在 `!_open` 早退**之后**（2026-10-17 修）：面板关着时不该响应滚轮 —— 否则将来把内容容器
        //    高度调大后，关着面板滚轮也会"预滚动"内容，开面板那一瞬间就是错位的。
        // ⚠️ **刻意不依赖 EventSystem / ScrollRect**：面板打开时光标是 Confined + 虚拟光标，
        //    直接读原始滚轮输入最可靠；这里只改内容容器的 anchoredPosition。
        if (_content != null)
        {
            float maxScroll = Mathf.Max(0f, _content.sizeDelta.y - ContentH);
            float wheel = Input.mouseScrollDelta.y;
            if (maxScroll > 0.5f && Mathf.Abs(wheel) > 0.01f)
            {
                // ⚠️ 这里是**减号**：与项目既有滚动手感一致（`InvGridUI` 用的是 `_scrollTarget - dy * ScrollStep`）。
                //    上滚（wheel > 0）→ 内容下移 → 露出**上方**内容。
                _scrollY = Mathf.Clamp(_scrollY - wheel * ScrollStep, 0f, maxScroll);
                _content.anchoredPosition = new Vector2(0f, _scrollY);
            }
            else if (_scrollY > 0.01f && maxScroll <= 0.5f)   // 内容已不再超出 → 归零（避免切页后残留错位）
            {
                _scrollY = 0f;
                _content.anchoredPosition = Vector2.zero;
            }
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
        if (_magicVal != null) _magicVal.text = DebugRead.MagicValue();
        if (_runVal != null) _runVal.text = DebugRead.RunValue();
        if (_noteText != null) _noteText.text = _note;
        if (_invulLabel != null)
        {
            bool on = DebugRead.PlayerInvulnerable();
            _invulLabel.text = on ? "无敌: 开" : "无敌: 关";
            _invulLabel.color = on ? DebugUI.Warn : DebugUI.Fg;
        }
        if (_godLabel != null) _godLabel.text = _godMode ? "止血: 开" : "止血: 关";
        if (_noMagicCdLabel != null) _noMagicCdLabel.text = _noMagicCd ? "魔法无冷却: 开" : "魔法无冷却: 关";
        if (_noAmmoLabel != null) _noAmmoLabel.text = _noAmmoUse ? "射击不耗弹: 开" : "射击不耗弹: 关";
        if (_hudLabel != null && hud != null) _hudLabel.text = hud.visible ? "常驻条: 开" : "常驻条: 关";

        // 当前页才刷新（省开销）
        if (_tab == 2 && _enemyText != null) _enemyText.text = DebugRead.EnemyLines(EnemyShow);
        if (_tab == 3) RefreshItemPage();
        if (_tab == 4 && _logDirty && _logText != null) { _logDirty = false; _logText.text = BuildLogText(); }
        if (_tab == 5) RefreshCameraValues();
        if (_tab == 6) SyncVisLabels();
    }

    /// <summary>物品页刷新：清单只构建一次；之后只更新"当前选中名 + 数量"。</summary>
    void RefreshItemPage()
    {
        if (_items == null || _items.Count == 0) _items = DebugRead.BuildSpawnableItems();
        if (_itemNameText != null)
        {
            if (_items == null || _items.Count == 0) _itemNameText.text = "（物品清单为空：跑一次 Tools/游戏/重建物品清单）";
            else
            {
                _itemIdx = Mathf.Clamp(_itemIdx, 0, _items.Count - 1);
                var def = _items[_itemIdx];
                _itemNameText.text = string.Format("[{0}/{1}]  {2}", _itemIdx + 1, _items.Count,
                                                   def != null ? def.DisplayName : "?");
            }
        }
        if (_itemCountText != null) _itemCountText.text = "×" + _itemCount;
    }

    /// <summary>杂项页：相机参数当前值。</summary>
    void RefreshCameraValues()
    {
        var cam = DebugRead.Cam;
        if (cam == null) return;
        if (_camFollowVal != null) _camFollowVal.text = cam.followLerp.ToString("0.0");
        if (_camLeadVal != null) _camLeadVal.text = cam.aimLeadRatio.ToString("0.00");
        if (_camLeadMaxVal != null) _camLeadMaxVal.text = cam.maxAimLead.ToString("0.0");
        if (_camFovVal != null) _camFovVal.text = cam.aimFovNarrow.ToString("0.0");
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

        // 切页时把滚动位置归零（2026-10-17）：否则从"滚过的页"切到"需要滚的页"会停在半截
        _scrollY = 0f;
        if (_content != null) _content.anchoredPosition = Vector2.zero;
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

    void OnKillPlayer() => SetNote(DebugRead.KillPlayer());

    void OnClearMagicCd()
    {
        var pm = PlayerMagic.Instance;
        if (pm == null) { SetNote("找不到 PlayerMagic"); return; }
        pm.ClearCooldown();
        SetNote("魔法冷却已清零");
    }

    /// <summary>魔法无冷却开关：打开后由 <see cref="Update"/> 每帧压制冷却（放完立刻清），关上即恢复。
    /// ⚠️ 与「清魔法冷却」按钮的区别：那个是**一次性**归零，这个是**持续**的 —— 连续放技能时用这个。</summary>
    void OnToggleNoMagicCd()
    {
        _noMagicCd = !_noMagicCd;
        if (_noMagicCdLabel != null)
        {
            _noMagicCdLabel.text = _noMagicCd ? "魔法无冷却: 开" : "魔法无冷却: 关";
            _noMagicCdLabel.color = _noMagicCd ? DebugUI.Warn : DebugUI.Fg;
        }
        if (_noMagicCd)
        {
            SuppressMagicCdTick();   // 立刻生效一次，不用等下一次施法
            SetNote("魔法无冷却：开（放完立刻清 CD；关着面板也在生效）");
        }
        else SetNote("魔法无冷却：关");
    }

    /// <summary>魔法无冷却的每帧动作：冷却还没走完就清掉。走 <see cref="PlayerMagic.ClearCooldown"/>
    /// （README 登记表里的 `[DEBUG-ONLY]` 中性方法）。
    /// ⚠️ 只在"确实还有冷却"时才调 → 常态零开销；主动 / 被动共用同一个 `CooldownRemaining`，所以两者都覆盖。</summary>
    void SuppressMagicCdTick()
    {
        var pm = PlayerMagic.Instance;
        if (pm != null && pm.CooldownRemaining > 0f) pm.ClearCooldown();
    }

    /// <summary>「射击不耗弹」开关：打开后由 <see cref="Update"/> 每帧把弹匣补满。</summary>
    void OnToggleNoAmmo()
    {
        _noAmmoUse = !_noAmmoUse;
        if (_noAmmoLabel != null)
        {
            _noAmmoLabel.text = _noAmmoUse ? "射击不耗弹: 开" : "射击不耗弹: 关";
            _noAmmoLabel.color = _noAmmoUse ? DebugUI.Warn : DebugUI.Fg;
        }
        if (_noAmmoUse)
        {
            SuppressAmmoTick();   // 立刻补满，不用等开下一枪
            SetNote("射击不耗弹：开（弹匣每帧补满；关着面板也在生效）");
        }
        else SetNote("射击不耗弹：关");
    }

    /// <summary>「射击不耗弹」的每帧动作：弹匣没满就补满 —— `PlayerController.Fire()` 里那句 `magAmmo--`
    /// 因此等于没扣（开火判定读的是同一帧补满后的值，所以也不会走"空弹自动换弹"）。
    /// ⚠️ 只写游戏**已有的 public 字段**（`PlayerController.weapon` / `magAmmo`）→ **不需要往核心加任何方法**。
    /// ⚠️ 只在"确实不满"时才写 → 常态零开销，也不会每帧无谓地碰游戏状态。
    /// 📌 语义边界：它**不碰备弹**（背包里的弹药数量）；但弹匣始终满 → 打不光。</summary>
    void SuppressAmmoTick()
    {
        var pc = PlayerController.Instance;
        if (pc != null && pc.weapon != null && pc.magAmmo < pc.weapon.magSize)
            pc.magAmmo = pc.weapon.magSize;
    }

    // ---------- 物品页 ----------

    void StepItem(int dir)
    {
        if (_items == null || _items.Count == 0) _items = DebugRead.BuildSpawnableItems();
        if (_items == null || _items.Count == 0) { SetNote("物品清单为空（跑一次 Tools/游戏/重建物品清单）"); return; }
        _itemIdx = (_itemIdx + dir + _items.Count) % _items.Count;
        RefreshItemPage();
    }

    void StepItemCount(int d)
    {
        _itemCount = Mathf.Clamp(_itemCount + d, 1, 99);
        RefreshItemPage();
    }

    ItemData CurrentItem()
        => (_items != null && _items.Count > 0) ? _items[Mathf.Clamp(_itemIdx, 0, _items.Count - 1)] : null;

    void OnSpawnPickupAtCrosshair() => SetNote(DebugRead.SpawnPickupAtCrosshair(CurrentItem(), _itemCount));
    void OnSpawnPickupRandom() => SetNote(DebugRead.SpawnPickupRandom(CurrentItem(), _itemCount));
    void OnGiveItemToBackpack() => SetNote(DebugRead.GiveToBackpack(CurrentItem(), _itemCount));
    void OnSpawnEnemy() => SetNote(DebugRead.SpawnEnemyAtCrosshair());
    void OnShopRestock() => SetNote(DebugRead.ShopForceRestockAll());
    void OnClearPickups() => SetNote(DebugRead.ClearAllPickups());

    // ---------- 敌人页（测武器数值与手感） ----------

    /// <summary>冻结敌人：**每帧压制**（关 AI/行为 + 清速度）—— 新刷出的敌人下一帧也会被冻住。
    /// ⚠️ 两个细节（2026-10-15 评审发现）：
    ///   ① 狙击手必须先 `HideLaser()` —— 否则瞄准中的红线会**永久冻在屏幕上**（`LaserBeam.cs:20` 约定）；
    ///   ② 冻结停了 `FixedUpdate` → 头顶血条不再更新/朝向相机 → 这里节流代刷（测武器掉血时血条要动）。</summary>
    void FreezeEnemyTick()
    {
        var list = EnemyAI.All;
        for (int i = 0; i < list.Count; i++)
        {
            var ai = list[i];
            if (ai == null) continue;
            var h = ai.HealthRef;
            if (h == null || !h.alive) continue;
            if (ai.behaviour is EnemySniperBehaviour sniper) sniper.HideLaser();   // ⚠️ 红线不能冻在屏幕上
            if (ai.enabled) ai.enabled = false;
            if (ai.behaviour != null && ai.behaviour.enabled) ai.behaviour.enabled = false;
            var rb = ai.GetComponent<Rigidbody>();
            if (rb != null && rb.velocity.sqrMagnitude > 0.0001f) rb.velocity = Vector3.zero;
        }

        // 血条节流代刷（0.2s 一次）：测武器掉血时头顶血条要能看见变化
        if (Time.unscaledTime >= _barsNext)
        {
            _barsNext = Time.unscaledTime + 0.2f;
            for (int i = 0; i < list.Count; i++)
            {
                var ai = list[i];
                if (ai == null) continue;
                var h = ai.HealthRef;
                if (h == null || !h.alive) continue;
                var bar = ai.GetComponentInChildren<HealthBar>();
                if (bar != null) bar.UpdateBar(h.hp / Mathf.Max(0.01f, h.maxHp));
            }
        }
    }

    void OnToggleEnemyFreeze()
    {
        _enemyFrozen = !_enemyFrozen;
        string msg;
        if (_enemyFrozen) { int n = DebugRead.FreezeAllEnemies(); msg = string.Format("已冻结 {0} 个敌人（木桩模式；解冻时引信中的自爆兵会立即爆炸、狙击手会立刻开火）", n); }
        else { int n = DebugRead.UnfreezeEnemies(); msg = string.Format("已解冻 {0} 个敌人", n); }
        if (_frozenLabel != null)
        {
            _frozenLabel.text = _enemyFrozen ? "冻结: 开" : "冻结: 关";
            _frozenLabel.color = _enemyFrozen ? DebugUI.Warn : DebugUI.Fg;
        }
        SetNote(msg);
    }

    /// <summary>给所有存活敌人增加 maxHp 并回满（记录原值供"恢复默认"）。</summary>
    void OnEnemyHpAdd(float add)
    {
        int n = 0;
        foreach (var ai in DebugRead.AliveEnemyAIs())
        {
            var h = ai.HealthRef;
            if (!_origMaxHp.ContainsKey(h)) _origMaxHp[h] = h.maxHp;
            h.maxHp += add;
            h.hp = h.maxHp;
            n++;
        }
        SetNote(n > 0 ? string.Format("已给 {0} 个敌人 maxHp +{1} 并回满", n, add) : "场上没有活着的敌人");
    }

    void OnEnemyHpMult(float mult)
    {
        int n = 0;
        foreach (var ai in DebugRead.AliveEnemyAIs())
        {
            var h = ai.HealthRef;
            if (!_origMaxHp.ContainsKey(h)) _origMaxHp[h] = h.maxHp;
            h.maxHp = Mathf.Max(1f, h.maxHp * mult);
            h.hp = h.maxHp;
            n++;
        }
        SetNote(n > 0 ? string.Format("已给 {0} 个敌人 maxHp ×{1} 并回满", n, mult) : "场上没有活着的敌人");
    }

    void OnEnemyHpRestore()
    {
        int n = 0;
        var keys = new List<Health>(_origMaxHp.Keys);   // 快照（可能有已销毁/已死亡的条目）
        foreach (var h in keys)
        {
            if (h == null) { _origMaxHp.Remove(h); continue; }
            h.maxHp = _origMaxHp[h];
            if (h.hp > h.maxHp) h.hp = h.maxHp;
            _origMaxHp.Remove(h);
            n++;
        }
        SetNote(n > 0 ? string.Format("已恢复 {0} 个敌人的默认血量", n) : "没有记录过被改过的敌人");
    }

    void OnSpawnEnemies() => SetNote(string.Format("已刷出 {0} 个敌人（随机空位）", DebugRead.SpawnEnemiesAtRandomSpots(5)));

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
        InfoRow("LblMagic", "魔法", y, out _magicVal); y -= 17f;
        InfoRow("LblRun", "进度", y, out _runVal); y -= 17f;

        DebugUI.NewHSep(_root, "Sep1", W, new Vector2(0f, y)); y -= 8f;

        // ---- 7 个 tab（索引：0战斗 1波次 2敌人 3物品 4日志 5杂项 6视觉）----
        // 7 个 tab：宽度收到 80 / 步距 84（12 + 6×84 + 80 = 596 < 面板宽 620，放得下）
        string[] titles = { "战斗", "波次", "敌人", "物品", "日志", "杂项", "视觉" };
        for (int i = 0; i < titles.Length; i++)
        {
            int idx = i;
            var b = DebugUI.NewButton(_root, "Tab" + i, titles[i], new Vector2(80f, 24f),
                                      new Vector2(12f + i * 84f, y), 13, DebugUI.Dim, () => SetTab(idx));
            _tabBtn[i] = b;
            _tabLabel[i] = DebugUI.LabelOf(b);
        }
        y -= 28f;

        DebugUI.NewHSep(_root, "Sep2", W, new Vector2(0f, y)); y -= 8f;

        // ---- 内容区（7 页，一次只激活一页）----
        // ⚠️ 2026-10-17：内容区外包一层**裁剪视口** + 可滚动的**内容容器** ——
        //    以后某一页的按钮/数据变多、超过 `ContentH` 时，用**鼠标滚轮**就能看全（不再是"挤成一团看不完"）。
        //    视口固定 `ContentH`；内容容器高度独立 —— 现在 = `ContentH`（即"暂时不需要滚"），
        //    以后某页内容超过它时，把下面的 `new Vector2(W, ContentH)` 改大即可，**滚动逻辑已经就绪**。
        var viewport = DebugUI.NewRect(_root, "Viewport", new Vector2(W, ContentH), new Vector2(0f, y));
        viewport.gameObject.AddComponent<RectMask2D>();   // 裁剪：滚出去的部分不显示（不需要 sprite，比 Mask 轻）
        var content = DebugUI.NewRect(viewport, "Content", new Vector2(W, ContentH), Vector2.zero);
        _content = content;
        BuildPageBattle(content, 0);
        BuildPageWave(content, 1);
        BuildPageEnemy(content, 2);
        BuildPageItems(content, 3);
        BuildPageLog(content, 4);
        BuildPageMisc(content, 5);
        BuildPageVisual(content, 6);
        y -= ContentH + 4f;

        // ---- 底部提示行 ----
        _noteText = DebugUI.NewText(_root, "Note", new Vector2(W - 24f, 16f), new Vector2(12f, y), 12, DebugUI.Warn, TextAnchor.MiddleLeft);

        Layers.SetRecursively(_canvas.gameObject, UIUtil.UILayer);
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

        // 一键杀死玩家（测死亡结算 / 重开清场是否干净）
        DebugUI.NewButton(page, "BtnKillPlayer", "杀死玩家", _btn, new Vector2(392f, -68f), 13, DebugUI.Error, OnKillPlayer);

        // 射击不耗弹（连发 / 压枪 / 手感测试时不用反复换弹；语义边界见 OnToggleNoAmmo 注释）
        _noAmmoLabel = DebugUI.LabelOf(DebugUI.NewButton(page, "BtnNoAmmo", "射击不耗弹: 关", _btn, new Vector2(0f, -100f), 13, DebugUI.Dim, OnToggleNoAmmo));
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

        // ---- 敌人测试按钮行（测武器数值与手感用）----
        _frozenLabel = DebugUI.LabelOf(DebugUI.NewButton(page, "BtnFreeze", "冻结行动: 关", new Vector2(116f, 24f), new Vector2(0f, 0f), 12, DebugUI.Fg, OnToggleEnemyFreeze));
        DebugUI.NewButton(page, "BtnHpAdd", "血量+500", new Vector2(96f, 24f), new Vector2(120f, 0f), 12, DebugUI.Fg, () => OnEnemyHpAdd(500f));
        DebugUI.NewButton(page, "BtnHpMul", "血量×5", new Vector2(88f, 24f), new Vector2(220f, 0f), 12, DebugUI.Fg, () => OnEnemyHpMult(5f));
        DebugUI.NewButton(page, "BtnHpReset", "恢复默认", new Vector2(96f, 24f), new Vector2(312f, 0f), 12, DebugUI.Fg, OnEnemyHpRestore);
        DebugUI.NewButton(page, "BtnSpawn5", "刷5个敌人", new Vector2(110f, 24f), new Vector2(412f, 0f), 12, DebugUI.Fg, OnSpawnEnemies);

        // ---- 敌人列表 ----
        DebugUI.NewText(page, "Hdr", new Vector2(W - 16f, 16f), new Vector2(8f, -30f), 12, DebugUI.Dim, TextAnchor.MiddleLeft)
               .text = "名字   行为   血量   状态   距离";
        _enemyText = DebugUI.NewText(page, "List", new Vector2(W - 16f, 156f), new Vector2(8f, -48f), 12, DebugUI.Bright, TextAnchor.UpperLeft);
        _enemyText.text = "（场上无敌人）";
    }

    /// <summary>视觉页（2026-10-16 新增）：**纯显示**的可视化开关 —— 全部只改 `DebugOverlay` / `DebugRangeIndicator`
    /// 的开关，不碰任何玩法状态与数值。
    /// ⚠️ 2026-10-17：**页内说明文字已删**（用户："默认会打开调试面板的都是懂相关功能的"）——
    ///    按钮一多就会挤，改由**滚轮滚动**兜底（见 `Build()` 的 Viewport/Content 与 `Update()` 的滚轮处理）。</summary>
    void BuildPageVisual(RectTransform content, int index)
    {
        var page = DebugUI.NewRect(content, "Page" + index, new Vector2(W, ContentH), Vector2.zero);
        _page[index] = page.gameObject;

        // ⚠️ 初始文字与 `DebugOverlay` 的默认值一致（两者都是"默认关"）；切到本页时会再 SyncVisLabels() 对齐一次
        //    （注意：初始化这一刻 `overlay` 还没被注入 —— Bootstrap 是 AddComponent 之后才赋值的 —— 所以这里必须写死"关"）
        _coneLabel = DebugUI.LabelOf(DebugUI.NewButton(page, "BtnCone", "视野扇形: 关", new Vector2(148f, 26f), new Vector2(0f, 0f), 13, DebugUI.Dim, OnToggleCone));
        _seenLabel = DebugUI.LabelOf(DebugUI.NewButton(page, "BtnSeen", "已发现高亮: 开", new Vector2(168f, 26f), new Vector2(158f, 0f), 13, DebugUI.Fg, OnToggleSeen));
        _rangeLabel = DebugUI.LabelOf(DebugUI.NewButton(page, "BtnRange", "判定圈: 关", new Vector2(148f, 26f), new Vector2(336f, 0f), 13, DebugUI.Dim, OnToggleRanges));
        // 第二行：效果范围（爆炸保留 1 秒 / 火焰与烟云存续期间一直画）+ 全部开 / 全部关
        // ⚠️ 位置必须与上一行的三个错开（2026-10-17 修：第一版把「效果范围」和「全部开」都放在 (0,-34) → 完全重叠）
        _rangeIndLabel = DebugUI.LabelOf(DebugUI.NewButton(page, "BtnRangeInd", "效果范围: 关", new Vector2(148f, 26f), new Vector2(0f, -34f), 13, DebugUI.Dim, OnToggleRangeInd));
        DebugUI.NewButton(page, "BtnVisAll", "全部开", new Vector2(96f, 26f), new Vector2(158f, -34f), 13, DebugUI.Fg, () => SetAllVisual(true));
        DebugUI.NewButton(page, "BtnVisNone", "全部关", new Vector2(96f, 26f), new Vector2(264f, -34f), 13, DebugUI.Fg, () => SetAllVisual(false));
        // 📌 本页曾有 7 行说明文字，2026-10-17 按用户要求**删除**（"默认会打开调试面板的都是懂相关功能的"）：
        //    按钮一多就会挤，改由**滚轮滚动**兜底（见 `Build()` 的 Viewport/Content 与 `Update()` 的滚轮处理）。
    }

    void OnToggleCone() { if (overlay == null) return; overlay.showCones = !overlay.showCones; SyncVisLabels(); }
    void OnToggleSeen() { if (overlay == null) return; overlay.highlightSeen = !overlay.highlightSeen; SyncVisLabels(); }
    void OnToggleRanges() { if (overlay == null) return; overlay.showRanges = !overlay.showRanges; SyncVisLabels(); }
    void OnToggleRangeInd() { if (rangeIndicator == null) return; rangeIndicator.visible = !rangeIndicator.visible; SyncVisLabels(); }

    void SetAllVisual(bool on)
    {
        if (overlay == null) return;
        overlay.showCones = on;
        overlay.highlightSeen = on;
        overlay.showRanges = on;
        if (rangeIndicator != null) rangeIndicator.visible = on;
        SyncVisLabels();
        SetNote(on ? "可视化：全部打开" : "可视化：全部关闭");
    }

    /// <summary>可视化开关按钮的文字/颜色与组件状态同步（颜色：开=正常、关=暗）。
    /// ⚠️ 2026-10-17 修：**不能因为 `overlay` 为空就整体早退** —— `overlay`（视野扇形/判定圈）与
    ///    `rangeIndicator`（效果范围）是**两个独立注入项**，前者缺失时后者的标签仍必须能同步。</summary>
    void SyncVisLabels()
    {
        if (overlay != null)
        {
            if (_coneLabel != null) { _coneLabel.text = overlay.showCones ? "视野扇形: 开" : "视野扇形: 关"; _coneLabel.color = overlay.showCones ? DebugUI.Fg : DebugUI.Dim; }
            if (_seenLabel != null) { _seenLabel.text = overlay.highlightSeen ? "已发现高亮: 开" : "已发现高亮: 关"; _seenLabel.color = overlay.highlightSeen ? DebugUI.Fg : DebugUI.Dim; }
            if (_rangeLabel != null) { _rangeLabel.text = overlay.showRanges ? "判定圈: 开" : "判定圈: 关"; _rangeLabel.color = overlay.showRanges ? DebugUI.Fg : DebugUI.Dim; }
        }
        if (_rangeIndLabel != null && rangeIndicator != null) { _rangeIndLabel.text = rangeIndicator.visible ? "效果范围: 开" : "效果范围: 关"; _rangeIndLabel.color = rangeIndicator.visible ? DebugUI.Fg : DebugUI.Dim; }
    }

    /// <summary>物品页：选一个物品（武器/弹药/魔法/投掷物/血包）→ 刷到准星 / 随机刷出 / 直接给背包；外加刷敌人、商店补货、清场。</summary>
    void BuildPageItems(RectTransform content, int index)
    {
        var page = DebugUI.NewRect(content, "Page" + index, new Vector2(W, ContentH), Vector2.zero);
        _page[index] = page.gameObject;

        DebugUI.NewText(page, "LblItem", new Vector2(36f, 24f), new Vector2(0f, 0f), 12, DebugUI.Dim, TextAnchor.MiddleLeft).text = "物品";
        DebugUI.NewButton(page, "Prev", "◀", new Vector2(36f, 24f), new Vector2(40f, 0f), 13, DebugUI.Fg, () => StepItem(-1));
        _itemNameText = DebugUI.NewText(page, "ItemName", new Vector2(276f, 24f), new Vector2(82f, 0f), 12, DebugUI.Bright, TextAnchor.MiddleLeft);
        DebugUI.NewButton(page, "Next", "▶", new Vector2(36f, 24f), new Vector2(362f, 0f), 13, DebugUI.Fg, () => StepItem(+1));

        DebugUI.NewText(page, "LblCnt", new Vector2(36f, 24f), new Vector2(408f, 0f), 12, DebugUI.Dim, TextAnchor.MiddleLeft).text = "数量";
        DebugUI.NewButton(page, "CntD", "−", new Vector2(28f, 24f), new Vector2(448f, 0f), 13, DebugUI.Fg, () => StepItemCount(-5));
        _itemCountText = DebugUI.NewText(page, "CntV", new Vector2(48f, 24f), new Vector2(480f, 0f), 12, DebugUI.Bright, TextAnchor.MiddleCenter);
        DebugUI.NewButton(page, "CntU", "+", new Vector2(28f, 24f), new Vector2(532f, 0f), 13, DebugUI.Fg, () => StepItemCount(+5));

        // 第二行：刷出方式
        DebugUI.NewButton(page, "AtCross", "刷到准星", new Vector2(104f, 26f), new Vector2(0f, -32f), 13, DebugUI.Fg, OnSpawnPickupAtCrosshair);
        DebugUI.NewButton(page, "Random", "随机刷出", new Vector2(104f, 26f), new Vector2(110f, -32f), 13, DebugUI.Fg, OnSpawnPickupRandom);
        DebugUI.NewButton(page, "ToBag", "给到背包", new Vector2(104f, 26f), new Vector2(220f, -32f), 13, DebugUI.Fg, OnGiveItemToBackpack);
        DebugUI.NewButton(page, "SpawnEnemy", "刷个敌人", new Vector2(104f, 26f), new Vector2(330f, -32f), 13, DebugUI.Warn, OnSpawnEnemy);

        // 第三行：商店 / 清理
        DebugUI.NewButton(page, "ShopRestock", "商店立即补货", new Vector2(126f, 26f), new Vector2(0f, -64f), 13, DebugUI.Fg, OnShopRestock);
        DebugUI.NewButton(page, "ClearPickups", "清空地上拾取物", new Vector2(140f, 26f), new Vector2(132f, -64f), 13, DebugUI.Fg, OnClearPickups);

        DebugUI.NewText(page, "Hint", new Vector2(W - 16f, 18f), new Vector2(0f, -98f), 12, DebugUI.Dim, TextAnchor.MiddleLeft)
               .text = "「刷到准星」= 准星指到的地面点；「随机刷出」= 地图空位；敌人从波次配置的敌人池随机取。";
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
        // ⚠️ 初始文字必须与 `DebugHud.visible` 的默认值一致（2026-10-17 起**默认关**）；切到本页时 RefreshValues 还会再对齐一次
        _hudLabel = DebugUI.LabelOf(DebugUI.NewButton(page, "BtnHud", hud != null && hud.visible ? "常驻条: 开" : "常驻条: 关", new Vector2(110f, 26f), new Vector2(100f, -34f), 12, DebugUI.Fg, OnToggleHud));

        DebugUI.NewButton(page, "BtnClearCd", "清魔法冷却", new Vector2(104f, 26f), new Vector2(220f, -34f), 12, DebugUI.Fg, OnClearMagicCd);
        // ⚠️ 初始文字与 `_noMagicCd` 的默认值一致（默认关）；切到本页时 `RefreshValues` 还会再对齐一次
        _noMagicCdLabel = DebugUI.LabelOf(DebugUI.NewButton(page, "BtnNoCd", "魔法无冷却: 关", new Vector2(130f, 26f), new Vector2(340f, -34f), 12, DebugUI.Dim, OnToggleNoMagicCd));

        // ---- 相机手感微调（全是 CameraRig 的 public 字段；调"跟手/前探/瞄准收窄"用）----
        _camFollowVal = CamRow(page, "跟随平滑", -68f, 1f, 0.5f, 40f, d => { var c = DebugRead.Cam; if (c != null) c.followLerp += d; });
        _camLeadVal = CamRow(page, "前瞄比例", -96f, 0.02f, 0f, 0.5f, d => { var c = DebugRead.Cam; if (c != null) c.aimLeadRatio += d; });
        _camLeadMaxVal = CamRow(page, "前瞄上限", -124f, 0.5f, 0f, 10f, d => { var c = DebugRead.Cam; if (c != null) c.maxAimLead += d; });
        _camFovVal = CamRow(page, "瞄准收窄", -152f, 1f, 0f, 30f, d => { var c = DebugRead.Cam; if (c != null) c.aimFovNarrow += d; });
    }

    /// <summary>杂项页的一行相机参数：标签 + [−] + 当前值 + [+]。step = 每点一次的增量。</summary>
    Text CamRow(RectTransform page, string label, float y, float step, float min, float max, System.Action<float> apply)
    {
        DebugUI.NewText(page, "Lbl" + label, new Vector2(96f, 24f), new Vector2(0f, y), 12, DebugUI.Dim, TextAnchor.MiddleLeft).text = label;
        DebugUI.NewButton(page, "D" + label, "−", new Vector2(28f, 24f), new Vector2(100f, y), 13, DebugUI.Fg,
                          () => { apply(-step); ClampCam(min, max); RefreshCameraValues(); });
        var v = DebugUI.NewText(page, "V" + label, new Vector2(60f, 24f), new Vector2(132f, y), 12, DebugUI.Bright, TextAnchor.MiddleCenter);
        DebugUI.NewButton(page, "U" + label, "+", new Vector2(28f, 24f), new Vector2(196f, y), 13, DebugUI.Fg,
                          () => { apply(+step); ClampCam(min, max); RefreshCameraValues(); });
        return v;
    }

    /// <summary>相机四个字段统一夹到合法区间（避免调到负值 / 离谱值）。</summary>
    void ClampCam(float min, float max)
    {
        var c = DebugRead.Cam;
        if (c == null) return;
        c.followLerp = Mathf.Clamp(c.followLerp, 0.5f, 40f);
        c.aimLeadRatio = Mathf.Clamp(c.aimLeadRatio, 0f, 0.5f);
        c.maxAimLead = Mathf.Clamp(c.maxAimLead, 0f, 10f);
        c.aimFovNarrow = Mathf.Clamp(c.aimFovNarrow, 0f, 30f);
        c.aimLeadExtra = Mathf.Clamp(c.aimLeadExtra, 0f, 3f);
    }

    // 按钮尺寸（实例只读字段：不引入任何静态状态）
    readonly Vector2 _btn = new Vector2(190f, 26f);   // 战斗/波次页按钮
    readonly Vector2 _small = new Vector2(48f, 26f);  // 杂项页小按钮
}
#endif
