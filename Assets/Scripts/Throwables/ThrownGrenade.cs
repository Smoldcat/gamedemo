using UnityEngine;

/// <summary>
/// **飞出去的投掷物**（真飞行）：弹道积分 + 弹跳 + 引信 + 到点引爆（交给效果卡）。
///
/// ⚠️ 弹道只用 <see cref="ThrowableBallistics"/>（与"按住右键的预览"**共用同一份**，所见即所得）。
/// ⚠️ 引信口径（用户 2026-10-12 定）：**碰地面才点火**（主引信）；另有两道保底 ——
///    · 首次撞到**非地面障碍物**（墙 / 箱子）+ `impactFuse` 秒必炸（0 = 撞到即爆）
///    · **出手** + `maxLife` 秒必炸（0 = 关闭；防"什么都没撞到"的哑弹）
///    到点即炸：滚动中 / 空中 / 贴墙上，**在哪都炸**。
/// ⚠️ 只撞 `Layers.Blocker`（墙 / 箱子 / 地面）—— 不撞玩家 / 敌人 / 拾取物（层掩码天然排除）。
/// ⚠️ 本组件**不挂碰撞体**：飞行是"射线推进"（与子弹同一套思路），不靠物理引擎。
/// </summary>
public class ThrownGrenade : MonoBehaviour
{
    /// <summary>最多弹几次；超过就进入"滑行"阶段（不再弹跳）。常量：真需要再按类别开成参数。</summary>
    const int MaxBounces = 3;

    /// <summary>滑行阶段的水平减速度（米/秒²）：滑一小段就停（"落地可滑"的代价与手感都在这里）。</summary>
    const float RollingDrag = 6f;

    /// <summary>从"弹跳"转入"滑行"的法向速度阈值（米/秒）：弹不动了就该贴地滚。</summary>
    const float RollEnterVN = 1.5f;

    ThrowableData _def;
    Vector3 _vel;
    float _gravity;
    float _acc;              // 固定步长累加器
    int _bounces;
    bool _rolling;           // 贴地滑行阶段（不再弹跳）
    bool _settled;           // 停稳（速度 < stopSpeed）→ 不再积分，只等引信

    float _tThrow;           // 出手时刻
    float _tGround = -1f;    // 首次碰地面的时刻（-1 = 还没碰到过）
    float _tObstacle = -1f;  // 首次撞非地面障碍物的时刻（-1 = 还没撞到过）

    /// <summary>创建并抛出（唯一入口：`PlayerThrowable` 调用）。返回创建出来的飞行物（测试/调试用）。</summary>
    public static ThrownGrenade Spawn(ThrowableData def, Vector3 origin, Vector3 target, Vector3 fallbackDirH)
    {
        if (def == null) return null;

        var solve = ThrowableBallistics.SolveThrow(origin, target, def.peakHeight, def.throwSpeed, fallbackDirH);

        var go = new GameObject("Thrown_" + def.DisplayName);
        go.transform.position = origin;
        var g = go.AddComponent<ThrownGrenade>();
        g._def = def;
        g._vel = solve.v0;
        g._gravity = solve.gravity;
        g._tThrow = Time.time;
        g.BuildVisual();
        return g;
    }

    void BuildVisual()
    {
        // 与拾取物同一套"两条外观路径"：配了模型用模型，没配就用代码生成的占位球（颜色 = 卡上的标识色）
        if (_def != null && _def.pickupModelPrefab != null)
        {
            var model = Instantiate(_def.pickupModelPrefab, transform);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero;
            Layers.SetRecursively(gameObject, Layers.Pickup);
            return;
        }

        var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "Model";
        ball.transform.SetParent(transform, false);
        ball.transform.localScale = Vector3.one * (ThrowableBallistics.BallRadius * 2f);
        var col = ball.GetComponent<Collider>();
        if (col != null) Destroy(col);                 // 飞行不靠物理：自己身上的碰撞体一律去掉
        var r = ball.GetComponent<Renderer>();
        if (r != null && _def != null) r.sharedMaterial = Effects.Shared(_def.pickupColor);
        Layers.SetRecursively(gameObject, Layers.Pickup);   // 静态工具（含负值守卫）—— 本类不再自带一份
    }

    void Update()
    {
        if (_def == null) { Destroy(gameObject); return; }
        if (GameState.Paused || GameState.GameOver) return;   // 暂停 / 结算冻结（时间也停了，引信自动不burn）

        // ---- 弹道推进（固定步长 → 与预览逐点一致）----
        if (!_settled)
        {
            _acc += Time.deltaTime;
            // ⚠️ 2026-10-12 硬化：**夹住积压**。一帧最多补 16 步，若不夹，一次长卡顿（比如编辑器长时间
            //    挂起）会积压几百步 → 之后每帧都跑满 16 步"拖着补帧"，看起来像手雷在空中卡住/慢放。
            //    夹取后：卡顿只会让它"跳到当前应有的位置"（引信本来就用 `Time.time`，不会因此延后爆炸）。
            float maxAcc = ThrowableBallistics.SimDt * 16f;
            if (_acc > maxAcc) _acc = maxAcc;
            int guard = 0;
            while (_acc >= ThrowableBallistics.SimDt && guard++ < 16)
            {
                _acc -= ThrowableBallistics.SimDt;
                if (_rolling) RollStep(); else FlyStep();
                if (_settled) break;
            }
        }

        // ---- 引信：三个计时里最先到的那个 ----
        // ⚠️ 语义：**引信 / 保底 A 的 0 = "一满足条件立即爆"**（燃烧瓶：触地即碎、碰任何东西即碎）；
        //         **保底 B 的 0 = "关闭"**（"出手即爆"没有意义，所以它的 0 只能是关闭）。
        // ⚠️ 2026-10-12 修：保底 A 原来写成 `_def.impactFuse > 0f`，把 0 当成了"关闭"
        //    → 与设计口径（0 = 撞到即爆）相反；地面引信那条没有这个多余判断，两条现在对齐了。
        float now = Time.time;
        if (_tGround >= 0f && now >= _tGround + Mathf.Max(_def.groundFuse, 0f)) { Explode(); return; }
        if (_tObstacle >= 0f && now >= _tObstacle + Mathf.Max(_def.impactFuse, 0f)) { Explode(); return; }
        if (_def.maxLife > 0f && now >= _tThrow + _def.maxLife) { Explode(); return; }
    }

    /// <summary>飞行一步（可能撞、可能弹、可能转入滑行）</summary>
    void FlyStep()
    {
        Vector3 np, nv; RaycastHit hit;
        bool collided = ThrowableBallistics.Step(transform.position, _vel, _gravity,
                                                 ThrowableBallistics.SimDt, Layers.Blocker, out np, out nv, out hit);

        if (!collided)
        {
            transform.position = np;
            _vel = nv;
            return;
        }

        // ---- 撞到东西：先记引信（碰地面 / 碰障碍各算一次）----
        bool ground = ThrowableBallistics.IsGround(hit);
        if (ground && _tGround < 0f) _tGround = Time.time;
        if (!ground && _tObstacle < 0f) _tObstacle = Time.time;

        transform.position = np;
        _bounces++;

        // 落地 / 弹不动了 → 进入滑行（贴地滚一小段就停）
        Vector3 reflected = ThrowableBallistics.Reflect(nv, hit.normal, _def.bounceNormal, _def.bounceTangential);
        float vn = Vector3.Dot(reflected, hit.normal);
        if (_bounces > MaxBounces || (ground && vn < RollEnterVN))
        {
            _rolling = true;
            _vel = Vector3.ProjectOnPlane(reflected, hit.normal);
            return;
        }
        _vel = reflected;
    }

    /// <summary>贴地滑行一步（水平减速 + 撞墙贴着滑）——地面按平地假设锁定在 <see cref="ThrowableBallistics.LandY"/>。</summary>
    void RollStep()
    {
        Vector3 v = _vel; v.y = 0f;
        float speed = v.magnitude;
        if (speed < Mathf.Max(_def.stopSpeed, 0.05f)) { _settled = true; _vel = Vector3.zero; return; }

        float dt = ThrowableBallistics.SimDt;
        float step = speed * dt;
        Vector3 dir = v / speed;
        RaycastHit hit;
        if (Physics.Raycast(transform.position, dir, out hit, step, Layers.Blocker, QueryTriggerInteraction.Ignore))
        {
            // 撞墙：贴着墙面滑（去掉法向分量，并吃一次损耗），位置停在墙前
            v = Vector3.ProjectOnPlane(v, hit.normal) * 0.9f;
            transform.position += dir * Mathf.Max(hit.distance - 0.02f, 0f);
        }
        else
        {
            transform.position += dir * step;
        }

        // 水平线性减速（滑一小段就停）
        float s2 = v.magnitude;
        float dec = RollingDrag * dt;
        if (dec >= s2) { v = Vector3.zero; _settled = true; }
        else v *= (s2 - dec) / s2;

        Vector3 p = transform.position; p.y = ThrowableBallistics.LandY; transform.position = p;
        _vel = v;
    }

    /// <summary>引爆：交给效果卡（效果卡里自己负责表现 / 伤害 / 音效）；没配效果卡时给一个无伤害的响。</summary>
    void Explode()
    {
        Vector3 p = transform.position;
        if (_def.effect != null)
        {
            _def.effect.Apply(p);
        }
        else
        {
            Effects.Impact(p + Vector3.up * 0.3f, new Color(1f, 0.6f, 0.15f), 1.6f);
            Sfx.Explosion();
        }
        Destroy(gameObject);
    }
}
