using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// **投掷物管理器**（挂 Player；与 `PlayerMagic` 同一套做法：`PlayerController` 只留按键入口，逻辑全在这里）。
///
/// 职责：
///   · **持弹模式**（G 键进出）：手里拿投掷物（枪收起），右键 = 弹道预览、左键 = 投出；
///   · **选择器**（已在 G 槽上再按一次 G）：滚轮上下选种类、立即生效、到头循环；
///   · **自动递补**：扔完当前种类还有别的 → 自动换列表里的下一个；全用光 → 自动切回"进入 G 之前的那把武器"；
///   · **库存驱动**（每帧比对）：手里拿的 / 有几颗，永远和背包里真实存在的投掷物一致（卖 / 丢 / 用光都自动收敛）。
///
/// 设计口径（用户 2026-10-12 逐条敲定，详见交接文档「最近变更」）：
///   · 落点 = **准星地面点**（超最大距离夹取）；出手点 = 手部；左键按下立即出手；
///   · 预览 = **白点（原抛物线）→ 撞点之后沿原轨迹标红**；落点标记只在白线真正到达时画；
///   · 预览与真实飞行**共用 `ThrowableBallistics`**（唯一实现：所见即所得）。
///
/// ⚠️ 单例：新增 static → 已在 `StaticReset` 登记（`ResetStatics`）。
/// </summary>
[DefaultExecutionOrder(130)]   // 排在 CameraRig(0) / 视觉系统(120) 之后：预览用的是"本帧最终"的相机
public class PlayerThrowable : MonoBehaviour
{
    public static PlayerThrowable Instance;

    // ---------------- 运行时状态 ----------------
    PlayerController _pc;
    PlayerBackpack _bp;

    bool _active;                    // 是否处于"持弹模式"
    ThrowableData _selected;         // 当前拿的种类（库存驱动：没货了就自动递补 / 退出）

    bool _selectorOpen;
    float _selectorCloseAt;
    float _scrollAccum;              // 选择器滚轮的"整格"累加器（与切枪滚轮同一套手感）

    readonly List<ThrowableData> _owned = new List<ThrowableData>(8);   // 本帧"库存 > 0"的种类（按名字排序）

    // ---------------- 渲染 / UI（2026-10-12 重构：按内聚块拆出去；本类只管状态机 + 输入） ----------------
    Camera _cam;
    readonly ThrowableTrajectoryView _view = new ThrowableTrajectoryView();   // 弹道点列 / 落点标记 / 范围圈
    readonly ThrowableSelectorUI _selector = new ThrowableSelectorUI();       // 选择器纵列（自建画布）
    System.Func<ThrowableData, int> _countOf;                                 // 缓存委托：选择器每帧要数量（零分配）

    public bool Active => _active;
    public ThrowableData Selected => _selected;
    public bool SelectorOpen => _selectorOpen;

    void Awake()
    {
        // 单例防护（写法同 PlayerMagic；⚠️ 只销毁多余的**组件**，不销毁整个玩家物体）
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        _pc = GetComponent<PlayerController>();
        _bp = GetComponent<PlayerBackpack>();

        _countOf = CountOf;                               // 传给选择器 UI 用（缓存委托 → 每帧零分配）
        _view.Build();                                    // 预览用的小资源（网格 / 贴图 / 材质）
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        _view.Dispose();
        _selector.Dispose();
    }

    /// <summary>登记到 `StaticReset`（单例）。</summary>
    public static void ResetStatics() => Instance = null;

    // ==================== 每帧同步（库存驱动） ====================

    /// <summary>由 `PlayerController.Update` 每帧调用。</summary>
    public void Tick(float dt)
    {
        if (GameState.GameOver)
        {
            if (_selectorOpen) CloseSelector();
            if (_active) ExitMode();
            return;
        }
        if (GameState.Paused)                        // 开背包 / 结算：关选择器（模式保留，关掉后按库存自动收敛）
        {
            if (_selectorOpen) CloseSelector();
            return;
        }

        RefreshOwned();

        if (_selected == null || CountOf(_selected) <= 0)
        {
            if (_active) EnsureSelection();           // 模式里：递补下一个（全空 → 退出模式、自动切回枪）
            else _selected = _owned.Count > 0 ? _owned[0] : null;   // 没在模式：只把"选中项"修正到有效（G 格显示靠它）
        }

        if (_selectorOpen && Time.time >= _selectorCloseAt) CloseSelector();
    }

    /// <summary>重扫"库存 > 0"的投掷物种类（按显示名排序 = 选择器顺序 = 自动递补顺序）。</summary>
    void RefreshOwned()
    {
        _owned.Clear();
        var db = ItemDatabase.I;
        if (db == null || db.all == null || _bp == null) return;
        for (int i = 0; i < db.all.Count; i++)
        {
            var t = db.all[i] as ThrowableData;
            if (t == null) continue;
            if (_bp.CountItems(t) <= 0) continue;
            _owned.Add(t);
        }
        _owned.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
    }

    /// <summary>某个种类的持有数量（**派生值**：直接数库存，唯一实现是背包的 `CountItems`）。</summary>
    public int CountOf(ThrowableData def) => (_bp != null && def != null) ? _bp.CountItems(def) : 0;

    // ==================== 进出持弹模式 ====================

    /// <summary>G 键入口（唯一）：不在模式里 → 进入；已在模式里 → 开关选择器。</summary>
    public void OnKeyG()
    {
        if (!_active)
        {
            RefreshOwned();
            if (_owned.Count == 0)
            {
                HUD.Announce("没有投掷物", 1.0f, 26, new Color(0.9f, 0.7f, 0.4f));
                return;
            }
            _active = true;
            if (_selected == null || CountOf(_selected) <= 0) _selected = _owned[0];
            RefreshVisual();
            return;
        }

        if (_selectorOpen) CloseSelector();
        else OpenSelector();
    }

    /// <summary>退出持弹模式（切武器 / 用光 / 死亡都走它）—— 枪切回来，并要求重新按右键才算瞄准（换手 = 重新举枪）。</summary>
    public void ExitMode()
    {
        if (!_active) return;
        _active = false;
        CloseSelector();
        if (_pc != null) _pc.RequireAimRepress();
        RefreshVisual();
    }

    /// <summary>玩家切武器槽时由 `PlayerController.SwitchSlot` 调（切到枪 = 退出投掷模式）。</summary>
    public void OnWeaponSwitch() => ExitMode();

    /// <summary>选中的种类**没货了**才递补：换列表里的下一个（到底绕回第一个）；全空则退出模式。
    /// ⚠️ **幂等**（2026-10-12 修 bug）：当前种类还有货 → 什么都不做 —— 所以"扔出一颗之后"可以无脑调它。
    ///    原来漏了这个守卫，导致手里明明还剩手雷、却因为名字排序被换成下一种（实测复现过）。</summary>
    void EnsureSelection()
    {
        if (_selected != null && CountOf(_selected) > 0) { RefreshOwned(); return; }   // 还有货 → 不动
        var gone = _selected;
        RefreshOwned();
        if (_owned.Count == 0) { _selected = null; ExitMode(); return; }

        ThrowableData next = _owned[0];
        if (gone != null)
        {
            // 找"排序在它后面"的第一个（绕回第一个）—— 与选择器列表顺序一致，玩家可预期
            for (int i = 0; i < _owned.Count; i++)
            {
                if (string.CompareOrdinal(_owned[i].DisplayName, gone.DisplayName) > 0) { next = _owned[i]; break; }
            }
        }
        _selected = next;
        RefreshVisual();
    }

    /// <summary>手里的模型 / HUD 状态变了：让 `PlayerController` 重刷手上外观（枪 ↔ 投掷物）。</summary>
    void RefreshVisual()
    {
        if (_pc != null) _pc.RefreshGunVisual();
    }

    // ==================== 选择器 ====================

    void OpenSelector()
    {
        _selectorOpen = true;
        _selectorCloseAt = Time.time + SelectorTimeout();
        _selector.Invalidate();     // 强制重建行（内容可能变了，但签名恰好没变）
        // ⚠️ 这里**不直接显示**：显示 / 隐藏统一由 `LateUpdate → _selector.Render(...)` 决定
        //    → "暂停 / 死亡时不许显示"只有一处判据（原来漏了，列表会留在屏幕上）
    }

    void CloseSelector()
    {
        _selectorOpen = false;
        _selector.Hide();
    }

    /// <summary>选择器滚轮（由 `PlayerController` 的滚轮分发处调用）。返回 true = 本次滚轮已被选择器吃掉。</summary>
    public bool HandleScrollDelta(float dy)
    {
        if (!_selectorOpen) return false;

        _selectorCloseAt = Time.time + SelectorTimeout();   // 有操作就续命
        _scrollAccum += dy;
        bool moved = false;
        while (_scrollAccum >= 1f) { _scrollAccum -= 1f; StepSelection(+1); moved = true; }
        while (_scrollAccum <= -1f) { _scrollAccum += 1f; StepSelection(-1); moved = true; }
        if (moved) RefreshVisual();
        return true;
    }

    /// <summary>滚轮选一项（立即生效：手里拿的直接换过去；到头循环）。</summary>
    void StepSelection(int dir)
    {
        RefreshOwned();
        if (_owned.Count == 0) return;
        int idx = _selected != null ? _owned.IndexOf(_selected) : -1;
        idx = idx < 0 ? 0 : (idx + dir + _owned.Count) % _owned.Count;
        _selected = _owned[idx];
    }

    static float SelectorTimeout()
    {
        var a = GameAssets.I;
        float t = a != null ? a.throwableSelectorTimeout : 1.5f;
        return t > 0.1f ? t : 0.1f;
    }

    // ==================== 投掷 ====================

    /// <summary>左键入口（持弹模式下由 `PlayerController` 调用）：按下即投出。</summary>
    public void HandlePrimaryInput()
    {
        if (!Input.GetMouseButtonDown(0)) return;
        if (_selected == null || CountOf(_selected) <= 0) { EnsureSelection(); return; }

        Vector3 origin, target;
        ComputeThrow(out origin, out target);

        ThrownGrenade.Spawn(_selected, origin, target, transform.forward);

        if (_bp != null) _bp.TakeItems(_selected, 1);
        Sfx.Pickup();                 // 暂时复用拾取音当"出手"反馈（正式音效素材就绪后换）

        if (_selectorOpen) CloseSelector();

        // 扔完立刻结算"还有没有货"：有别的 → 自动递补；全没了 → 自动切回枪（库存驱动）
        EnsureSelection();
    }

    /// <summary>算出出手点与落点（落点 = 准星地面点，按"离玩家"的水平距离夹到 [MinDistance, maxThrow]）。</summary>
    void ComputeThrow(out Vector3 origin, out Vector3 target)
    {
        Vector3 p = transform.position;
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        fwd.Normalize();

        origin = p + Vector3.up * ThrowableBallistics.HandHeight + fwd * ThrowableBallistics.HandForward;

        // 准星 → 地面点（射线与落点平面求交；瞄向天空 / 地平线时回落到"正前方最大距离"）
        Vector3 hit = p + fwd * _selected.maxThrowDistance; hit.y = ThrowableBallistics.LandY;
        var cam = _cam != null ? _cam : CameraCache.Main;
        if (cam != null && _pc != null)
        {
            Ray ray = cam.ScreenPointToRay(_pc.CrosshairScreenPos);
            if (ray.direction.y < -0.0001f)
            {
                float t = (ThrowableBallistics.LandY - ray.origin.y) / ray.direction.y;
                if (t > 0f) hit = ray.origin + ray.direction * t;
            }
        }

        // 夹取：按"离玩家"的水平距离（好理解：我能扔 15 米，不是"从手上算 15 米"）
        Vector3 flat = hit - p; flat.y = 0f;
        float d = flat.magnitude;
        Vector3 dir = d > 0.0001f ? flat / d : fwd;
        float maxD = Mathf.Max(_selected.maxThrowDistance, ThrowableBallistics.MinDistance);
        d = Mathf.Clamp(d, ThrowableBallistics.MinDistance, maxD);
        target = p + dir * d; target.y = ThrowableBallistics.LandY;
    }

    // ==================== 轨迹预览（按住右键） ====================

    void LateUpdate()
    {
        // 选择器：显示与否**只在这一处判**（暂停 / 死亡 / 退出模式 → 一律不显示）
        // ⚠️ 2026-10-12 修 bug：原来"关闭选择器"只发生在 `Tick` 里，而暂停时 `PlayerController.Update`
        //    是早退的 → Tick 根本不跑 → 列表会一直留在屏幕上。现在这里多了"状态 × 可见性"的绑定。
        _selector.Render(_selectorOpen && _active && !GameState.Paused && !GameState.GameOver,
                         _owned, _selected, _countOf);

        bool show = _active && _selected != null && CountOf(_selected) > 0
                    && Input.GetMouseButton(1) && !GameState.Paused && !GameState.GameOver;
        if (!show) return;

        Vector3 origin, target;
        ComputeThrow(out origin, out target);
        DrawTrajectory(origin, target);
    }

    /// <summary>白点 = 原抛物线；撞点之后沿原轨迹标红；白线真正到达落点时才画落点圈。
    /// 渲染动作全部委托给 `ThrowableTrajectoryView`（本方法只管"哪一段白的、哪一段红的、画不画落点圈"）。</summary>
    void DrawTrajectory(Vector3 origin, Vector3 target)
    {
        var cam = _cam != null ? _cam : CameraCache.Main;
        if (cam == null || !_view.Ready) return;
        Quaternion face = cam.transform.rotation;

        // 范围指示圈：效果卡勾了「显示范围指示圈」才画（画在同一个落点上、渲染队列在弹道点之下）
        var eff = _selected != null ? _selected.effect : null;
        if (eff != null && eff.showRangeIndicator) _view.DrawRangeIndicator(target, eff.DangerRadius, cam);

        var solve = ThrowableBallistics.SolveThrow(origin, target, _selected.peakHeight, _selected.throwSpeed, transform.forward);

        // ---- 前半段：**与真飞行同一步进 / 同一掩码**地模拟，撞上就停 ----
        Vector3 pos = origin, vel = solve.v0;
        float acc = ThrowableTrajectoryView.Spacing;   // 进位初始化 = 一个点距 → 弧线起点（手部）就有第一个点
        bool impacted = false;
        Vector3 impactPos = Vector3.zero, impactVel = Vector3.zero;

        int maxSteps = Mathf.CeilToInt(ThrowableBallistics.MaxPreviewTime / ThrowableBallistics.SimDt);
        for (int i = 0; i < maxSteps; i++)
        {
            Vector3 np, nv; RaycastHit hit;
            bool collided = ThrowableBallistics.Step(pos, vel, solve.gravity, ThrowableBallistics.SimDt,
                                                     Layers.Blocker, out np, out nv, out hit);
            if (collided) { impactPos = np; impactVel = nv; impacted = true; break; }

            _view.DrawDots(pos, np, face, false, cam, ref acc);
            pos = np; vel = nv;
        }

        if (!impacted)
        {
            _view.DrawDots(pos, target, face, false, cam, ref acc);   // 收尾到落点
            _view.DrawLandingMarker(target, cam);                     // 白线到达 → 画落点圈
            return;
        }

        // ---- 后半段：撞点之后**沿原轨迹**（忽略碰撞）继续到地面，全部标红 ----
        acc = ThrowableTrajectoryView.Spacing;    // 撞点处补一个红点（白线在这里截止，红段从这里开始）
        pos = impactPos; vel = impactVel;
        Vector3 g = Vector3.down * solve.gravity;
        float dt = ThrowableBallistics.SimDt;
        for (int i = 0; i < maxSteps; i++)
        {
            Vector3 np = pos + vel * dt + g * (0.5f * dt * dt);
            vel += g * dt;
            _view.DrawDots(pos, np, face, true, cam, ref acc);
            pos = np;
            if (pos.y <= ThrowableBallistics.LandY) break;               // 原轨迹到地面为止
        }
    }

}
