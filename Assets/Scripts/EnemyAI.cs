using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 敌人AI核心：感知（巡逻 → 发现玩家）+ 追击状态 + 移动 + 血条 + 死亡。
/// 「怎么攻击」（近战砍 / 远程射击 / 自爆）不在本类，由挂载的 EnemyBehaviour 行为组件负责：
/// WaveManager 生成敌人时按配置挂上对应行为组件。
/// ⚠️ 本类**不是每帧**驱动它（这里曾经写成"每帧驱动 Tick()"）：只在**追击状态**下、
///    于 `FixedUpdate` 里调用 `behaviour.Tick()` —— 巡逻分支不会调用。
/// 要新增敌人行为 = 新建一个继承 EnemyBehaviour 的脚本即可，本类不需要改。
/// </summary>
[RequireComponent(typeof(Health))]
[RequireComponent(typeof(Rigidbody))]
public class EnemyAI : MonoBehaviour
{
    /// <summary>全场存活敌人注册表。用列表代替每帧 FindObjectsOfType 全场景搜索。</summary>
    public static readonly List<EnemyAI> All = new List<EnemyAI>();

    [Header("配置（由 WaveManager 生成时写入）")]
    [Tooltip("本敌人对应的配置资源")]
    public EnemyConfig cfg;
    [Tooltip("本敌人当前挂载的行为组件（决定怎么攻击）")]
    public EnemyBehaviour behaviour;
    [Tooltip("头部（爆头判定锚点；由 WaveManager 生成时写入）。可能为 null")]
    public EnemyHead head;

    [Header("巡逻")]
    public float patrolSpeed = 2.2f;
    public Vector3[] waypoints;

    [Header("索敌")]
    public float sightRange = 11f;     // 视野距离
    public float fov = 150f;           // 视野角度（巡逻时）
    public float loseSightTime = 3f;   // 失去视线多久后放弃追击

    [Header("移动")]
    public float chaseSpeed = 3.8f;

    [Header("攻击参数（生成时按配置赋值，行为组件读取）")]
    public float reactionTime = 0.4f;  // 发现玩家到第一次攻击的间隔
    public float attackRange = 1.4f;   // 近战距离
    public float meleeDamage = 12f;
    public float meleeCooldown = 0.9f;
    public float keepDistance = 7f;    // 远程保持的距离
    public WeaponData shootWeapon;     // 远程武器（EnemyShooterBehaviour 初始化时写入）

    Rigidbody _rb;
    Health _hp;
    Transform _player;
    int _wpIndex;
    float _lastSeenTime = -999f;   // -999：开局不进入追击状态，必须先看见玩家
    Vector3 _lastSeenPos;          // 最后看到玩家的位置（丢失视线后追这里，而不是透视追踪）
    float _hitTime = -999f;        // 最近一次挨打时间
    float _nextSightCheck;         // 索敌检测降频计时（不必每个物理帧都做射线）
    bool _cachedSees;
    bool _lastChasing;             // 上一帧是否追击中（用于检测「刚发现玩家」）
    float _aggroStart = -999f;     // 最近一次进入追击的时间（反应时间起点）
    HealthBar _bar;

    /// <summary>是否处于交战状态（看见玩家，或近期挨过打）。WaveManager 的僵局兜底用它判断。</summary>
    public bool InCombat => (Time.time - _lastSeenTime < loseSightTime) || (Time.time - _hitTime < 4f);

    /// <summary>本敌人的生命组件（Awake 已缓存）。准星索敌等高频外部逻辑用它，避免反复 GetComponent。</summary>
    public Health HealthRef => _hp;

    /// <summary>反应时间是否已过（发现玩家后到可以第一次攻击的间隔）。行为组件攻击前检查它。</summary>
    public bool ReactionReady => Time.time - _aggroStart >= reactionTime;

    /// <summary>外部惊动（被传送到玩家附近后立刻进入追击）</summary>
    public void Alert()
    {
        if (_player == null) return;
        _lastSeenTime = Time.time;
        _lastSeenPos = _player.position;
        _aggroStart = Time.time;   // 被惊动也算「重新发现」，重置反应计时
    }

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _hp = GetComponent<Health>();
        _hp.OnDeath += OnDead;
        _hp.OnDamaged += OnDamagedBy;
        All.Add(this);
    }

    /// <summary>受伤回调：记录最近挨打时间（僵局兜底判断用）</summary>
    void OnDamagedBy(float dmg, Vector3 fromDir) => _hitTime = Time.time;

    void OnDestroy()
    {
        // 规范：订阅必须在销毁时退订，避免销毁后仍被回调
        if (_hp != null)
        {
            _hp.OnDeath -= OnDead;
            _hp.OnDamaged -= OnDamagedBy;
        }
        All.Remove(this);
    }

    void Start()
    {
        var p = GameObject.FindGameObjectWithTag(GameConst.TagPlayer);
        if (p != null) _player = p.transform;

        _bar = GetComponentInChildren<HealthBar>();
    }

    void FixedUpdate()
    {
        // 暂停（背包打开，timeScale=0）时 FixedUpdate 照常执行，必须显式拦截，否则敌人会趁暂停继续移动/攻击
        if (GameState.GameOver || GameState.Paused || _player == null) return;

        // 索敌检测降频：每 0.12 秒做一次视线射线（敌人多时省大量物理射线）
        if (Time.time >= _nextSightCheck)
        {
            _nextSightCheck = Time.time + 0.12f;
            _cachedSees = CanSeePlayer();
        }
        bool seesPlayer = _cachedSees;

        if (seesPlayer)
        {
            _lastSeenTime = Time.time;
            _lastSeenPos = _player.position;
        }
        bool chasing = Time.time - _lastSeenTime < loseSightTime;

        // 从「未追击」变为「看见玩家」：记录反应时间起点（发现玩家 → 第一次攻击前的停顿）
        if (seesPlayer && !_lastChasing) _aggroStart = Time.time;
        _lastChasing = chasing;

        if (!chasing) Patrol();
        else
        {
            // 追击目标：看得见→玩家实时位置；看不见→最后目击位置
            Vector3 target = seesPlayer ? _player.position : _lastSeenPos;
            Vector3 toTarget = target - transform.position;
            toTarget.y = 0f;

            // 到达最后目击点但仍然看不见 → 放弃追击，回巡逻
            if (!seesPlayer && toTarget.magnitude < 1.2f)
            {
                _lastSeenTime = -999f;
                Patrol();
            }
            else if (behaviour != null) behaviour.Tick(target, seesPlayer);
        }

        // 血条面向摄像机
        if (_bar != null) _bar.UpdateBar(_hp.hp / _hp.maxHp);
    }

    // ---------- 视野判断 ----------
    bool CanSeePlayer()
    {
        Vector3 toPlayer = _player.position - transform.position;
        toPlayer.y = 0f;
        float dist = toPlayer.magnitude;
        if (dist > sightRange) return false;

        // 巡逻状态下有视野角限制；追击状态全向感知
        bool aggro = Time.time - _lastSeenTime < loseSightTime;
        if (!aggro && dist > 3f)
        {
            float angle = Vector3.Angle(transform.forward, toPlayer);
            if (angle > fov * 0.5f) return false;
        }

        // 视线遮挡检测：先打到障碍物的算看不见。
        // 掩码 = Layers.SightMask（墙/箱子/地面 + 玩家）—— **不含敌人层**：
        // 敌人排成一列时，后排的视线若被前排同伴挡住 → 会判定"看不见玩家"→ 不追击也不开火
        //（玩家感受："后面的敌人像傻子一样站着"）。
        // ⚠️ 这是 V-12 的**正掩码**方案：场景阻挡物（地面 / 4 面墙 / 9 个箱子）已按 Ground / Obstacle 分层，
        //    见 Layers 的注释；改动分层方案时必须"物体分层 + 所有射线"一次做完，否则会射线穿墙。
        Vector3 origin = transform.position + Vector3.up * 0.4f;
        Vector3 dir = toPlayer.normalized;
        if (Physics.Raycast(origin, dir, out RaycastHit hit, dist, Layers.SightMask, QueryTriggerInteraction.Ignore))
        {
            return hit.transform.CompareTag(GameConst.TagPlayer);
        }
        return false;
    }

    // ---------- 巡逻 ----------
    void Patrol()
    {
        if (waypoints == null || waypoints.Length == 0) return;
        Vector3 wp = waypoints[_wpIndex];
        Vector3 to = wp - transform.position;
        to.y = 0f;
        if (to.magnitude < 0.6f)
            _wpIndex = (_wpIndex + 1) % waypoints.Length;

        MoveTo(to.normalized, patrolSpeed);
        FaceTo(to);
    }

    // ---------- 供行为组件调用的通用能力 ----------

    /// <summary>面向目标方向（Y 轴旋转，平滑转向）</summary>
    public void FaceTo(Vector3 to)
    {
        to.y = 0f;
        if (to.sqrMagnitude > 0.001f)
        {
            var rot = Quaternion.LookRotation(to.normalized);
            // ⚠️ 这里直接写 `transform.rotation`（不用 `Rigidbody.MoveRotation`）**目前是正确的**：
            //    敌人的刚体**没有开插值**（2026-09-22 实测 interpolation=None）→ 不存在"物理系统接管 transform、
            //    直接写会打断插值"的问题。（玩家当初就在这上面踩过，见 `PlayerController.UpdateAim` 的长注释。）
            // ⚠️ 但**一旦给敌人开了插值**（想让 50Hz 的位移变平滑），下面三件事必须**一起**做，缺一不可：
            //    ① 这一行换成 `_rb.MoveRotation(look)`；② `SpawnEnemy` 里约束从 `FreezeRotation`（全冻结）放开成
            //    `FreezeRotationX | FreezeRotationZ`（否则转不动）；③ 确认刚体开了 `Interpolate`。
            transform.rotation = Quaternion.Slerp(transform.rotation, rot, 8f * Time.fixedDeltaTime);
        }
    }

    /// <summary>朝方向移动（前方有障碍自动侧向绕行）</summary>
    public void MoveTo(Vector3 dir, float speed)
    {
        // 前方有障碍则侧向绕行。
        // ⚠️ 掩码刻意**带上玩家/敌人**（Layers.AvoidMask）：保留原来"躲开同伴"的行为，
        //    避免敌人贴着同伴硬挤；只看阻挡物会让它们叠在一起。
        Vector3 origin = transform.position + Vector3.up * 0.4f;
        if (Physics.Raycast(origin, dir, 1.2f, Layers.AvoidMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            dir = (Random.value < 0.5f) ? (dir + side * 0.8f).normalized : (dir - side * 0.8f).normalized;
        }
        var v = _rb.velocity;
        v.x = Mathf.MoveTowards(v.x, dir.x * speed, 20f * Time.fixedDeltaTime);
        v.z = Mathf.MoveTowards(v.z, dir.z * speed, 20f * Time.fixedDeltaTime);
        _rb.velocity = v;
    }

    /// <summary>站定不动（命中时清空速度）</summary>
    public void StopMoving()
    {
        _rb.velocity = Vector3.zero;
    }

    /// <summary>给刚体一个瞬时速度（近战的小冲刺表现等）</summary>
    public void Dash(Vector3 velocity)
    {
        _rb.velocity = velocity;
    }

    /// <summary>直接攻击玩家（近战行为用；远程行为走子弹）</summary>
    public void AttackPlayer(float dmg)
    {
        var hp = _player.GetComponent<Health>();
        if (hp != null && hp.alive)
        {
            hp.TakeDamage(dmg, (_player.position - transform.position).normalized);
            Sfx.Hurt();
        }
    }

    // ---------- 死亡 ----------
    void OnDead()
    {
        All.Remove(this);   // 立即移出注册表：尸体在销毁前不应再被僵局兜底等逻辑当成活敌人

        GameState.Kills++;
        GameState.AliveEnemies--;

        // 打击感：击杀确认（震屏比玩家受击的 0.15 稍强，+ 专属击杀音）
        CameraRig.Shake(0.2f);
        Sfx.Kill();

        // 击杀反馈（屏幕下方中央的图标 + 名字）：**自杀不算**
        //（自爆兵自爆走 Health.TakeDamage(creditKill:false)），爆头击杀用另一套图标（最后致命一击是爆头）。
        if (_hp == null || _hp.LastDamageCredited)
            KillFeed.Show(cfg != null ? cfg.displayName : "敌人", _hp != null && _hp.LastDamageWasHeadshot);

        // 远程行为按配置概率掉落武器（波次模式下避免满地枪）
        // ⚠️ 判据是「**身上有没有枪**」（`shootWeapon != null`），**不要**写成 `behaviour is EnemyShooterBehaviour` ——
        //    那样狙击手永远不会掉（`EnemySniperBehaviour` 继承的是 `EnemyBehaviour` 基类，不是 `EnemyShooterBehaviour`），
        //    而且**静默失效**（不报错，只是概率恒为 0），用户 2026-09-24 实测"把掉落概率调到 1 也不掉"才发现。
        //    近战/自爆兵不设置 `shootWeapon` → 天然不掉，语义仍是"只有带枪的才掉"。
        //
        // 掉落语义（用户 2026-09-24 定，**A/B 两条路刻意分开**）：
        //   · **敌人手里的枪** = `CloneShootWeapon()` 按 `EnemyConfig` 改过数值的副本（伤害/间隔/弹速/散布）——
        //     那是"敌人的攻击参数"，**不是战利品**；
        //   · **掉给玩家的** = 配置里那把**基础武器的正常副本**（玩家侧数值 + 正确名字/图标/拾取模型）。
        //     ⚠️ 所以这里掉 `cfg.shooterWeapon` 而**不是** `shootWeapon`：否则玩家会捡到"狙击手的枪"
        //     （实测伤害只有 M107 的 1/3、弹速不到一半，见 §3 日志 2026-09-24）。
        //   ⚠️ 两边都必须 `Clone()`：直接掉资源本体会让玩家侧的改动写回 `.asset`（同 §4 #25 那族风险）。
        //   ⚠️ `cfg.shooterWeapon` 留空时（敌人用的是代码兜底的 SMG）没有"基础武器"可言 → 回落到敌人手里那把，
        //     行为与旧版一致，不会因为没配资源就变成"什么都不掉"。
        if (shootWeapon != null && cfg != null && Random.value < cfg.dropWeaponChance)
        {
            var loot = (cfg.shooterWeapon != null ? cfg.shooterWeapon : shootWeapon).Clone();
            WeaponPickup.Create(loot, transform.position + Vector3.up * 0.0f);
        }

        // 简单死亡特效：火花 + 尸体下沉消失
        Effects.Impact(transform.position + Vector3.up * 0.4f, new Color(1f, 0.3f, 0.2f), 1.6f);
        Effects.Decal(transform.position, new Color(0.4f, 0.05f, 0.05f, 0.8f));
        if (_bar != null) Destroy(_bar.gameObject);

        // 用自定义模型时不强行染成纯色（那会破坏模型本该有的外观）；
        // 只有用代码生成的胶囊体时才染色表示死亡。
        // 走 Health.SetTint（MaterialPropertyBlock 覆盖，不换 sharedMaterial）：
        // **身体 + 头 + 脸 一起变暗红** —— 原来只抓第一个 Renderer，加了头/脸之后会漏。
        if ((cfg == null || cfg.bodyPrefab == null) && _hp != null)
            _hp.SetTint(new Color(0.3f, 0.05f, 0.05f));

        foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
        _rb.velocity = Vector3.zero;
        _rb.detectCollisions = false;
        // 死亡时隐藏狙击红线（否则 LineRenderer 冻结在屏幕上直到尸体销毁）
        if (behaviour is EnemySniperBehaviour sniper) sniper.HideLaser();
        enabled = false;
        Destroy(gameObject, 1.2f);
    }
}