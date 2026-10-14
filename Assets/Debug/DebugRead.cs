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

    /// <summary>魔法行：当前魔法 + 冷却剩余（只读）。</summary>
    public static string MagicValue()
    {
        var pm = PlayerMagic.Instance;
        if (pm == null) return "--";
        if (pm.Equipped == null) return "空槽";
        string name = pm.Equipped.DisplayName;
        float cd = pm.CooldownRemaining;
        return cd > 0.01f ? string.Format("{0}   冷却 {1:0.0}s", name, cd) : name + "   就绪";
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

    /// <summary>准星所指的地面点，取不到则回退到"玩家脚下"。
    ///
    /// ⚠️⚠️ 掩码必须用 **`Layers.Blocker`**（= Obstacle + Ground 的**掩码**）！
    ///    `Layers.Ground` / `Layers.Obstacle` 是**层索引**（11 / 12），直接当掩码传会被解释成
    ///    "layer 0/1/3"→ **静默打不中任何东西**（项目里 `MagicProjectile.cs:110` 明文记过这个坑，
    ///    我在 2026-10-14 又踩了一次，表现为"刷到准星/传送"全部无声失败）。
    /// ⚠️ 打到**墙面**（法线不朝上）时不算地面 → 回退到脚下，避免把东西塞进墙里。</summary>
    public static bool GroundPoint(out Vector3 point)
    {
        point = Vector3.zero;
        var pc = Pc;
        var cam = CameraCache.Main;
        if (pc == null || cam == null) return false;

        // 1) 准星方向（只认朝上的面）
        Ray ray = cam.ScreenPointToRay(pc.CrosshairScreenPos);
        if (Physics.Raycast(ray, out RaycastHit hit, 300f, Layers.Blocker, QueryTriggerInteraction.Ignore)
            && Vector3.Dot(hit.normal, Vector3.up) > 0.5f)
        {
            point = hit.point;
            return true;
        }

        // 2) 回退：玩家正上方往下打（Blocker 不含玩家层，不会打到自己）
        Vector3 above = pc.transform.position + Vector3.up * 30f;
        if (Physics.Raycast(above, Vector3.down, out hit, 80f, Layers.Blocker, QueryTriggerInteraction.Ignore))
        {
            point = hit.point;
            return true;
        }

        return false;
    }

    /// <summary>传送到"准星所指的地面点"。
    ///
    /// ⚠️ Y 必须落在**项目约定的实体高度平面**（`GameConst.EntityPlaneY = 0.55`；地面 y ≈ 0，见 `SmokeVolume.cs:64`），
    ///    **不能**直接把地面命中点当玩家坐标 —— 玩家枢轴在胶囊中心，直接落地就是"**陷进地板**"
    ///    （2026-10-14 用户实测：传送后 Y 变成 0.10，而正常站立是 0.55）。
    ///    抬高量取"命中点 + EntityPlaneY"，所以平地和箱顶/平台都成立。</summary>
    public static bool TeleportToCrosshair()
    {
        var pc = Pc;
        if (pc == null || !GroundPoint(out Vector3 p)) return false;

        pc.transform.position = new Vector3(p.x, p.y + GameConst.EntityPlaneY, p.z);
        var rb = pc.GetComponent<Rigidbody>();
        if (rb != null) rb.velocity = Vector3.zero;
        return true;
    }

    public static CameraRig Cam => CameraRig.Instance;

    /// <summary>一键杀死玩家（测死亡结算 / 重开清场是否干净）。</summary>
    public static string KillPlayer()
    {
        var h = PlayerHealth();
        if (h == null) return "找不到玩家血量组件";
        h.TakeDamage(99999f, Vector3.zero);
        return h.alive ? "玩家没死（是不是开着无敌？）" : "玩家已阵亡（可验证死亡结算与重开）";
    }

    // ==================== 刷东西（拾取物 / 敌人） ====================

    /// <summary>可刷到地上的物品清单（武器/弹药/魔法/投掷物 + 血包）。**由面板缓存**，不要每帧调用。</summary>
    public static List<ItemData> BuildSpawnableItems()
    {
        var list = new List<ItemData>();
        var db = ItemDatabase.I;
        if (db != null && db.all != null)
        {
            for (int i = 0; i < db.all.Count; i++)
            {
                var def = db.all[i];
                if (def == null) continue;
                if (def is WeaponData || def is AmmoData || def is MagicData || def is ThrowableData) list.Add(def);
            }
        }
        var med = PlayerBackpack.MedkitDef();
        if (med != null) list.Add(med);
        return list;
    }

    /// <summary>在指定位置刷出一个拾取物（按物品类型分发到各自的公开 <c>Create</c>）。</summary>
    public static string SpawnPickupAt(ItemData def, Vector3 pos, int count)
    {
        if (def == null) return "没有选中物品";

        if (def is WeaponData w)
            return WeaponPickup.Create(w, pos) != null ? "刷出武器 " + w.weaponName : "刷出失败（武器）";

        if (def is AmmoData a)
            return AmmoPickup.Create(a, pos, count) != null ? string.Format("刷出弹药 {0} ×{1}", a.DisplayName, count) : "刷出失败（弹药）";

        if (def is MagicData m)
            return MagicPickup.Create(m, pos) != null ? "刷出魔法 " + m.DisplayName : "刷出失败（魔法）";

        if (def is ThrowableData t)
            return ThrowablePickup.Create(t, pos, count) != null ? string.Format("刷出投掷物 {0} ×{1}", t.DisplayName, count) : "刷出失败（投掷物）";

        // 剩下的是血包（MedkitDef）：一包一个，多个就绕着落点散开，避免完全重叠
        int made = 0;
        int want = Mathf.Max(1, count);
        for (int i = 0; i < want; i++)
        {
            float ang = (i / (float)want) * Mathf.PI * 2f;
            Vector3 p = pos + new Vector3(Mathf.Cos(ang) * 0.4f, 0f, Mathf.Sin(ang) * 0.4f);
            if (HealthPack.Create(p) != null) made++;
        }
        return made > 0 ? string.Format("刷出血包 ×{0}", made) : "刷出失败（血包）";
    }

    /// <summary>刷在**准星地面点**（取不到就用玩家脚下）。
    /// ⚠️ y 用项目约定的实体平面 `GameConst.EntityPlaneY`（与 `SpawnPosition.FindOpenSpot` 一致；
    ///    拾取物的 `_baseY` 就是它）—— 直接用地面命中点会让物品/敌人**半埋进地里**。</summary>
    public static string SpawnPickupAtCrosshair(ItemData def, int count)
    {
        if (!GroundPoint(out Vector3 p)) return "准星与脚下都没打到地面（Blocker 层）";
        return SpawnPickupAt(def, new Vector3(p.x, GameConst.EntityPlaneY, p.z), count);
    }

    /// <summary>在地图上**随机空位**刷出（落点由 `SpawnPosition.FindOpenSpot` 决定：不会进墙，且 y 已是实体平面）。</summary>
    public static string SpawnPickupRandom(ItemData def, int count)
    {
        Vector3 p = SpawnPosition.FindOpenSpot(4f, SpawnPosition.RangeMedkit);
        return SpawnPickupAt(def, p, count);
    }

    /// <summary>把选中的物品直接塞进背包（不经过拾取流程）。</summary>
    public static string GiveToBackpack(ItemData def, int count)
    {
        var pc = Pc;
        var bp = pc != null ? pc.backpack : null;
        if (bp == null) return "背包不可用";
        if (def == null) return "没有选中物品";

        if (def is WeaponData w)
            return bp.AddWeaponItem(w, -1, null) > 0 ? "背包 +武器 " + w.weaponName : "背包放不下武器";

        int got = bp.AddItems(def, count);
        return got > 0 ? string.Format("背包 +{0} ×{1}", def.DisplayName, got) : "背包放不下";
    }

    /// <summary>在准星地面点刷出**一个敌人**（从波次配置的敌人池里随机取一种）。</summary>
    public static string SpawnEnemyAtCrosshair()
    {
        var wm = WaveManager.Instance;
        if (wm == null) return "找不到 WaveManager";
        if (!GroundPoint(out Vector3 p)) return "准星与脚下都没打到地面（Blocker 层）";

        EnemyConfig cfg = null;
        if (wm.enemyPool != null && wm.enemyPool.Length > 0)
        {
            var e = wm.enemyPool[UnityEngine.Random.Range(0, wm.enemyPool.Length)];
            if (e != null) cfg = e.config;
        }
        if (cfg == null) cfg = EnemyConfig.Melee();   // 池为空时的兜底

        // ⚠️ y 用实体平面（与 `SpawnPosition.FindOpenSpot` 一致）：敌人刚体锁了 Y，
        //    若按地面命中点生成会**卡在地板下方**（2026-10-14 同类问题一并修）。
        wm.SpawnEnemyAt(cfg, new Vector3(p.x, GameConst.EntityPlaneY, p.z));
        return "已刷出敌人" + (string.IsNullOrEmpty(cfg.displayName) ? "" : "：" + cfg.displayName);
    }

    /// <summary>在地图随机空位刷出 count 个敌人（位置互不重叠）。返回实际刷出数。</summary>
    public static int SpawnEnemiesAtRandomSpots(int count)
    {
        var wm = WaveManager.Instance;
        if (wm == null) return 0;
        int made = 0;
        for (int i = 0; i < count; i++)
        {
            EnemyConfig cfg = null;
            if (wm.enemyPool != null && wm.enemyPool.Length > 0)
            {
                var e = wm.enemyPool[UnityEngine.Random.Range(0, wm.enemyPool.Length)];
                if (e != null) cfg = e.config;
            }
            if (cfg == null) cfg = EnemyConfig.Melee();
            wm.SpawnEnemyAt(cfg, SpawnPosition.FindOpenSpot(4f, SpawnPosition.RangeEnemy));
            made++;
        }
        return made;
    }

    /// <summary>存活敌人的 EnemyAI 快照（**遍历前必须快照**：死亡会立刻从注册表移除）。</summary>
    public static List<EnemyAI> AliveEnemyAIs()
    {
        var outList = new List<EnemyAI>();
        for (int i = 0; i < EnemyAI.All.Count; i++)
        {
            var ai = EnemyAI.All[i];
            if (ai != null && ai.HealthRef != null && ai.HealthRef.alive) outList.Add(ai);
        }
        return outList;
    }

    /// <summary>冻结场上全部敌人的行动（**零核心改动**：关掉 AI 与行为组件的 `enabled`）。
    /// 血量组件不受影响 → 照常可以打伤害、在敌人列表看掉血。返回冻结数。
    /// ⚠️ 用法：由调试面板**每帧**调用（新刷出的敌人下一帧也会被冻住）；解除用 `UnfreezeEnemies`。</summary>
    public static int FreezeAllEnemies()
    {
        int n = 0;
        foreach (var ai in AliveEnemyAIs())
        {
            if (ai.enabled) ai.enabled = false;                                                // 停掉移动/索敌/攻击决策
            if (ai.behaviour != null && ai.behaviour.enabled) ai.behaviour.enabled = false;    // 停掉行为组件（远程开火等）
            var rb = ai.GetComponent<Rigidbody>();
            if (rb != null && rb.velocity.sqrMagnitude > 0.0001f) rb.velocity = Vector3.zero;  // 清残余速度
            n++;
        }
        return n;
    }

    /// <summary>解除冻结（只恢复**存活**敌人；死亡的本来就该保持 disabled）。返回恢复数。</summary>
    public static int UnfreezeEnemies()
    {
        int n = 0;
        for (int i = 0; i < EnemyAI.All.Count; i++)
        {
            var ai = EnemyAI.All[i];
            if (ai == null) continue;
            var h = ai.HealthRef;
            if (h == null || !h.alive) continue;    // 死亡的敌人本来就该是 disabled，别误开
            ai.enabled = true;
            if (ai.behaviour != null) ai.behaviour.enabled = true;
            n++;
        }
        return n;
    }

    // ==================== 商店 / 清理 ====================

    /// <summary>让场上所有商店**立即补货**（`Shop.ForceRestock`，README 登记表里的 `[DEBUG-ONLY]` 方法）。</summary>
    public static string ShopForceRestockAll()
    {
        var all = Shop.All;
        if (all == null || all.Count == 0) return "场上没有商店";
        int missBefore = 0, missAfter = 0, n = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var s = all[i];
            if (s == null) continue;
            missBefore += s.MissingToFull();
            s.ForceRestock();
            missAfter += s.MissingToFull();
            n++;
        }
        return string.Format("已让 {0} 家商店立即补货（缺货 {1} → {2} 件）", n, missBefore, missAfter);
    }

    /// <summary>清空地上全部拾取物（武器/弹药/魔法/投掷物/血包）。</summary>
    public static string ClearAllPickups()
    {
        int n = 0;
        n += DestroyAll(WeaponPickup.All);
        n += DestroyAll(AmmoPickup.All);
        n += DestroyAll(MagicPickup.All);
        n += DestroyAll(ThrowablePickup.All);
        n += DestroyAll(HealthPack.All);
        return n > 0 ? ("已清空地上 " + n + " 个拾取物") : "地上没有拾取物";
    }

    /// <summary>销毁一整张注册表里的物体。
    /// ⚠️ **必须先快照**：`Destroy` 会触发各对象的 `OnDestroy` 立刻把它们从注册表移除，
    ///    直接遍历原表会在中途被改（与 `EnemyAI.All` 同一个坑，2026-10-14 已踩过一次）。</summary>
    static int DestroyAll<T>(List<T> list) where T : Component
    {
        if (list == null) return 0;
        var snapshot = new List<T>(list);
        int n = 0;
        for (int i = 0; i < snapshot.Count; i++)
        {
            if (snapshot[i] == null) continue;
            UnityEngine.Object.Destroy(snapshot[i].gameObject);
            n++;
        }
        return n;
    }
}
#endif
