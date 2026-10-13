using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// **弹药等级（子弹档位）选择器**（挂 Player；与 `PlayerThrowable` 同一套做法：`PlayerController` 只留 T 入口，
/// 逻辑全在这里）。
///
/// 职责（2026-10-13 规划、落地）：
///   · **按 T 开关选择器**：开着 → 滚轮上下选档位、立即高亮；再按 T / 左键 → 提交换级（一次换弹）；
///   · **滚轮优先占滚轮**：开着时滚轮归本选择器（选档），关掉后滚轮恢复切武器槽 —— 与投掷物选择器同套路；
///   · **库存驱动**：列表 = 当前武器口径下、背包有货的 `AmmoData`（按 `tier` 升序）；没货的等级不显示；
///   · **提交 = 关闭时一次性换级**：弹药换级是一次换弹（耗时 `reloadTime`），不能每滚一下换一次，
///     所以滚轮只移动高亮、关闭选择器才真换（仿 `PlayerThrowable` 的"所见即所选"，但换成"关闭才生效"）。
///
/// 写回复用 `PlayerController.BeginAmmoGradeSwap`（原 `StartSwitchAmmo` 改造而来，认"选定的档"而非"自动下一档"），
/// 内部走 `CanSwapMag` 预检 + 原子换级（`FinishSwitchAmmo`），与旧单步 T 完全同一套底层。
/// </summary>
public class AmmoSelector : MonoBehaviour
{
    PlayerController _pc;
    PlayerBackpack _bp;

    bool _selectorOpen;
    float _selectorCloseAt;
    float _scrollAccum;                 // 选择器滚轮的"整格"累加器（与投掷物选择器同一套手感）

    readonly List<AmmoData> _owned = new List<AmmoData>(8);   // 本帧"该口径 + 有货"的弹药卡（按 tier 升序）
    AmmoData _selected;                 // 当前高亮的档（关闭时提交它）

    readonly AmmoSelectorUI _selector = new AmmoSelectorUI();  // 选择器纵列（自建画布）
    System.Func<AmmoData, int> _countOf;                       // 缓存委托：选择器每帧要数量（零分配）

    public bool SelectorOpen => _selectorOpen;

    void Awake()
    {
        _pc = GetComponent<PlayerController>();
        _bp = GetComponent<PlayerBackpack>();
        _countOf = CountOf;             // 传给选择器 UI（缓存委托 → 每帧零分配）
    }

    void OnDestroy()
    {
        if (_selector != null) _selector.Dispose();
    }

    // ==================== 每帧同步（库存驱动） ====================

    /// <summary>由 `PlayerController.Update` 每帧调用（与 `PlayerThrowable.Tick` 同位置）。</summary>
    public void Tick(float dt)
    {
        if (GameState.GameOver) { if (_selectorOpen) CloseSelector(false); return; }
        if (GameState.Paused)  { if (_selectorOpen) CloseSelector(false); return; }   // 暂停/死亡：取消、不换级

        if (_selectorOpen)
        {
            BuildOwned();
            // 当前高亮档若已无货（被别的系统用掉）→ 回退到第一个有效档
            if (_selected == null || !_owned.Contains(_selected))
                _selected = _owned.Count > 0 ? _owned[0] : null;
            if (Time.time >= _selectorCloseAt) CloseSelector(true);   // 超时：自动提交高亮档
        }
    }

    /// <summary>重扫"该口径 + 有货"的弹药卡（按 `tier` 升序 = 选择器顺序）。</summary>
    void BuildOwned()
    {
        _owned.Clear();
        if (_pc == null || _pc.weapon == null || _bp == null) return;
        var db = ItemDatabase.I;
        if (db == null || db.all == null) return;
        var kind = _pc.weapon.ammoKind;
        for (int i = 0; i < db.all.Count; i++)
        {
            var a = db.all[i] as AmmoData;
            if (a == null || a.ammoKind != kind || a.tier <= 0) continue;
            if (_bp.CountAmmo(a) <= 0) continue;     // 没货 → 不显示（与换级预检只在有货卡里循环一致）
            _owned.Add(a);
        }
        _owned.Sort((x, y) => x.tier.CompareTo(y.tier));
    }

    /// <summary>某个档位的持有数量（**派生值**：直接数库存，唯一实现是背包的 `CountAmmo`）。</summary>
    public int CountOf(AmmoData def) => (_bp != null && def != null) ? _bp.CountAmmo(def) : 0;

    // ==================== T 键入口 ====================

    /// <summary>T 键唯一入口：没开 → 开；开着 → 关闭并提交。守卫与旧 `StartSwitchAmmo` 一致
    /// （有武器、不在换弹、不在持弹模式、武器吃弹药）。</summary>
    public void OnKeyT()
    {
        if (_pc == null || _pc.weapon == null || _pc.reloading) return;
        if (_pc.throwables != null && _pc.throwables.Active) return;        // 持弹模式不抢 T
        if (_pc.weapon.ammoKind == AmmoKind.None)                           // RPG 那类不吃弹药
        {
            HUD.Announce("该武器不吃弹药", 1.0f, 26, new Color(0.9f, 0.85f, 0.5f));
            return;
        }
        if (_selectorOpen) CloseSelector(true);
        else OpenSelector();
    }

    // ==================== 开关 ====================

    void OpenSelector()
    {
        BuildOwned();
        if (_owned.Count == 0)
        {
            HUD.Announce("没有可切换的弹药", 1.0f, 26, new Color(0.9f, 0.7f, 0.4f));
            return;
        }
        // 默认高亮"当前弹匣那档"；若它已不在库存（刚好用光）则回退到第一档
        _selected = _pc.magAmmoId;
        if (_selected == null || !_owned.Contains(_selected)) _selected = _owned[0];
        _selectorOpen = true;
        _selectorCloseAt = Time.time + SelectorTimeout();
        _selector.Invalidate();     // 强制重建行（内容可能变了，但签名恰好没变）
        // ⚠️ 不直接显示：显示/隐藏统一由 `LateUpdate → _selector.Render(...)` 决定（暂停/死亡时不许显示只有一处判据）
    }

    /// <summary>关闭选择器。`commit == true` 且选了有效档 → 走 `PlayerController.BeginAmmoGradeSwap` 提交换级；
    /// `commit == false`（暂停/切枪/超时以外）→ 只收起、不换级。</summary>
    void CloseSelector(bool commit)
    {
        _selectorOpen = false;
        _selector.Hide();
        if (commit && _selected != null)
            _pc.BeginAmmoGradeSwap(_selected);
    }

    /// <summary>左键确认（在 `PlayerController.Update` 里、选择器开着时由左键触发）：提交并关闭。</summary>
    public void Confirm() { if (_selectorOpen) CloseSelector(true); }

    /// <summary>切武器槽时由 `PlayerController.SwitchSlot` 调：关掉选择器并取消（换级的目标口径会变，不能留半截）。</summary>
    public void OnWeaponSwitch() { if (_selectorOpen) CloseSelector(false); }

    // ==================== 滚轮 ====================

    /// <summary>选择器滚轮（由 `PlayerController` 的滚轮分发处调用）。返回 true = 本次滚轮已被选择器吃掉。</summary>
    public bool HandleScrollDelta(float dy)
    {
        if (!_selectorOpen) return false;
        _selectorCloseAt = Time.time + SelectorTimeout();   // 有操作就续命
        _scrollAccum += dy;
        bool moved = false;
        while (_scrollAccum >= 1f) { _scrollAccum -= 1f; StepSelection(+1); moved = true; }
        while (_scrollAccum <= -1f) { _scrollAccum += 1f; StepSelection(-1); moved = true; }
        return true;
    }

    /// <summary>滚轮选一项（高亮当前项；到头循环，关闭时才真正换级）。</summary>
    void StepSelection(int dir)
    {
        BuildOwned();
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

    // ==================== 渲染 ====================

    void LateUpdate()
    {
        // 显示与否只在这一处判（暂停/死亡/没武器 → 一律不显示）
        _selector.Render(_selectorOpen && _pc != null && _pc.weapon != null && !GameState.Paused && !GameState.GameOver,
                         _owned, _selected, _countOf);
    }
}
