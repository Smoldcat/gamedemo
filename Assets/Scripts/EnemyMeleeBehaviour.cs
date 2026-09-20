using UnityEngine;

/// <summary>
/// 近战行为：冲到攻击距离内，冷却后挥砍，攻击瞬间带一个小冲刺。
/// 攻击距离 / 伤害 / 冷却都读 EnemyAI 上的参数（生成时由 WaveManager 从敌人配置赋值）。
/// </summary>
[BehaviourName("近战")]
public class EnemyMeleeBehaviour : EnemyBehaviour
{
    float _nextMelee;
    float _dashUntil;   // 冲刺持续到的时间点（冲刺期间不调 StopMoving，否则下一物理帧就被清零）

    public override void Tick(Vector3 target, bool seesPlayer)
    {
        Vector3 to = target - transform.position;
        to.y = 0f;
        float dist = to.magnitude;

        ai.FaceTo(to);
        if (dist > ai.attackRange)
        {
            ai.MoveTo(to.normalized, ai.chaseSpeed);
            return;
        }

        // 冲刺持续期内不站定，保持冲刺速度（表现：挥砍瞬间往前蹿一小段）
        if (Time.time < _dashUntil)
        {
            ai.MoveTo(to.normalized, ai.chaseSpeed * 2.5f);
            return;
        }

        ai.StopMoving();
        if (seesPlayer && ai.ReactionReady && Time.time >= _nextMelee)
        {
            _nextMelee = Time.time + ai.meleeCooldown;
            ai.AttackPlayer(ai.meleeDamage);
            ai.Dash(transform.forward * 4f);   // 小冲刺表现
            _dashUntil = Time.time + 0.25f;    // 冲刺持续 0.25 秒（约 1 米）
        }
    }
}