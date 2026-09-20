using UnityEngine;

/// <summary>
/// 远程行为：与玩家保持距离进行三点射。
/// 枪模型与武器数值在初始化时准备（数值取自敌人配置，武器用配置基枪克隆或默认冲锋枪，
/// 绝不改资源本体）；死亡掉枪时由 WaveManager 拿 shootWeapon 生成掉落物。
/// </summary>
[BehaviourName("远程")]
public class EnemyShooterBehaviour : EnemyBehaviour
{
    public WeaponData shootWeapon;

    float _nextBurst, _nextBurstShot;
    int _burstLeft;

    protected override void OnInit()
    {
        // 枪模型与武器（与狙击手共用基类的构建逻辑）
        BuildGunModel();
        shootWeapon = CloneShootWeapon();
        ai.shootWeapon = shootWeapon;
    }

    public override void Tick(Vector3 target, bool seesPlayer)
    {
        Vector3 to = target - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        ai.FaceTo(to);

        if (!seesPlayer)
        {
            ai.MoveTo(to.normalized, ai.chaseSpeed);   // 追到大概位置
            return;
        }

        // 保持距离：太远靠近，太近拉开（基类共用逻辑）
        KeepDistance(to, dist);

        // 三点射（反应时间内只追不打）。连发数/连发间隔/散布都读敌人配置
        // （配置里没填就回落硬编码默认值），保证 Inspector 上的数值真的生效
        if (ai.ReactionReady && Time.time >= _nextBurst)
        {
            _burstLeft = ai.cfg != null ? ai.cfg.burstCount : 3;
            _nextBurst = Time.time + (ai.cfg != null ? ai.cfg.burstGap : 1.4f);
            _nextBurstShot = Time.time;
        }
        if (_burstLeft > 0 && Time.time >= _nextBurstShot)
        {
            _burstLeft--;
            _nextBurstShot = Time.time + (shootWeapon != null ? shootWeapon.fireInterval : 0.13f);

            Vector3 muzzle = transform.position + transform.forward * 0.6f + Vector3.up * 0.05f;
            float sp = ai.cfg != null ? ai.cfg.spread : 5f;
            float spread = Random.Range(-sp, sp);
            Vector3 dir = Quaternion.AngleAxis(spread, Vector3.up) * to.normalized;
            Bullet.Spawn(muzzle, dir, shootWeapon, Health.Team.Enemy);
            Effects.MuzzleFlash(muzzle, dir);
            Sfx.Shot(shootWeapon);
        }
    }
}