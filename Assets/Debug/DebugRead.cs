#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Text;
using Unity.Profiling;              // ProfilerRecorder
using UnityEngine;
using UnityEngine.Profiling;      // Profiler.GetTotalAllocatedMemoryLong

/// <summary>
/// 调试工具的**只读数据层 + 少量动作封装**：只读/只调游戏已有的 public 成员。
///
/// ⚠️ 三条纪律（详见 Assets/Debug/README.md）：
///   ① **单向依赖**：本类只读/只调游戏的 public 成员；**游戏代码永不引用 Debug 的任何类型**；
///   ② **零静态状态**：只有方法 + 一个 Unity 原生计数器句柄（见下），无业务字段；
///   ③ 取值只在**节流刷新**时发生，因此每次 GetComponent / 遍历都无需缓存。
///
/// 命名约定：`XxxValue()` = **纯值**（面板两列布局用，标签由 UI 单独画）；
///          `XxxLine()`  = **带标签的一整行**（常驻条用）。
/// </summary>
internal static class DebugRead
{
    public static PlayerController Pc => PlayerController.Instance;

    public static Health PlayerHealth()
    {
        var pc = Pc;
        return pc != null ? pc.GetComponent<Health>() : null;
    }

    // ==================== 纯值（面板信息区两列用） ====================

    public static string HpValue()
    {
        var h = PlayerHealth();
        if (h == null) return "--";
        int pct = h.maxHp > 0.01f ? Mathf.RoundToInt(h.hp / h.maxHp * 100f) : 0;
        string tag = h.alive ? (h.Invulnerable ? "  [无敌]" : "") : "  [死亡]";
        // 低血/危险用颜色区分（终端风：黄=警告、红=危险）
        string col = !h.alive ? "#FF7366" : (pct <= 30 ? "#FFD95A" : "#D1FFDA");
        return string.Format("<color={0}>{1:0}/{2:0} ({3}%)</color>{4}", col, h.hp, h.maxHp, pct, tag);
    }

    public static string SpeedValue()
    {
        var pc = Pc;
        if (pc == null) return "--";
        string ts = (Time.timeScale > 0.0001f && !Mathf.Approximately(Time.timeScale, 1f))
            ? string.Format("   时间x{0:0.0}", Time.timeScale) : "";
        return string.Format("{0:0.00}   (走 {1:0.0} / 跑 {2:0.0} / 瞄 {3:0.0}){4}",
                             pc.CurrentMoveSpeed, pc.walkSpeed, pc.runSpeed, pc.aimSpeed, ts);
    }

    public static string AmmoValue()
    {
        var pc = Pc;
        if (pc == null) return "--";
        if (pc.weapon == null) return "空手";

        string name = pc.weapon.weaponName;
        string mag = pc.magAmmo + "/" + pc.weapon.magSize;
        if (pc.weapon.ammoKind == AmmoKind.None) return name + "   " + mag + "   备弹 ∞";

        var card = pc.magAmmoId != null
            ? pc.magAmmoId
            : (pc.backpack != null ? pc.backpack.LowestOwnedAmmo(pc.weapon.ammoKind) : null);
        if (card == null) return name + "   " + mag + "   " + pc.weapon.ammoKind + " 备弹 0";
        int reserve = pc.backpack != null ? pc.backpack.CountAmmo(card) : 0;
        return name + "   " + mag + "   " + pc.weapon.ammoKind + " " + card.TierMark + " 备弹 " + reserve;
    }

    public static string RunValue()
    {
        string clear = "";
        var wm = WaveManager.Instance;
        if (wm != null && wm.ClearingCountdown > 0f) clear = string.Format("   下一波 {0:0.0}s", wm.ClearingCountdown);
        return string.Format("波次 {0}   击杀 {1}   敌人 {2}   金币 {3}{4}",
                             GameState.Wave, GameState.Kills, GameState.AliveEnemies, GameState.Gold, clear);
    }

    // ==================== 带标签的一整行（常驻条用） ====================

    public static string HpLine() => "HP " + HpValue();
    public static string SpeedLine() => "移速 " + SpeedValue();
    public static string AmmoLine() => "武器 " + AmmoValue();
    public static string RunLine() => RunValue();

    /// <summary>内存 / GC 行（常驻条第 5 行）。</summary>
    public static string MemLine()
    {
        long total = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
        long mono = GC.GetTotalMemory(false) / (1024 * 1024);
        return string.Format("内存 {0} MB (托管 {1} MB)   GC/帧 {2}", total, mono, GcPerFrame());
    }

    static string GcPerFrame()
    {
        if (!_gcPerFrame.Valid) return "-";
        double kb = _gcPerFrame.LastValue / 1024.0;
        return kb >= 1024.0 ? (kb / 1024.0).ToString("0.00") + " MB" : kb.ToString("0.0") + " KB";
    }

    // ⚠️ 本文件唯一的静态字段：Unity **原生计数器句柄**（进程级共享，不能放实例；也不参与"重开一局"状态）。
    static ProfilerRecorder _gcPerFrame;
    static bool _gcStarted;

    public static void EnsureGcRecorder()
    {
        if (_gcStarted) return;
        _gcStarted = true;
        _gcPerFrame = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
    }

    public static void DisposeGcRecorder()
    {
        if (_gcPerFrame.Valid) _gcPerFrame.Dispose();
        _gcStarted = false;
    }

    // ==================== 多行文本（敌人页） ====================

    /// <summary>敌人列表（AI 状态）：名字 / 行为组件 / 血量 / 是否交战 / 与玩家距离。最多 max 个。</summary>
    public static string EnemyLines(int max)
    {
        var list = EnemyAI.All;
        if (list == null || list.Count == 0) return "（场上无敌人）";

        var pc = Pc;
        Vector3 p = pc != null ? pc.transform.position : Vector3.zero;

        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < list.Count && shown < max; i++)
        {
            var ai = list[i];
            if (ai == null) continue;   // 列表里可能有"假 null"（见 WaveManager 注释）
            var hp = ai.HealthRef;
            if (hp == null) continue;

            string name = ai.cfg != null && !string.IsNullOrEmpty(ai.cfg.displayName) ? ai.cfg.displayName : ai.name;
            string beh = ai.behaviour != null ? ai.behaviour.GetType().Name : "无行为";
            float dist = pc != null ? Vector3.Distance(p, ai.transform.position) : 0f;
            string state = ai.InCombat ? "<color=#FFD95A>交战</color>" : "<color=#4E8F60>巡逻</color>";

            sb.Append(name).Append("  ").Append(beh)
              .Append("  HP ").Append(Mathf.Max(0f, hp.hp).ToString("0"))
              .Append("  ").Append(state)
              .Append("  ").Append(dist.ToString("0.0")).Append("m");
            if (shown < max - 1) sb.Append('\n');
            shown++;
        }
        if (list.Count > shown) sb.Append("\n…还有 ").Append(list.Count - shown).Append(" 个");
        return shown == 0 ? "（场上无敌人）" : sb.ToString();
    }

    // ==================== 动作（全部走游戏已有的 public 入口） ====================

    public static void FullHeal()
    {
        var h = PlayerHealth();
        if (h != null) h.Heal(h.maxHp);
    }

    public static void Damage(float amount)
    {
        var h = PlayerHealth();
        if (h != null) h.TakeDamage(amount, Vector3.zero);
    }

    public static void AddGold(int amount) => GameState.Gold += amount;

    /// <summary>精确无敌开关（`Health.SetInvulnerable`，README 登记表里的 `[DEBUG-ONLY]` 方法）。</summary>
    public static bool ToggleInvulnerable()
    {
        var h = PlayerHealth();
        if (h == null) return false;
        h.SetInvulnerable(!h.Invulnerable);
        return h.Invulnerable;
    }

    public static bool PlayerInvulnerable()
    {
        var h = PlayerHealth();
        return h != null && h.Invulnerable;
    }

    /// <summary>把背包塞满调试物资：每种武器 ×1、每种弹药 ×N、血包 ×2（走 `PlayerBackpack` 的 public 方法）。</summary>
    public static string GrantAllItems(int ammoPerKind)
    {
        var pc = Pc;
        var bp = pc != null ? pc.backpack : null;
        var db = ItemDatabase.I;
        if (bp == null || db == null || db.all == null) return "失败：背包或物品库不可用";

        int weapons = 0, ammoKinds = 0, ammoCount = 0, medkits = 0;
        foreach (var def in db.all)
        {
            if (def == null) continue;
            var ammo = def as AmmoData;
            if (ammo != null) { ammoCount += bp.AddItems(ammo, ammoPerKind); ammoKinds++; continue; }
            var w = def as WeaponData;
            if (w != null) { weapons += bp.AddWeaponItem(w, -1, null); continue; }
        }
        var med = PlayerBackpack.MedkitDef();
        if (med != null) medkits = bp.AddItems(med, 2);

        return string.Format("给了 武器{0}把 弹药{1}种/{2}发 血包{3}个（放不下的没进背包）",
                             weapons, ammoKinds, ammoCount, medkits);
    }

    /// <summary>秒杀场上全部敌人（走 `Health.TakeDamage`，零核心改动）。返回击杀数。</summary>
    public static int KillAllEnemies()
    {
        var list = EnemyAI.All;
        if (list == null) return 0;

        // ⚠️ **必须先快照**：敌人死亡时 `EnemyAI.OnDead()` 会立刻 `All.Remove(this)`（EnemyAI.cs:268），
        //    直接遍历原列表会在中途被改 → `InvalidOperationException` 中断循环
        //    （2026-10-14 实测：6 个敌人只杀掉 3 个）。
        var snapshot = new List<EnemyAI>(list);

        int n = 0;
        for (int i = 0; i < snapshot.Count; i++)
        {
            var ai = snapshot[i];
            if (ai == null) continue;
            var hp = ai.HealthRef;
            if (hp == null || !hp.alive) continue;
            hp.TakeDamage(99999f, Vector3.zero);
            n++;
        }
        return n;
    }

    /// <summary>跳过"清场倒计时"直接进入下一波（`WaveManager.ForceNextWave`，README 登记表里的 `[DEBUG-ONLY]` 方法）。</summary>
    public static string SkipClearingDelay()
    {
        var wm = WaveManager.Instance;
        if (wm == null) return "失败：找不到 WaveManager";
        if (!wm.ForceNextWave())
            return wm.IsClearing ? "失败：当前不能跳波" : "不在清场倒计时阶段（先点「秒杀全场敌人」再关面板）";
        return "已跳过倒计时，下一波马上开始";
    }

    /// <summary>时间缩放**只能在游戏运行时写**：`GameState.ApplyPause` 会在"关面板"时把
    /// `Time.timeScale` 强制回 1（`GameState.cs:54`），且面板打开时它是 0（暂停语义）。
    /// 所以"想要的值"由面板自己存着，**面板关闭后**再由它写回来。</summary>
    public static void ApplyTimeScaleIfRunning(float v)
    {
        if (GameState.Paused || GameState.GameOver) return;
        v = Mathf.Max(0.01f, v);
        if (Mathf.Approximately(Time.timeScale, v)) return;   // 值相同就不写（避免每帧冗余写入）
        Time.timeScale = v;
    }

    /// <summary>一键截图到 `Captures/`（该目录已在 .gitignore 里）。</summary>
    public static string CaptureScreenshot()
    {
        try
        {
            const string dir = "Captures";
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            string file = dir + "/shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            ScreenCapture.CaptureScreenshot(file);
            return "已截图 " + file;
        }
        catch (Exception ex)
        {
            return "截图失败：" + ex.Message;
        }
    }

    /// <summary>传送到"准星所指的地面点"（显式传 LayerMask + QueryTriggerInteraction，见 Layers.cs）。</summary>
    public static bool TeleportToCrosshair()
    {
        var pc = Pc;
        var cam = CameraCache.Main;
        if (pc == null || cam == null) return false;

        Ray ray = cam.ScreenPointToRay(pc.CrosshairScreenPos);
        if (!Physics.Raycast(ray, out RaycastHit hit, 300f, Layers.Ground, QueryTriggerInteraction.Ignore))
            return false;

        pc.transform.position = hit.point + Vector3.up * 0.1f;
        var rb = pc.GetComponent<Rigidbody>();
        if (rb != null) rb.velocity = Vector3.zero;
        return true;
    }
}
#endif
