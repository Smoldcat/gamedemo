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
/// </summary>
public static class StaticReset
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetAll()
    {
        // ---- 全局游戏状态（含 Paused / PickupEConsumedFrame）----
        GameState.Reset();

        // ---- 静态注册表 ----
        EnemyAI.All.Clear();
        WeaponPickup.All.Clear();
        HealthPack.All.Clear();
        HealthPack.ResetStatics();      // 静态事件 AnyConsumed（漏登记 → 上一局已销毁的 WaveManager 仍会被回调）
        SpawnPosition.ResetStatics();   // 地图边界（漏登记 → 上一局算出的边界残留到下一局）

        // ---- 对象池 ----
        ObjectPool.Clear();

        // ---- 单例与实例引用 ----
        PlayerController.Instance = null;
        CameraRig.Instance = null;
        HUD.Instance = null;
        Crosshair.InstanceUI = null;
        WaveManager.ResetStatics();   // WaveManager._instance + 内置波次配置缓存

        // ---- 各工具的静态缓存 ----
        GameAssets.ResetStatics();
        Sfx.ResetStatics();
        DamageText.ResetStatics();
        Crosshair.ResetStatics();
        BackpackPanel.ResetStatics();
        KillFeed.ResetStatics();
        VirtualCursor.ResetStatics();
        WeaponData.ResetStatics();

        CameraCache.Clear();

        // ---- UI 共用资源（全项目共用的那份动态字体）----
        UIUtil.ResetStatics();
    }
}
