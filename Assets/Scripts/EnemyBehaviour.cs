using UnityEngine;

/// <summary>
/// 敌人行为组件基类（策略模式）。
/// 每种「攻击/行为方式」是一个继承本类的组件：近战、远程，以及未来的自爆/召唤/飞行…
/// 新增行为 = 写一个继承本类的脚本，敌人配置的「行为类型」下拉会自动出现它，
/// 不需要改 EnemyAI / WaveManager 的任何代码。
///
/// 本组件只负责「怎么打」；巡逻/索敌/追击/移动等通用逻辑由 EnemyAI 负责 ——
/// ⚠️ 它只在**追击状态**下、于 `FixedUpdate` 里调用本组件的 `Tick()`（**巡逻分支不调**，
///    所以别把"待机/巡逻时也该做的事"放进 Tick，那样永远不会执行）。
/// </summary>
[RequireComponent(typeof(EnemyAI))]
public abstract class EnemyBehaviour : MonoBehaviour
{
    /// <summary>宿主（本敌人身上的 EnemyAI）</summary>
    protected EnemyAI ai;

    /// <summary>敌弹的存活时间（秒）——**唯一实现**：射程上限 = 子弹速度 × 本值。
    /// ⚠️ 别在别处再写一遍这个数：`EnemyConfigEditor` 的自检要用它算「射程上限」，
    ///    两处各写一份的话，改了一处就会让告警说假话（§4 #15 那类"同一个量两处定义"的坑）。</summary>
    public const float EnemyBulletLife = 1.2f;

    /// <summary>生成敌人时由 WaveManager 调用：绑定宿主并触发子类初始化</summary>
    public void Init(EnemyAI ai)
    {
        this.ai = ai;
        OnInit();
    }

    /// <summary>子类初始化钩子（如远程兵在此准备武器与枪模型）</summary>
    protected virtual void OnInit() { }

    /// <summary>
    /// 构建枪模型（远程兵/狙击手共用）：优先敌人配置的 Prefab → 资源总表 → 代码生成的方块枪。
    /// localPos/localScale 可传参自定义代码生成枪的尺寸（如狙击手的长管）。
    /// </summary>
    protected GameObject BuildGunModel(Vector3? localPos = null, Vector3? localScale = null)
    {
        var assets = GameAssets.I;
        GameObject gun;
        GameObject gunPrefab = (ai.cfg != null && ai.cfg.gunPrefab != null)
            ? ai.cfg.gunPrefab
            : (assets != null ? assets.enemyGunPrefab : null);

        if (gunPrefab != null)
        {
            gun = Instantiate(gunPrefab, transform);
            gun.name = "Gun";
            return gun;
        }

        gun = Effects.CreateMeshObject(PrimitiveType.Cube, "Gun", transform,
                                       Effects.Shared(new Color(0.15f, 0.15f, 0.17f)));
        gun.transform.localPosition = localPos ?? new Vector3(0.22f, -0.12f, 0.45f);
        gun.transform.localScale = localScale ?? new Vector3(0.12f, 0.12f, 0.55f);
        return gun;
    }

    /// <summary>
    /// 克隆一份「按敌人配置改好数值」的远程武器（远程兵/狙击手共用）。
    /// 必须用副本：行为会改造武器数值，直接改资源本体会把 .asset 文件写坏。
    /// </summary>
    protected WeaponData CloneShootWeapon()
    {
        var cfg = ai.cfg;
        var baseW = (cfg != null && cfg.shooterWeapon != null) ? cfg.shooterWeapon : WeaponData.SMG();
        var w = baseW.Clone();
        if (cfg != null)
        {
            w.damage = cfg.ShooterDamageAt(GameState.Wave);
            w.fireInterval = cfg.fireInterval;
            w.bulletSpeed = cfg.bulletSpeed;
            w.hipSpread = cfg.spread;   // 敌人不瞄准，只吃腰射值（aimSpread 不参与）
            w.tracerColor = cfg.tracerColor;
            // 有效射程 / 超出射程伤害倍率：敌人侧独立配（玩家侧看武器自己的那份）。
            // 两者都走 `Combat.DamageAtDistance` 同一份实现 —— 敌人子弹和玩家子弹是同一条 `Bullet`。
            w.effectiveRange = cfg.effectiveRange;
            w.beyondRangeDamage = cfg.beyondRangeDamage;
            w.weaponName = cfg.displayName + "的枪";
            // 敌弹射程单独定：玩家子弹 bulletLife=0.6（提速配套），敌人若沿用则射程
            // = speed×0.6（远程兵仅 12m）会打不到玩家。敌弹统一 1.2s：射程 = speed×1.2
            // （远程兵 20→24m、狙击手 55→66m），恢复远程威胁。
            w.bulletLife = EnemyBulletLife;
        }
        return w;
    }

    /// <summary>
    /// 每帧行为主入口（追击状态下由 EnemyAI 驱动）。
    /// target：当前追击目标点（看见玩家=玩家位置，看不见=最后目击点）。
    /// seesPlayer：是否正看见玩家。
    /// </summary>
    public abstract void Tick(Vector3 target, bool seesPlayer);

    /// <summary>保持与玩家的距离（远程/狙击共用）：太远靠近，太近拉开</summary>
    protected void KeepDistance(Vector3 to, float dist)
    {
        if (dist > ai.keepDistance + 1f) ai.MoveTo(to.normalized, ai.chaseSpeed * 0.9f);
        else if (dist < ai.keepDistance - 2f) ai.MoveTo(-to.normalized, ai.chaseSpeed * 0.7f);
        else ai.StopMoving();
    }
}

/// <summary>行为的中文显示名（敌人配置的「行为类型」下拉框用）。不写则用类名去掉 Enemy/Behaviour 后的名字</summary>
[System.AttributeUsage(System.AttributeTargets.Class)]
public sealed class BehaviourName : System.Attribute
{
    public string Name;
    public BehaviourName(string name) { Name = name; }
}