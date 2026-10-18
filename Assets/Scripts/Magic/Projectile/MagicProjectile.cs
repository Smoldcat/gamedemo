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
    Health _targetHealth;     // 缓存追踪目标的血量组件（避免 homing 期间每帧 GetComponent；目标换了/销毁了会自动重取）

    /// <summary>已判定消散。⚠️ 用显式标志而不是 `this == null`：Unity 的 `Destroy` 是**延迟**到帧末的，
    /// 同一帧里 `this == null` 还不会成立 —— 那种写法看起来像守卫，其实什么都没挡。</summary>
    bool _dead;

    /// <summary>球体的命中半径（球比看起来大一点，手感更跟手）。</summary>
    const float HitRadius = 0.28f;

    /// <summary>② 兜底查询用的**复用缓冲**（`OverlapSphereNonAlloc`）。
    /// ⭐ 2026-10-18（外部审查 A13）：原来每帧每球 `Physics.OverlapSphere` 都**新建一个 `Collider[]`**
    ///    （5 颗球 = 每帧 5 次分配）。改成 NonAlloc + 这个常量容量。
    /// 📌 `HitRadius` 只有 0.28m，32 个槽足够；万一塞满，返回的 count 会被截断（漏掉的只是"同帧叠在一起的第 33 个"）。</summary>
    readonly Collider[] _hitBuf = new Collider[32];

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
        Layers.SetRecursively(gameObject, Layers.FX);   // 统一实现（2026-10-15 收敛）
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

    // 设层已收敛到 `Layers.SetRecursively`（2026-10-15）：本类原来那份 `SetLayer` 已删。

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
            // ⚠️ 缓存目标的 Health：homing 期间**每帧** GetComponent 是白白开销（2026-10-15 审计）。
            //    目标换人 / 组件被销毁（Unity 假 null 会命中 `== null`）时自动重取 → 行为不变。
            if (_targetHealth == null || _targetHealth.transform != _target)
                _targetHealth = _target.GetComponent<Health>();
            var th = _targetHealth;
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
    /// 两件事**分开做**（2026-09-29 起）：
    ///   ① **公转**：每帧按 `orbitSpeed` 推进自己的角度 —— 不论补不补位，球都一直在绕圈；
    ///   ② **补位**：只有 `allowCatchUp == true` 时才朝【当前剩余数量的均分位置】限速靠拢。
    ///      ⚠️ 为什么拆开：用户要的是"派出后先观察一小会儿，确认这一波派完了再统一补位"，
    ///        放行与否由管理器按 `ProjectileMagicData.catchUpDelay` 决定。
    ///        拆开还有一个好处：`orbitCatchUpSpeed = 0` 也**不会**把球的公转一起钉死（旧实现会）。
    ///
    /// ⭐ 补位是**限速滑动**（<see cref="ProjectileMagicData.orbitCatchUpSpeed"/> 度/秒），不是直接吸附：
    ///    直接吸附会让【5 等分 → 4 等分】时最外侧那颗**瞬间平移 1.09 米**（2026-09-28 实测），非常突兀。
    /// ⚠️ 已进入 <see cref="OrbPhase.Chasing"/> 的球会忽略本调用（它在自己飞）。
    /// ⚠️ 还在播"出现插值"（`!_flying`）时也忽略 —— 否则会和 `Update` 里的插值**抢位置**
    ///    （实测侥幸没出问题，但那依赖脚本执行顺序，不可靠）。</summary>
    public void SetOrbit(int index, int count, float phaseRad, bool allowCatchUp)
    {
        if (_phase != OrbPhase.Orbiting || _d == null || !_flying) return;

        // 本球"该在"的角度（公共相位 + 均分偏移）
        float slot = phaseRad + (Mathf.PI * 2f / Mathf.Max(1, count)) * index;

        if (float.IsNaN(_orbitAngle))
        {
            _orbitAngle = slot;                       // 第一次（刚生成）：直接吸附到本位，不延迟
        }
        else
        {
            // ① 公转：**永远**推进（球一直在绕圈，与补不补位无关）
            _orbitAngle += _d.orbitSpeed * Mathf.Deg2Rad * Time.deltaTime;

            // ② 补位：只有放行时才朝"均分位置"限速靠拢。
            //    ⚠️ 因为 `_orbitAngle` 与 `phaseRad` 以**同样的速率**前进，这里的差值只反映【相对偏移】，
            //       所以"公转 + 补位"叠加不会造成双倍公转。
            //    ⚠️ 必须走最短弧：把角度差归一化到 [-180°, 180°]，否则 350° → 10° 会被算成"倒着转 340°"。
            if (allowCatchUp)
            {
                float delta = Mathf.DeltaAngle(_orbitAngle * Mathf.Rad2Deg, slot * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                float maxStep = Mathf.Max(0f, _d.orbitCatchUpSpeed) * Mathf.Deg2Rad * Time.deltaTime;
                _orbitAngle += Mathf.Clamp(delta, -maxStep, maxStep);
            }

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

        // ② 当前位置兜底（复用缓冲，避免每帧每球分配 `Collider[]` —— 外部审查 A13）
        int n = Physics.OverlapSphereNonAlloc(transform.position, HitRadius, _hitBuf, _enemyMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (HandleHit(_hitBuf[i], transform.position)) return true;

        return false;
    }

    /// <summary>命中特效：卡上填了 <see cref="ProjectileMagicData.hitPrefab"/> 才播（延迟见 `hitDelay`）。
    ///
    /// ⚠️ **必须定时销毁** —— 命中特效 prefab 播完后粒子停下，但 GameObject **不会自己消失**；
    ///    不销毁的话每个命中点都会留下一份（5 个球打十几下就堆一堆）。
    ///    ⭐ 销毁时长**自动读** prefab 自己的粒子总时长（`Effects.PrefabFxLife` + 0.3s 余量）
    ///      → **换特效不用改代码、也不用填数值**。
    /// ⚠️ **延迟不能挂在球上**：球命中后会 `Expire()` 自毁，挂在它身上的计时器会跟着消失
    ///    → 延迟走**常驻的 `PlayerMagic`** 上的协程（它挂在 Player 上、跨整局存在）。</summary>
    void PlayHitFx(Vector3 point)
    {
        if (_d == null || _d.hitPrefab == null) return;
        if (_d.hitDelay <= 0f) { SpawnHitFx(point); return; }
        var host = PlayerMagic.Instance;
        if (host == null) { SpawnHitFx(point); return; }     // 找不到宿主就退化成"立即播"，不静默丢失
        host.StartCoroutine(SpawnHitFxLater(point));
    }

    void SpawnHitFx(Vector3 point)
    {
        if (_d == null || _d.hitPrefab == null) return;      // 延迟期间卡可能已被卸载
        // ⚠️ 必须**保留 prefab 自带的旋转** —— 本项目对特效 prefab 的统一约定（`HealFx` / `ExplosionFx` / `MagicMark`
        //    都这么做）。该 prefab 根旋转是 `(270,0,0)`（躺平），用 `Instantiate(prefab, point, Quaternion.identity)`
        //    会把它转正（2026-10-17 实测：世界尺寸 0.98 → 0.80，虽不严重但没必要偏）。
        var go = Instantiate(_d.hitPrefab);
        go.transform.position = point;
        Destroy(go, Effects.PrefabFxLife(_d.hitPrefab) + 0.3f);
    }

    System.Collections.IEnumerator SpawnHitFxLater(Vector3 point)
    {
        yield return new WaitForSeconds(_d.hitDelay);
        SpawnHitFx(point);
    }

    /// <summary>结算一次命中。返回 true = 这一帧的检测到此为止（有效命中，或撞墙消散）。
    /// ⚠️ 用 `GetComponentInParent&lt;Health&gt;()`：敌人的碰撞体可能挂在子物体上，
    ///    直接 `GetComponent` 会拿不到（表现是"打中了但没伤害"，极难查）。
    ///
    /// ⭐ **撞墙分支**（2026-09-29 补上 —— 之前是个"假开关"）：
    ///    卡上勾了 <see cref="ProjectileMagicData.blockedByObstacle"/> 时，墙 / 箱子 / 地面
    ///    也会被算进检测掩码（见 <see cref="Setup"/>），于是"扫到一个**没有 `Health`** 的碰撞体"
    ///    就意味着**撞到墙了** → 在这里消散。<br/>
    ///    ⚠️ 之前这里只判"有没有 Health"，没有 Health 就直接跳过 →
    ///       勾了开关球照样穿墙（静默失效：既不报错、也不生效）。
    ///    ⚠️ 反过来，**没勾**开关时掩码里根本没有墙 → 这个分支永远不会进来 → 天然穿墙、零开销。
    ///    ⚠️ 注意判 `!h.alive`：**已死敌人**的碰撞体仍在掩码内，但那是"打完的尸体"不是墙 ——
    ///       应该忽略它继续飞（否则球会被尸体挡住）。</summary>
    bool HandleHit(Collider col, Vector3 point)
    {
        if (col == null) return false;
        var h = col.GetComponentInParent<Health>();

        if (h == null)
        {
            // 走到这里 = 扫到了非敌人的东西 = 墙 / 箱子 / 地面（只有勾了开关才会进掩码）
            if (_d != null && _d.blockedByObstacle)
            {
                Effects.Impact(point, _d.orbColor, 1.2f);
                Expire();
                return true;          // 已消散 → 本帧不再继续检测
            }
            return false;             // 没勾开关（理论上进不来）→ 忽略、继续飞
        }

        // 已死敌人 / 玩家 → 不是有效目标，忽略并继续（不消散）
        if (!h.alive || h.team == Health.Team.Player) return false;

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
        PlayHitFx(point);                                   // 卡上填了命中特效 prefab 才播
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
