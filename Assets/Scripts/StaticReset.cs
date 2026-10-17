using UnityEngine;

/// <summary>
/// 静态状态的统一清理入口。
///
/// 为什么需要：本项目的敌人/拾取物注册表、对象池、单例、缓存全部是 static 字段。
/// 正常模式下它们靠「脚本域重载（Reload Domain）」在每次进 Play 时被清空，
/// 但一旦为了加快迭代开启 Enter Play Mode Options → Reload Domain: false，
/// 这些静态状态会跨 Play 会话残留，表现为：
///   - 重开一局敌人数量翻倍（EnemyAI.All 里还留着已销毁的旧条目）
///   - 音效消失（Sfx._src 指向已销毁的 AudioSource）
///   - 单例指向已销毁对象、对象池弹出死引用
/// 这类问题极难排查（现象随机、断点错位），所以这里统一在「子系统注册阶段」复位，
/// 等价于一次域重载的效果。
///
/// 维护约定：**新增任何 static 字段/注册表/单例缓存，都要在下面登记清理**。
/// （仅作为「资源/模板」存在的静态缓存，如 Effects 的共享材质池、Sfx 的合成 AudioClip，
///   跨会话残留是无害且有益的——它们是纯资源、不持有场景对象，故不清。）
///
/// ⚠️ **防漏校验（2026-10-15 加）**：光靠"人记得登记"太脆弱，所以这里记下"本次复位动过哪些
///    `ResetStatics()`"（`_touched`），编辑器里再扫一遍程序集：**凡声明了 `ResetStatics()` 却没被登记的
///    类型都会打一条 Warning**。于是"新增 static 忘了登记"会当场被指出来，而不是等你哪天关掉
///    Reload Domain 才遇到"重开一局敌人翻倍"这种诡异 bug。（只在编辑器里跑，运行时零开销。）
/// </summary>
public static class StaticReset
{
    /// <summary>本次复位"处理过的类型"（只给编辑器校验用；每次复位开头清空，运行时不读）。</summary>
    static readonly System.Collections.Generic.HashSet<System.Type> _touched
        = new System.Collections.Generic.HashSet<System.Type>();

    /// <summary>复位某个类的静态状态，并**同时登记**。
    /// ⚠️ 一律用这个包一层（而不是直接写 `X.ResetStatics();`）—— 这样"忘了登记"这件事
    ///    在结构上就不可能发生（编辑器期校验见 <see cref="ValidateRegistrations"/>）。
    /// ⚠️ 第一个参数收 `System.Type` 而不是泛型 `T`：本项目不少类是 **static class**
    ///    （`UIUtil` / `Sfx` / `UIState` …），而 C# 禁止把静态类当泛型实参（CS0718）。
    ///    委托只在每次进 Play 时建 24 个，开销可忽略。</summary>
    static void ResetOf(System.Type owner, System.Action reset)
    {
        _touched.Add(owner);
        reset();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetAll()
    {
        _touched.Clear();

        // ---- 全局游戏状态（含 Paused / InteractConsumedFrame）----
        GameState.Reset();

        // ---- 静态注册表 ----
        // 敌人：存活注册表 `All` + 新增的致盲图标共享材质（2026-10-18）。
        // ⚠️ 走 `ResetOf`（`All.Clear()` 已搬进 `EnemyAI.ResetStatics`）—— 同 SmokeVolume / FireZone 的写法，
        //    否则"有 ResetStatics 却还在外面直接清"会被编辑器校验判成漏登记。
        ResetOf(typeof(EnemyAI), EnemyAI.ResetStatics);
        WeaponPickup.All.Clear();
        HealthPack.All.Clear();
        AmmoPickup.All.Clear();          // 弹药包注册表（2026-09-27）
        MagicPickup.All.Clear();         // 地上的魔法卡注册表（2026-09-28）
        ThrowablePickup.All.Clear();     // 地上的投掷物注册表（2026-10-12）
        // 烟云注册表（2026-10-13；漏清 → 重开一局残留死引用、且会"幽灵挡视线"）。
        // ⚠️ 走 `ResetStatics()`（实现就是 `All.Clear()`）而不是直接 `SmokeVolume.All.Clear()`：
        //    统一写法后，"登记"只剩一种形式，编辑器校验才不会把它当成漏登记（2026-10-15）。
        ResetOf(typeof(SmokeVolume), SmokeVolume.ResetStatics);
        // 火焰区域注册表（2026-10-16；与烟云同一个理由：漏清 → 重开一局残留死引用）
        ResetOf(typeof(FireZone), FireZone.ResetStatics);
        // 爆炸事件订阅链（2026-10-17；漏清 → 上一局已销毁的订阅者仍会被回调）
        ResetOf(typeof(Combat), Combat.ResetStatics);
        ResetOf(typeof(MagicMark), MagicMark.ResetStatics);          // 敌人身上的印记注册表（2026-09-28）
        ResetOf(typeof(HealthPack), HealthPack.ResetStatics);        // 静态事件 AnyConsumed（漏登记 → 上一局已销毁的 WaveManager 仍会被回调）
        ResetOf(typeof(SpawnPosition), SpawnPosition.ResetStatics);  // 地图边界（漏登记 → 上一局算出的边界残留到下一局）

        // ---- 对象池 ----
        ObjectPool.Clear();

        // ---- 单例与实例引用 ----
        PlayerController.Instance = null;
        CameraRig.Instance = null;
        HUD.Instance = null;
        Crosshair.InstanceUI = null;
        ResetOf(typeof(WaveManager), WaveManager.ResetStatics);      // WaveManager._instance + 内置波次配置缓存

        // ---- 各工具的静态缓存 ----
        ResetOf(typeof(GameAssets), GameAssets.ResetStatics);
        ResetOf(typeof(Sfx), Sfx.ResetStatics);
        ResetOf(typeof(DamageText), DamageText.ResetStatics);
        ResetOf(typeof(Crosshair), Crosshair.ResetStatics);
        ResetOf(typeof(BackpackPanel), BackpackPanel.ResetStatics);
        ResetOf(typeof(KillFeed), KillFeed.ResetStatics);
        ResetOf(typeof(VirtualCursor), VirtualCursor.ResetStatics);
        ResetOf(typeof(WeaponData), WeaponData.ResetStatics);
        ResetOf(typeof(ItemDatabase), ItemDatabase.ResetStatics);    // 物品清单（便利表）的 Resources 缓存
        ResetOf(typeof(PlayerMagic), PlayerMagic.ResetStatics);      // 魔法管理器单例（2026-09-28；漏了 → 重开一局残留上一局的装备与冷却）
        ResetOf(typeof(PlayerThrowable), PlayerThrowable.ResetStatics);  // 投掷物管理器单例（2026-10-12）
        ResetOf(typeof(UIState), UIState.ResetStatics);              // 界面总开关的计数（漏了 → 重开一局可能一进去就是暂停）
        ResetOf(typeof(InteractFocus), InteractFocus.ResetStatics);  // 交互焦点注册表（场景重载后里面会留死引用）
        ResetOf(typeof(Chest), Chest.ResetStatics);                  // 箱子注册表
        ResetOf(typeof(ChestPanel), ChestPanel.ResetStatics);        // 箱子面板单例（2026-09-26 补登记：以前只靠 OnDestroy 兜底）
        ResetOf(typeof(ItemVisuals), ItemVisuals.ResetStatics);      // 物品的占位图标（自己创建的运行时 Sprite）
        ResetOf(typeof(Shop), Shop.ResetStatics);                    // 商店注册表（2026-09-25）
        ResetOf(typeof(ShopPanel), ShopPanel.ResetStatics);          // 商店面板：单例 + Tab 帧号（漏了 → 重开一局可能残留"Tab 被占"）
        ResetOf(typeof(ShopInventoryPanel), ShopInventoryPanel.ResetStatics);  // 商店的左侧库存面板：单例

        CameraCache.Clear();

        // ---- UI 共用资源（全项目共用的那份动态字体）----
        ResetOf(typeof(UIUtil), UIUtil.ResetStatics);

        ValidateRegistrations();   // 编辑器期防漏校验（发布版整段被裁掉）
    }

    /// <summary>编辑器期防漏校验：扫本程序集里所有声明了**无参 `ResetStatics()`** 的类型，
    /// 凡不在 `_touched`（即上面没登记）的就告警 —— 把"新增 static 忘了登记"变成当场可见的 Warning，
    /// 而不是等你哪天关掉 Reload Domain 才撞上"重开一局数据翻倍"那类诡异 bug。
    /// ⚠️ 只告警、**不改任何运行时行为**；非编辑器（打包）时调用点被 Conditional 裁掉。</summary>
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    static void ValidateRegistrations()
    {
        var asm = typeof(StaticReset).Assembly;
        var types = asm.GetTypes();
        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i];
            var m = t.GetMethod("ResetStatics",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                                null, System.Type.EmptyTypes, null);
            if (m == null || m.ReturnType != typeof(void)) continue;   // 只认无参、返回 void 的
            if (_touched.Contains(t)) continue;
            Debug.LogWarning("[StaticReset] " + t.Name + ".ResetStatics() 存在但没登记 → 关掉 Reload Domain 时它的静态状态会跨 Play 会话残留。" +
                             "请在 StaticReset.ResetAll 里补：ResetOf(typeof(" + t.Name + "), " + t.Name + ".ResetStatics);");
        }
    }
}
