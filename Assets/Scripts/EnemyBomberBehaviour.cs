using UnityEngine;

/// <summary>
/// 自爆行为：高速冲向玩家 → 进入爆炸半径后站定 + 身体红光闪烁预警（引信时间）→ 自爆。
/// 爆炸对玩家造成大范围高伤害，也会误伤周围其他敌人（可把自爆兵引进敌群）。
/// 自杀走 Health 死亡流程：击杀数/存活数/死亡特效都由 EnemyAI.OnDead 统一处理。
/// </summary>
[BehaviourName("自爆")]
public class EnemyBomberBehaviour : EnemyBehaviour
{
    bool _armed;          // 已进入引爆预警
    float _fuseAt;        // 引爆时间点
    Health _hp;           // 预警闪烁统一走 Health（会连头/脸一起闪，也不依赖子物体顺序）

    static readonly Color WarningFlash = new Color(1f, 0.15f, 0.1f);   // 预警闪烁红
    static readonly Color WarningDim = new Color(0.75f, 0.35f, 0.25f);  // 闪烁间隔的暗红

    /// <summary>误伤友军的快照缓冲（复用，避免每次爆炸都 new 一个 List → GC 峰值）。
    /// 纯容器、每次用前 Clear，跨会话残留无害，不需要 StaticReset 登记。</summary>
    static readonly System.Collections.Generic.List<EnemyAI> _aoeSnapshot = new System.Collections.Generic.List<EnemyAI>();

    protected override void OnInit()
    {
        _hp = GetComponent<Health>();
    }

    public override void Tick(Vector3 target, bool seesPlayer)
    {
        if (_armed)
        {
            // 预警中：站桩 + 全身红光闪烁 + 引信到点就爆（玩家有 fuseTime 时间跑开）
            if (_hp != null)
            {
                float k = Mathf.PingPong((Time.time - _fuseAt) * 7f, 1f);   // 0~1 往复 → 闪烁
                _hp.SetTint(Color.Lerp(WarningDim, WarningFlash, k));
            }
            if (Time.time >= _fuseAt) Explode();
            return;
        }

        Vector3 to = target - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        ai.FaceTo(to);

        // 进入爆炸半径 + 反应时间过 → 点燃引信
        if (dist <= ExplodeRadius() && ai.ReactionReady)
        {
            _armed = true;
            _fuseAt = Time.time + FuseTime();
            ai.StopMoving();
            Sfx.Hurt();   // 用受伤音作警告提示（玩家听到就该跑了）
            return;
        }

        ai.MoveTo(to.normalized, ai.chaseSpeed);   // 高速冲脸
    }

    float ExplodeRadius() => ai.cfg != null ? ai.cfg.explodeRadius : 4f;
    float ExplodeDamage() => ai.cfg != null ? ai.cfg.explodeDamage : 60f;
    float FuseTime() => ai.cfg != null ? ai.cfg.fuseTime : 0.8f;

    void Explode()
    {
        float radius = ExplodeRadius();
        float dmg = ExplodeDamage();

        // 恢复颜色（否则尸体上的死亡变色会被预警色盖住）
        if (_hp != null) _hp.ClearTint();

        // 爆炸表现：大火花 + 地面烧痕 + 爆炸范围圈（临时用扩散圆环表示，待素材就绪可换粒子）+ 音效
        Effects.Impact(transform.position + Vector3.up * 0.4f, new Color(1f, 0.55f, 0.1f), 3.2f);
        Effects.Impact(transform.position + Vector3.up * 0.4f, new Color(0.9f, 0.9f, 0.95f), 2.2f);
        Effects.Decal(transform.position, new Color(0.12f, 0.1f, 0.1f, 0.7f));
        Effects.BlastRing(transform.position, radius);   // 扩散圆环：直观显示爆炸范围

        // 对玩家造成范围伤害（离得越近越疼？不，爆炸就是满伤害）
        var pc = PlayerController.Instance;
        if (pc != null)
        {
            var hp = pc.GetComponent<Health>();
            // 隔墙不掉血：与 Bullet/Combat 的爆炸 AOE 用同一套视线检测（复用它的实现，不复制第二份真相）。
            // 否则玩家躲到箱子/墙后仍会被"隔空炸死"，和 RPG 的规则自相矛盾。
            if (hp != null && hp.alive
                && Vector3.Distance(transform.position, pc.transform.position) <= radius
                && Combat.HasClearPath(transform.position, pc.transform.position, pc.transform))
            {
                hp.TakeDamage(dmg, (pc.transform.position - transform.position).normalized);
                Sfx.Hurt();   // 只有炸到玩家才播受伤音，避免远处爆炸误导
            }
        }

        // 误伤周围其他敌人（爆炸炸自己人，可引它进敌群）
        // 注意必须快照遍历：循环内打死友军会触发 EnemyAI.OnDead → All.Remove，直接枚举会抛 InvalidOperationException，
        // 中断后续的自杀代码（自爆兵将永生 + 每帧重复爆炸）。
        _aoeSnapshot.Clear();
        _aoeSnapshot.AddRange(EnemyAI.All);
        for (int i = 0; i < _aoeSnapshot.Count; i++)
        {
            var e = _aoeSnapshot[i];
            if (e == null || e == ai) continue;
            var h = e.GetComponent<Health>();
            if (h != null && h.alive
                && Vector3.Distance(transform.position, e.transform.position) <= radius
                && Combat.HasClearPath(transform.position, e.transform.position, e.transform))   // 与玩家那条同规则
                h.TakeDamage(dmg * 0.6f, (e.transform.position - transform.position).normalized);
        }

        // 自杀：走 Health 死亡流程（EnemyAI.OnDead 统一处理计数/特效/销毁）
        var selfHp = GetComponent<Health>();
        // creditKill:false —— 自爆兵自爆不算"被击杀"：击杀反馈（图标+名字）不显示它
        //（GameState.Kills 计数仍照旧 +1，与改动前一致）
        if (selfHp != null) selfHp.TakeDamage(selfHp.hp, Vector3.zero, creditKill: false);
        else Destroy(gameObject);
    }
}