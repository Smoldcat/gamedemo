using UnityEngine;

/// <summary>
/// 投射物群里的**单个球**（运行时生成，不是 prefab —— 外观由卡上的 orbPrefab 或代码占位球决定）。
///
/// 生命周期：生成（**挂在玩家身上待命**）→ 被派出（锁定目标）→ 追踪 → 命中 / 目标死亡 / 超时 → 消散。
/// ⚠️ 索敌**不在这里**：由 <see cref="ProjectileGroupMagic"/> 在"轮到此球"时调用 <see cref="TryDispatch"/> 触发。
///
/// ⚠️ **穿墙的实现**：检测掩码**只留敌人层** —— 墙和箱子根本不在被检测范围内，自然穿过去。
///    不要用 `Layers.BulletMask`（它含 Obstacle 与 Ground，靠回调里跳过命中来假装穿墙更绕更易错）。
/// ⚠️ **本类不做主动索敌**：锁定目标只由管理器在"轮到本球"时（<see cref="TryDispatch"/>）触发一次。
/// ⚠️ **本类不碰 UI**：命中只走 `Health.TakeDamage`（受击白闪 / 伤害飘字 / 击杀播报全部自动获得）
///    与 `Effects` 粒子；要广播事件由 `PlayerMagic` 负责。
/// ⚠️ 表现资源全部来自卡：`orbPrefab` 留空 → 代码生成球体占位（与 `EnemyBehaviour.BuildGunModel`
///    的"优先资源、兜底代码"同一套做法）。
/// </summary>
public class MagicProjectile : MonoBehaviour
{
    // ==================== 两种状态（用户 2026-09-28 定，参考王者荣耀诸葛亮的被动） ====================

    /// <summary>法球的状态。
    ///   · <see cref="OrbPhase.Orbiting"/> = **挂在玩家身上**（绕圈待命）：位置由
    ///     <see cref="ProjectileGroupMagic"/> 每帧统一摆放，自己**不移动、不索敌、不攻击**；
    ///   · <see cref="OrbPhase.Chasing"/>  = 已被派出：锁定目标飞过去，命中 / 目标死亡 / 超时后消散。</summary>
    public enum OrbPhase { Orbiting, Chasing }

    ProjectileMagicData _d;
    Transform _owner;         // 玩家（索敌范围以**玩家**为圆心判定）
    OrbPhase _phase = OrbPhase.Orbiting;

    EnemyAI _target;          // 当前锁定的目标（Chasing 阶段恒为非 null，直到目标死亡）
    Vector3 _dir;             // 当前飞行方向（单位向量）
    Vector3 _lastTargetPos;   // 目标死亡瞬间的位置（飞到这里就消散）
    float _life;              // 剩余存活时间
    int _pierceLeft;
    int _enemyMask;

    // 出现插值（spawnDuration 大于 0 时才用）
    Vector3 _spawnFrom, _spawnTo;
    float _spawnT;
    bool _flying;

    /// <summary>当前公转角度（弧度），由 <see cref="SetOrbit"/> 维护。
    /// `NaN` = 还没摆过位 → 第一次调用时**直接吸附**到目标角度（免得从 0° 绕一大圈扫过去）。</summary>
    float _orbitAngle = float.NaN;

    /// <summary>宿主（玩家）的 Health —— **缓存**起来，避免每帧 `GetComponentInParent`（5 颗球 × 每帧不便宜）。
    /// 用途：挂在身上时检测"玩家是不是死了"（死了要立刻消散，别僵在半空）。</summary>
    Health _ownerHealth;

    /// <summary>锁定的目标被打死了（不是本球命中的）。
    /// true → 不再重新索敌（免得球飞到一半掉头去追别人），朝 `_lastTargetPos` 飞完就消散。</summary>
    bool _targetDied;

    /// <summary>已判定消散。⚠️ 用显式标志而不是 `this == null`：Unity 的 `Destroy` 是**延迟**到帧末的，
    /// 同一帧里 `this == null` 还不会成立 —— 那种写法看起来像守卫，其实什么都没挡。</summary>
    bool _dead;

    /// <summary>球体的命中半径（球比看起来大一点，手感更跟手）。</summary>
    const float HitRadius = 0.28f;

    // ==================== 创建 ====================

    /// <summary>生成一颗**挂在玩家身上**的法球（用户 2026-09-28 定：参考诸葛亮被动）。
    /// 出生点 = 环绕轨道上第 `index` 个位置（共 `count` 个均匀分布，`phaseRad` 是公共相位）。
    /// 之后由 <see cref="ProjectileGroupMagic"/> 每帧调用 <see cref="SetOrbit"/> 摆位，
    /// 直到它把本球 <see cref="TryDispatch"/> 派出去为止。</summary>
    internal static MagicProjectile CreateOrbiting(ProjectileMagicData data, Transform owner,
                                                   int index, int count, float phaseRad)
    {
        if (data == null) return null;

        Vector3 orbitPos = OrbitPositionOf(owner, data, index, count, phaseRad);
        var go = new GameObject("MagicOrb");
        go.transform.position = orbitPos;
        var p = go.AddComponent<MagicProjectile>();
        p.Setup(data, owner, orbitPos + data.spawnFromOffset, orbitPos);
        return p;
    }

    /// <summary>环绕轨道上第 index 个点的世界坐标（半径 = spawnRadius，高度 = 玩家原点 + spawnHeight）。
    /// ⚠️ 高度一律相对**玩家原点**算 —— 玩家原点在身高一半处，直接加常数很容易把球顶到敌人头顶之上
    ///    （2026-09-28 踩过：球飞 1.55m、敌人只有 1.17m 高，一颗都打不中）。</summary>
    static Vector3 OrbitPositionOf(Transform owner, ProjectileMagicData data, int index, int count, float phaseRad)
    {
        Vector3 c = owner != null ? owner.position : Vector3.zero;
        float ang = phaseRad + (Mathf.PI * 2f / Mathf.Max(1, count)) * index;
        Vector3 p = c + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * data.spawnRadius;
        p.y = c.y + data.spawnHeight;
        return p;
    }

    void Setup(ProjectileMagicData data, Transform owner, Vector3 from, Vector3 to)
    {
        _d = data;
        _owner = owner;
        _ownerHealth = owner != null ? owner.GetComponentInParent<Health>() : null;
        _phase = OrbPhase.Orbiting;
        _target = null;
        _targetDied = false;
        _dir = Vector3.forward;
        _lastTargetPos = to;
        _orbitAngle = float.NaN;      // 第一次 SetOrbit 时直接吸附到本位
        // ⚠️ 挂着的时候就开始计时（用户 2026-09-28 定：最多挂 6s，不然一直没敌人就能无限叠加）；
        //    被派出时会**重新计一次**（见 TryDispatch）—— 免得挂到最后一刻的球刚出击就超时。
        _life = data.orbLifetime;
        _pierceLeft = data.pierceCount;

        // ⚠️ 掩码：默认只测敌人层 → 天然穿墙；卡上勾了【被墙挡住】才把墙与地面加进来。
        // ⚠️ `Layers.Obstacle` / `Layers.Ground` 是**层索引**（12 / 11），必须各自左移成掩码后再或 ——
        //    直接 `索引A | 索引B` 会得到一个完全不相关的层掩码（静默失效，最难查的那种）。
        _enemyMask = 1 << Layers.Enemy;
        if (data.blockedByObstacle) _enemyMask |= (1 << Layers.Obstacle) | (1 << Layers.Ground);

        // 出现插值：时长 0 = 直接出现在终点
        _spawnFrom = from;
        _spawnTo = to;
        _spawnT = 0f;
        _flying = data.spawnDuration <= 0f;
        if (!_flying) transform.position = from;

        BuildVisual();
        SetLayer();
    }

    /// <summary>外观：优先卡上的 prefab，留空则用代码生成的球体占位。
    /// ⭐ 换成美术资源时**只改卡上的引用**，本类一行不用动。</summary>
    void BuildVisual()
    {
        if (_d.orbPrefab != null)
        {
            var v = Instantiate(_d.orbPrefab, transform);
            v.name = "Visual";
            v.transform.localPosition = Vector3.zero;
        }
        else
        {
            var v = Effects.CreateMeshObject(PrimitiveType.Sphere, "Visual", transform,
                                             Effects.Shared(_d.orbColor));
            v.transform.localPosition = Vector3.zero;
            v.transform.localScale = Vector3.one * (HitRadius * 1.6f);
        }

        if (_d.trailPrefab != null)
        {
            var t = Instantiate(_d.trailPrefab, transform);
            t.name = "Trail";
            t.transform.localPosition = Vector3.zero;
        }
    }

    void SetLayer()
    {
        if (Layers.FX >= 0)
            foreach (var t in GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layers.FX;
    }

    // ==================== 每帧 ====================

    void Update()
    {
        float dt = Time.deltaTime;

        // ① 出现插值（时长 0 时 _flying 一开始就是 true，跳过）
        if (!_flying)
        {
            _spawnT += dt / Mathf.Max(0.0001f, _d.spawnDuration);
            if (_spawnT >= 1f) { _spawnT = 1f; _flying = true; }
            transform.position = Vector3.Lerp(_spawnFrom, _spawnTo, _spawnT);
            return;   // 出现过程中不索敌、不飞行（"成型"之后才算数）
        }

        // ② 存活时限（用户 2026-09-28 定：挂着也开始计时，最多 6s —— 不然没敌人就能无限叠加）
        _life -= dt;
        if (_life <= 0f) { Expire(); return; }

        // ③ 挂在身上待命：位置由 ProjectileGroupMagic 每帧设置，自己什么都不做
        //    （不索敌、不移动、不攻击 —— 索敌只发生在"轮到本球"的那一刻，见 TryDispatch）
        if (_phase == OrbPhase.Orbiting)
        {
            // ⚠️ 宿主失效（玩家死亡 / 玩家物体被销毁）→ `PlayerController` 不再驱动 `PlayerMagic.Tick`，
            //    管理器就不会再摆位，球会**僵在半空不动**直到超时消散（2026-09-28 实测确认）。
            //    这里直接消散，别在玩家尸体旁留几颗飘着的球。
            if (_owner == null || (_ownerHealth != null && !_ownerHealth.alive)) { Expire(); return; }
            return;
        }

        // ---------- 以下都是"已被派出"（Chasing）之后的飞行逻辑 ----------

        // ④ 目标还活着吗（活着 → 一直更新它的位置；死了 → 记住死亡瞬间的位置）
        if (_d.homing && _target != null)
        {
            var th = _target.GetComponent<Health>();
            if (th != null && th.alive)
            {
                _lastTargetPos = _target.transform.position;
            }
            else
            {
                // ⚠️ 走到这里说明**不是本球打死的**（本球命中的话在上面的 OnHit 里就处理了）——
                //    别人抢了人头。此时不再重新索敌，飞完最后一段就消散（用户 2026-09-28 定）。
                _targetDied = true;
                _target = null;
            }
        }

        // ⑤ 已到达"目标死亡时的位置" → 消散（用户 2026-09-28 定：飞到了就自动消失）
        //     ⚠️ 判定用三维距离、半径 1m：球每帧走约 0.23m，1m 足够宽松、不会被高速跨过。
        if (_targetDied && (transform.position - _lastTargetPos).sqrMagnitude < 1f)
        {
            Expire();
            return;
        }

        // ⑥ 转向（⭐ **含高度**：球会自动修正到目标的真实高度 ——
        //     之前"球飞 1.55m、敌人只有 1.17m 高 → 从头顶掠过打不中"就是因为这里把 y 清零了）
        // ⚠️ 新逻辑下**不需要**再判"有没有锁定过"：能进入 Chasing 就一定被派过（必有目标位置），
        //    所以 `_lastTargetPos` 永远有意义（活着=目标当前位置，死了=死亡瞬间位置）。
        if (_d.homing)
        {
            Vector3 want = _lastTargetPos - transform.position;
            if (want.sqrMagnitude > 0.0001f)
                _dir = TurnTowards(_dir, want.normalized, _d.turnSpeed * Mathf.Deg2Rad * dt);   // ⚠️ 度 → 弧度
        }
        transform.rotation = Quaternion.LookRotation(_dir, Vector3.up);

        float step = _d.orbSpeed * dt;

        // ⑦ 命中检测（两道保险，见 TryHit 注释）
        if (TryHit(step) && _dead) return;   // 穿透数为 0 → OnHit 里已经判定消散

        transform.position += _dir * step;
    }

    // ==================== 挂在身上（轨道待命） ====================

    /// <summary>由 <see cref="ProjectileGroupMagic"/> **每帧**调用：把自己摆到环绕轨道的第 `index` 位。
    ///
    /// ⚠️ 每帧**重算目标角度**（而不是自己累加）的关键好处：只要有球被派出去，
    ///    剩下的球下一帧就会朝新的均分位置靠拢（不会留下空位）—— 这就是诸葛亮被动的观感。
    /// ⭐ 但靠拢是**限速滑动**（<see cref="ProjectileMagicData.orbitCatchUpSpeed"/> 度/秒），不是直接吸附：
    ///    直接吸附会让【5 等分 → 4 等分】时最外侧那颗**瞬间平移 1.09 米**（2026-09-28 实测），非常突兀。
    /// ⚠️ 已进入 <see cref="OrbPhase.Chasing"/> 的球会忽略本调用（它在自己飞）。
    /// ⚠️ 还在播"出现插值"（`!_flying`）时也忽略 —— 否则会和 `Update` 里的插值**抢位置**
    ///    （实测侥幸没出问题，但那依赖脚本执行顺序，不可靠）。</summary>
    public void SetOrbit(int index, int count, float phaseRad)
    {
        if (_phase != OrbPhase.Orbiting || _d == null || !_flying) return;

        // 目标角度 = 公共相位 + 均分偏移
        float target = phaseRad + (Mathf.PI * 2f / Mathf.Max(1, count)) * index;

        if (float.IsNaN(_orbitAngle))
        {
            _orbitAngle = target;                     // 第一次：直接吸附到本位
        }
        else
        {
            // 朝目标角度限速靠拢。⚠️ 必须走最短弧：把角度差归一化到 [-180°, 180°]，
            //    否则 350° → 10° 会被算成"倒着转 340°"。
            float delta = Mathf.DeltaAngle(_orbitAngle * Mathf.Rad2Deg, target * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            float maxStep = Mathf.Max(0f, _d.orbitCatchUpSpeed) * Mathf.Deg2Rad * Time.deltaTime;
            _orbitAngle += Mathf.Clamp(delta, -maxStep, maxStep);
            // ⚠️ 归一化到 [0, 2π)：不归一的话角度会随游玩时间**无限增大**
            //    （实测仅 3 分钟就累到 972°），float 精度会越来越粗 → 长时间游玩后公转会变顿。
            //    安全性：位置由 cos/sin 算（周期函数，折返无影响）；靠拢用 `Mathf.DeltaAngle`（走最短弧），也不怕折返。
            _orbitAngle = Mathf.Repeat(_orbitAngle, Mathf.PI * 2f);
        }

        Vector3 c = _owner != null ? _owner.position : Vector3.zero;
        Vector3 p = c + new Vector3(Mathf.Cos(_orbitAngle), 0f, Mathf.Sin(_orbitAngle)) * _d.spawnRadius;
        p.y = c.y + _d.spawnHeight;
        transform.position = p;

        // 面朝切线方向（看起来像在绕圈）
        Vector3 tangent = Vector3.Cross(Vector3.up, p - c);
        if (tangent.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(tangent.normalized, Vector3.up);
    }

    /// <summary>⭐ 由 <see cref="ProjectileGroupMagic"/> 在**攻击间隔到点**时调用：
    /// 「轮到你了 —— 在以玩家为圆心 `searchRadius` 内**随机**找一个活敌人：
    ///   找到就飞出去追（返回 true，管理器把你从身上摘掉）；**没找到就继续挂着**（返回 false）」。
    ///
    /// ⚠️ **这一段就是用户 2026-09-28 定的核心规则**：索敌只发生在"轮到本球的那一刻"，
    ///    而不是施法时提前分配 —— 所以"前一颗球在路上、目标就死了"时，
    ///    **后面的球压根不会被派出**（不会追到尸体位置去）。
    /// ⚠️ 飞行阶段**重新计一次**存活时限：不然挂到第 5.9 秒才出击的球，刚起飞就超时消散、根本飞不到敌人。</summary>
    public bool TryDispatch()
    {
        if (_phase != OrbPhase.Orbiting || _d == null || _dead) return false;

        var tgt = FindRandomEnemyInRange();
        if (tgt == null) return false;      // 范围内没敌人 → 继续挂在身上

        _phase = OrbPhase.Chasing;
        _target = tgt;
        _targetDied = false;
        _lastTargetPos = tgt.transform.position;
        _life = _d.orbLifetime;             // ⭐ 飞行阶段重新计时

        Vector3 dir = tgt.transform.position - transform.position;
        if (dir.sqrMagnitude < 0.0001f) dir = transform.forward;
        _dir = dir.normalized;
        return true;
    }

    /// <summary>在以玩家为圆心、`searchRadius` 内**随机**取一个活敌人（用户 2026-09-28 定：范围内随机）。
    /// ⚠️ 水塘抽样：只遍历一遍、不产生 GC 分配。</summary>
    EnemyAI FindRandomEnemyInRange()
    {
        Vector3 origin = _owner != null ? _owner.position : transform.position;
        float r2 = _d.searchRadius * _d.searchRadius;

        EnemyAI best = null;
        int seen = 0;
        var all = EnemyAI.All;
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (e == null) continue;
            var h = e.GetComponent<Health>();
            if (h == null || !h.alive) continue;
            if ((e.transform.position - origin).sqrMagnitude > r2) continue;
            seen++;
            if (Random.Range(0, seen) == 0) best = e;   // 每个候选等概率被选中
        }
        return best;
    }

    // ==================== 命中 ====================

    /// <summary>命中检测（**两道保险**）。返回 true = 这一帧打中了某个敌人。
    ///   ① `SphereCast` 沿"这一帧要走的路"扫一段 → 防高速穿模；
    ///   ② `OverlapSphere` 查球**当前所在位置** → `SphereCast` **不检测起点处的重叠**，
    ///      球贴着敌人飞过去时会整个漏掉（这一步专门补它）。
    /// ⚠️ 只测 `_enemyMask`（默认只含敌人层）→ 天然穿墙，墙与箱子根本不在检测范围内。</summary>
    bool TryHit(float step)
    {
        if (_enemyMask == 0) return false;

        // ① 前扫
        if (Physics.SphereCast(transform.position, HitRadius, _dir, out RaycastHit hit,
                               step + 0.05f, _enemyMask, QueryTriggerInteraction.Ignore))
        {
            if (HandleHit(hit.collider, hit.point)) return true;
        }

        // ② 当前位置兜底
        var cols = Physics.OverlapSphere(transform.position, HitRadius, _enemyMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < cols.Length; i++)
            if (HandleHit(cols[i], transform.position)) return true;

        return false;
    }

    /// <summary>结算一次命中。返回 true = 有效命中（已造成伤害）。
    /// ⚠️ 用 `GetComponentInParent&lt;Health&gt;()`：敌人的碰撞体可能挂在子物体上，
    ///    直接 `GetComponent` 会拿不到（表现是"打中了但没伤害"，极难查）。</summary>
    bool HandleHit(Collider col, Vector3 point)
    {
        if (col == null) return false;
        var h = col.GetComponentInParent<Health>();
        if (h == null || !h.alive || h.team == Health.Team.Player) return false;
        OnHit(h, point);
        return true;
    }

    /// <summary>把当前方向朝目标方向旋转（受 turnSpeed 限制）—— 决定追踪弧线好不好看。
    /// ⚠️ `maxRadians` 的单位是**弧度**（与 `Vector3.Angle` 的结果同单位）——
    ///    调用处必须先把"度/秒 × dt"换算成弧度，否则转向会变成"瞬间完成"（turnSpeed 失效）。</summary>
    static Vector3 TurnTowards(Vector3 cur, Vector3 want, float maxRadians)
    {
        float ang = Vector3.Angle(cur, want) * Mathf.Deg2Rad;
        if (ang <= maxRadians || ang < 0.0001f) return want;
        var axis = Vector3.Cross(cur, want);
        if (axis.sqrMagnitude < 1e-8f) return want;
        return Quaternion.AngleAxis(maxRadians * Mathf.Rad2Deg, axis.normalized) * cur;
    }

    void OnHit(Health h, Vector3 point)
    {
        float dmg = _d.damage * MagicBehaviour.LevelMulOf(_d);
        h.TakeDamage(dmg, _dir);   // ⭐ 走标准伤害流程 → 白闪 / 飘字 / 击杀播报 / 掉落 全部自动获得

        Effects.Impact(point, _d.orbColor, 1.2f);
        if (_d.hitSound != null) Sfx.Play(_d.hitSound);

        if (_pierceLeft > 0)
        {
            _pierceLeft--;
            // 穿透：在范围内再随机找一个继续追；没有就转入"朝刚才的位置飞完就消散"
            var next = FindRandomEnemyInRange();
            if (next != null)
            {
                _target = next;
                _targetDied = false;
                _lastTargetPos = next.transform.position;
                Vector3 nd = next.transform.position - transform.position;
                if (nd.sqrMagnitude > 0.0001f) _dir = nd.normalized;
            }
            else
            {
                _target = null;
                _targetDied = true;
            }
        }
        else
        {
            Expire();
        }
    }

    void Expire()
    {
        if (_dead) return;
        _dead = true;
        if (_d.endSound != null) Sfx.Play(_d.endSound);
        Destroy(gameObject);
    }
}
