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
    Health _playerHealth;   // 玩家血量组件缓存（近战每次攻击都要用；原来每次 GetComponent）
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

    /// <summary>[DEBUG-ONLY] 调试可视化用：**当前是否看得见玩家**（就是 `CanSeePlayer` 每 0.12s 写进
    /// `_cachedSees` 的那个结果）。只读、无副作用 —— 让调试面板画的视野扇形能按"真值"变色，
    /// 而不是用 `InCombat` 那种近似（后者把"4 秒内挨过打"也算进来，会误报）。
    /// 中性语义："当前是否看见目标"。发布前清理见 `Assets/Debug/README.md` 的登记表。</summary>
    public bool SeesPlayerNow => _cachedSees;

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

        // ⭐ 闪光弹：图标先更新（致盲期间下面会提前 return）；致盲时**整段跳过**索敌/追击/攻击，
        //    只缓慢乱走 —— 否则它会按"最后目击点"继续追过来，那就不叫致盲了（用户 2026-10-18 选 B）。
        UpdateBlindIcon();
        if (Time.time < _blindUntil)
        {
            BlindWander();
            if (_bar != null) _bar.UpdateBar(_hp.hp / _hp.maxHp);   // 血条照常刷新
            return;
        }

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
        // ⭐ 闪光弹致盲：与「烟雾挡视线」**同一个出口** —— 所有敌人行为都读本函数返回值，
        //    所以这一行就能让全类敌人一起"失明"（不追人、不攻击、不再走"最后目击点"那条路）。
        if (Time.time < _blindUntil) return false;

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
        // 烟雾遮挡（2026-10-13 新增）：**烟云体积挡视线** —— 烟里的玩家，敌人看不见（会去"最后目击点"找）。
        // ⚠️ 判据只有这一处：所有敌人行为都吃本函数返回的 `seesPlayer` → 一处生效、全类敌人自动覆盖。
        // ⚠️ 刻意**不走**"给烟加碰撞体 + 新层加进 SightMask"那条路：那要动工程分层与物理碰撞矩阵，
        //    而且烟会变成实体（玩家/敌人被挡住走不进去）。改用静态注册表的几何判定，见 SmokeVolume.BlocksLine。
        if (SmokeVolume.BlocksLine(transform.position, _player.position)) return false;

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
        // ⭐ 闪光弹减速：**唯一入口**在这里 —— 巡逻 / 追击 / 致盲乱走 全都走 MoveTo，
        //    所以这一行就能覆盖所有移动（不随距离衰减，圈内统一）。
        if (Time.time < _slowUntil) speed *= _slowMul;

        // 前方有障碍则侧向绕行。
        // ⚠️ 掩码刻意**带上玩家/敌人**（Layers.AvoidMask）：保留原来"躲开同伴"的行为，
        //    避免敌人贴着同伴硬挤；只看阻挡物会让它们叠在一起。
        // 避障射线 10Hz 就够（A-12：原来每物理帧一条，30 敌人 ≈ 1500 次/秒；0.1 秒延迟玩家感知不到）
        if (Time.time >= _nextAvoidProbe)
        {
            _nextAvoidProbe = Time.time + 0.1f;
            Vector3 origin = transform.position + Vector3.up * 0.4f;
            _avoidHit = Physics.Raycast(origin, dir, 1.2f, Layers.AvoidMask, QueryTriggerInteraction.Ignore);
        }
        if (_avoidHit)
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

    // ==================== 闪光弹：致盲 / 减速（2026-10-18）====================

    float _blindUntil;        // 致盲截止时刻（`Time.time` 口径）
    float _slowUntil;         // 减速截止时刻
    float _slowMul = 1f;      // 减速倍率（1 = 正常；0.5 = 半速）
    float _nextAvoidProbe;    // 避障射线节流（0.1s 一次，A-12）
    bool _avoidHit;           // 上一次避障探测的结果
    Vector3 _blindDir;        // 致盲时"乱走"的方向
    float _blindNextTurn;     // 下次换向的时刻
    GameObject _blindIcon;    // 头顶致盲图标（懒创建）

    /// <summary>被闪光弹闪到：**致盲 + 减速**。重复命中取**更长的那个**，不叠加（避免叠成十分钟）。
    /// <paramref name="slowPercent"/> = 减速百分比（0.5 = 速度剩一半）。</summary>
    public void ApplyFlash(float blindSec, float slowPercent, float slowSec)
    {
        float now = Time.time;
        if (blindSec > 0f) _blindUntil = Mathf.Max(_blindUntil, now + blindSec);
        if (slowPercent > 0.001f && slowSec > 0f)
        {
            _slowMul = Mathf.Clamp01(1f - slowPercent);
            _slowUntil = Mathf.Max(_slowUntil, now + slowSec);
        }
        _blindNextTurn = 0f;   // 立刻选一个新方向（不然会沿用上次的）

        // ⚠️ 致盲期间 `FixedUpdate` **整段跳过 `behaviour.Tick`** ⇒ 行为组件里"由 Tick 维护的持续视觉"会**冻住**。
        //    目前只有狙击手的瞄准线属于这一类（它靠 Tick 里的 ShowLaser/HideLaser 切换），
        //    不主动收掉的话，红线会**一直指着最后的位置**悬在半空直到致盲结束（2026-10-18 全面检查发现）。
        //    📌 以后若再加"由 Tick 维护的持续视觉"的行为，也要在这里补一条（或让它能安全跳过 Tick）。
        if (behaviour is EnemySniperBehaviour sniper) sniper.OnBlinded();
    }

    /// <summary>致盲期间：**不听指挥** —— 不追人、不攻击；每隔约 1 秒换个随机方向、以半速摸索着走。
    /// 📌 这就是"瞎了的人在乱撞"，比"原地不动"自然（用户 2026-10-18 选 B）。</summary>
    void BlindWander()
    {
        float now = Time.time;
        if (now >= _blindNextTurn)
        {
            _blindNextTurn = now + 0.9f;
            float a = Random.Range(0f, Mathf.PI * 2f);
            _blindDir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
        }
        MoveTo(_blindDir, patrolSpeed * 0.5f);   // ⚠️ 走 MoveTo → 减速倍率也会生效
        FaceTo(_blindDir);
    }

    /// <summary>头顶"致盲"图标的显隐 + 位置 + 面向相机（懒创建，只在被闪到时才建）。</summary>
    void UpdateBlindIcon()
    {
        bool on = Time.time < _blindUntil;
        if (on && _blindIcon == null) _blindIcon = MakeBlindIcon();
        if (_blindIcon == null) return;

        if (_blindIcon.activeSelf != on) _blindIcon.SetActive(on);
        if (!on) return;

        _blindIcon.transform.position = transform.position + Vector3.up * 2.35f;
        var cam = CameraCache.Main;
        if (cam != null) _blindIcon.transform.rotation = cam.transform.rotation;   // 面向相机（billboard）
    }

    /// <summary>致盲图标的**共享材质**（所有敌人外观一致）。
    /// ⚠️ 原来这里是**每个敌人 `new Material(...)`**：Unity 销毁 GameObject 时**不会**连带销毁材质，
    ///    一波波刷怪会一路累积（2026-10-18 全面检查发现）。改成共享一份后：**不再泄漏**，
    ///    而且所有图标能用同一个材质 → **可合批**（少一批 DrawCall）。
    /// 📌 静态资源必须登记到 `StaticReset`（见本类 `ResetStatics`）。</summary>
    static Material _blindIconMat;

    static Material BlindIconMaterial
    {
        get
        {
            if (_blindIconMat == null)
            {
                _blindIconMat = new Material(Shader.Find("Sprites/Default"));
                _blindIconMat.color = new Color(1f, 1f, 1f, 0.9f);
            }
            return _blindIconMat;
        }
    }

    /// <summary>致盲图标的**共享网格**（一个 1×1 的四边形，朝 -Z）。
    /// ⚠️ **刻意不用 `CreatePrimitive(Quad)`**：它会附带一个 **MeshCollider**，而图标是敌人的**子物体**
    ///    ⇒ 那个碰撞体会挂到**父物体的 Rigidbody** 上，Unity 当场报
    ///    `Non-convex MeshCollider with non-kinematic Rigidbody is no longer supported`
    ///    （2026-10-18 实测：每建一个图标就一条警告；而且那一物理帧敌人的碰撞体集合多了一个非凸网格）。
    ///    手搓 4 顶点网格 ⇒ **零碰撞体、零警告**。</summary>
    static Mesh _blindIconMesh;

    static Mesh BlindIconMesh
    {
        get
        {
            if (_blindIconMesh == null)
            {
                var m = new Mesh { name = "致盲图标Quad" };
                m.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                    new Vector3(-0.5f,  0.5f, 0f), new Vector3(0.5f,  0.5f, 0f)
                };
                m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
                m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
                m.RecalculateBounds();
                _blindIconMesh = m;
            }
            return _blindIconMesh;
        }
    }

    /// <summary>静态状态清理（在 `StaticReset` 里用 `ResetOf` 登记）。
    /// ⚠️ 一旦本类有了 `ResetStatics()`，就**不能再在 `StaticReset` 里直接写 `EnemyAI.All.Clear()`** ——
    ///    那样编辑器校验会把它判成"有 ResetStatics 却没（统一登记）"，所以 `All.Clear()` 也搬进这里。</summary>
    internal static void ResetStatics()
    {
        All.Clear();                       // 存活敌人注册表（原来在 StaticReset 里直接清）

        if (_blindIconMat != null)
        {
            if (Application.isPlaying) Destroy(_blindIconMat);
            else DestroyImmediate(_blindIconMat);
        }
        _blindIconMat = null;

        if (_blindIconMesh != null)
        {
            if (Application.isPlaying) Destroy(_blindIconMesh);
            else DestroyImmediate(_blindIconMesh);
        }
        _blindIconMesh = null;
    }

    GameObject MakeBlindIcon()
    {
        var go = new GameObject("致盲图标(占位)");
        go.transform.SetParent(transform, false);
        go.transform.localScale = Vector3.one * 0.55f;

        go.AddComponent<MeshFilter>().sharedMesh = BlindIconMesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = BlindIconMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go;
    }

    /// <summary>直接攻击玩家（近战行为用；远程行为走子弹）</summary>
    public void AttackPlayer(float dmg)
    {
        // ⚠️ 玩家血量组件缓存一次（原来每次攻击都 GetComponent；玩家换人/销毁时自动重取，行为不变）—— 2026-10-15 审计
        if (_playerHealth == null) _playerHealth = _player.GetComponent<Health>();
        var hp = _playerHealth;
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
        Effects.BloodPool(transform.position);   // 地面血池（慢慢扩大；槽留空则回退旧的 Decal 贴花）
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