using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 世界里的一片**火焰区域**（2026-10-16 新增）：落地点开始燃烧 → 持续一段时间（火焰粒子 + 每跳伤害）→ 自然烧完。
///
/// 沿用 `SmokeVolume` 的那套**范式**（这是本项目"持续区域"的标准形状）：
///   · **静态注册表 `All`**（+ `StaticReset` 登记，否则关掉 Reload Domain 会残留死引用）；
///   · **不写碰撞体、不进任何射线掩码** → 单位 / 子弹 / 视线**自由穿过**（火只伤害，不挡路、不挡视线 —— 用户 2026-10-16 定）；
///   · 伤害**自己用几何 + 射线算**（`Vector3` 距离 + `Combat.HasClearPath`），不指望物理触发；
///   · **贴地生成**（只取 XZ、y=0，与烟云同一口径 —— 投掷物可能在空中引爆）；
///   · **同屏上限**（性能护栏）。
///
/// ⚠️ 与烟云的差异（刻意保留）：火**不挡视线**、**不夹取粒子位置**（火本就该往上窜）、
///    只保留"发射盘 + 寿命 + 变色 + 上窜"这几项最像火的。
/// ⚠️ 每跳伤害都会**弹一次伤害飘字**（`Health.TakeDamage` 的既有行为）→ 卡上 `tickInterval` 别调太短。
/// </summary>
public class FireZone : MonoBehaviour
{
    /// <summary>所有存活火焰区域（调试 / 上限统计用）。</summary>
    public static readonly List<FireZone> All = new List<FireZone>();

    /// <summary>静态清理（供 `StaticReset` 调用）。</summary>
    public static void ResetStatics() => All.Clear();

    /// <summary>同屏上限（性能护栏）：超过就把**最老的一片**直接销毁（不是拒绝生成）。
    /// ⚠️ 实现必须**有限次**：`Destroy` 是帧末才生效的，所以这里"先 `All.Remove` 再 Destroy"
    ///    让列表**立刻**变短，循环必然终止（烟云那次写 `while (All.Count > Max)` 把编辑器卡死过，见 `SmokeVolume` 注释）。</summary>
    const int MaxConcurrent = 4;

    float _radius;
    float _damagePerTick;
    float _tickInterval;
    float _selfMul;
    float _tStopEmit;      // 停止发射的时刻（之后残留火焰自然烧完）
    float _tEnd;           // 销毁时刻
    float _nextTick;       // 下一跳结算时刻
    ParticleSystem _ps;
    Material _ownMat;      // 卡上没配材质时自己建的兜底材质 → 成对销毁
    Health _playerHp;      // 玩家血量（懒取一次，避免每跳 GetComponent）
    readonly List<EnemyAI> _snap = new List<EnemyAI>(32);   // 伤害结算用的敌人快照（复用，零逐帧分配）

    /// <summary>生成一片火焰区域（唯一入口：`FlameEffectData.Apply`）。</summary>
    public static FireZone Spawn(Vector3 ground, FlameEffectData cfg)
    {
        if (cfg == null) return null;
        var go = new GameObject("Fire_" + (string.IsNullOrEmpty(cfg.effectName) ? "火焰" : cfg.effectName));
        // ⚠️ 只取 XZ、y 一律贴地（与烟云同一口径）：投掷物可能在**空中**引爆，跟着它的 y 会"火飘在半空"。
        go.transform.position = new Vector3(ground.x, 0f, ground.z);
        var z = go.AddComponent<FireZone>();
        z.Build(cfg);
        z.EnforceLimit();
        return z;
    }

    void Build(FlameEffectData cfg)
    {
        All.Add(this);

        // 纯表现层：归 FX 层（与爆炸 / 枪口火光同一族）。它**不在任何射线掩码里**、也没有碰撞体
        // → 子弹 / 视线 / 绕障都打不到它（这是"火不挡视线"的实现方式，不需要额外代码）。
        if (Layers.FX >= 0) gameObject.layer = Layers.FX;

        _radius = Mathf.Max(cfg.radius, 0.5f);
        _damagePerTick = Mathf.Max(cfg.damagePerTick, 0f);
        _tickInterval = Mathf.Max(cfg.tickInterval, 0.1f);
        _selfMul = Mathf.Clamp(cfg.selfDamageMultiplier, 0f, 2f);

        float now = Time.time;
        _tStopEmit = now + Mathf.Max(cfg.duration, 0.5f);
        _tEnd = _tStopEmit + Mathf.Max(cfg.fadeOut, 0.2f);
        _nextTick = now + _tickInterval;      // 第一跳等一个间隔（起燃有个"烧起来"的过程）

        // 面积缩放：半径相对基准（3.5m）的倍数，**数量按面积**放大（与烟云同一口径）
        float k = Mathf.Max(_radius / 3.5f, 0.4f);
        float rate = Mathf.Max(cfg.emitRate, 1f) * k * k;
        float lifeMin = Mathf.Max(cfg.particleLifetime * 0.75f, 0.1f);
        float lifeMax = Mathf.Max(cfg.particleLifetime * 1.25f, 0.2f);

        _ps = gameObject.AddComponent<ParticleSystem>();
        // ⚠️ `AddComponent` 出来的粒子系统**默认就在播放**，此时设 `duration` 会报
        //    "Setting the duration while system is still playing" → 先彻底停掉（含清空）再配置，最后 Play。
        _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = _ps.main;
        main.duration = Mathf.Max(cfg.duration + cfg.fadeOut, 1f);
        main.loop = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifeMin, lifeMax);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.15f);          // 出生速度几乎为 0：往上窜靠 velocity 模块
        float sizeMul = Mathf.Sqrt(k);                                           // 尺寸按 √k（温和，别让"半径大"变成"火也巨大"）
        main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Max(cfg.particleSize * 0.7f, 0.1f) * sizeMul,
                                                       Mathf.Max(cfg.particleSize * 1.3f, 0.2f) * sizeMul);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 1f, 1f, 0.9f));
        main.gravityModifier = 0f;                                              // 不做重力：上窜由速度模块给
        main.maxParticles = Mathf.Max(48, Mathf.CeilToInt(rate * lifeMax) + 16);
        main.simulationSpace = ParticleSystemSimulationSpace.World;             // 世界空间：火不跟着玩家跑
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.2831855f);    // 每片随机朝向

        var em = _ps.emission;
        em.rateOverTime = rate;

        // 发射盘：一片**贴地的薄盒**（本机 Unity 的 ShapeType 没有 Cylinder 枚举，用薄 box 当圆盘 —— 与烟云同理）
        var shp = _ps.shape;
        shp.enabled = true;
        shp.shapeType = ParticleSystemShapeType.Box;
        float boxR = _radius * 0.9f;
        shp.scale = new Vector3(boxR * 2f, 0.12f, boxR * 2f);
        shp.position = new Vector3(0f, 0.06f, 0f);                              // 抬半个盒高 → 底面贴地

        // 火色：出生白黄（最热）→ 橙 → 尾巴暗红；alpha 两头淡（火苗的"舔"感）
        var col = _ps.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new GradientColorKey[]
            {
                new GradientColorKey(new Color(1f, 0.95f, 0.62f), 0f),
                new GradientColorKey(new Color(1f, 0.58f, 0.18f), 0.45f),
                new GradientColorKey(new Color(0.70f, 0.15f, 0.04f), 1f),
            },
            new GradientAlphaKey[]
            {
                new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.18f),
                new GradientAlphaKey(0.55f, 0.7f), new GradientAlphaKey(0f, 1f),
            });
        col.color = new ParticleSystem.MinMaxGradient(grad);

        // 尺寸：先长大一点、末尾缩小（火舌收梢）
        var sz = _ps.sizeOverLifetime;
        sz.enabled = true;
        sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.55f), new Keyframe(0.35f, 1f), new Keyframe(1f, 0.35f)));

        // 自转：让每片火翻动（"像活的"）。⚠️ 这里是**弧度/秒**（2026-10-13 实测口径，别填"度"）
        var rot = _ps.rotationOverLifetime;
        rot.enabled = true;
        float rad = 26f * Mathf.Deg2Rad;
        rot.z = new ParticleSystem.MinMaxCurve(-rad, rad);

        // 上窜：火苗往上舔（这是"火"和"贴纸"的关键差别）
        var vel = _ps.velocityOverLifetime;
        vel.enabled = cfg.riseSpeed > 0.01f;
        if (vel.enabled)
        {
            vel.space = ParticleSystemSimulationSpace.World;
            vel.y = new ParticleSystem.MinMaxCurve(cfg.riseSpeed * 0.7f, cfg.riseSpeed * 1.3f);
        }

        // 抖动：火舌左右摇摆（幅度随半径温和放大）
        var nz = _ps.noise;
        nz.enabled = true;
        nz.strength = 0.22f * Mathf.Max(k, 0.5f);
        nz.frequency = 0.7f;
        nz.scrollSpeed = new ParticleSystem.MinMaxCurve(0.45f);

        // 材质：卡上配了就用它（推荐 `Assets/Art/FX/火焰.mat`）；没配 → 代码生成的火焰贴图 + 加法混合兜底
        var pr = _ps.GetComponent<ParticleSystemRenderer>();
        pr.renderMode = ParticleSystemRenderMode.Billboard;
        if (cfg.material != null) pr.material = cfg.material;
        else
        {
            _ownMat = FireVisual.CreateMaterial(FireVisual.Texture());
            pr.material = _ownMat;
        }

        _ps.Play();
    }

    void Update()
    {
        float now = Time.time;

        if (now <= _tStopEmit)
        {
            if (now >= _nextTick) { _nextTick = now + _tickInterval; ApplyDamageTick(); }
        }
        else if (_ps != null && _ps.isPlaying)
        {
            _ps.Stop();          // 停止发射；已生成的火焰自然烧完（fadeOut 就是给它收尾的时间）
        }

        if (now >= _tEnd) Destroy(gameObject);
    }

    /// <summary>一跳伤害：圈内敌人 + 造火者自己（**隔墙不烧**，与爆炸同一口径）。</summary>
    void ApplyDamageTick()
    {
        if (GameState.GameOver) return;                       // 结算态不再结算（与其它系统一致）
        if (_damagePerTick <= 0f && _selfMul <= 0f) return;

        Vector3 c = transform.position;
        float rSq = _radius * _radius;

        // 敌人：⚠️ **必须先快照**（死亡会立刻从 `All` 移除 → 边遍历边改会中断循环）
        _snap.Clear();
        var all = EnemyAI.All;
        for (int i = 0; i < all.Count; i++)
            if (all[i] != null) _snap.Add(all[i]);

        if (_damagePerTick > 0f)
        {
            for (int i = 0; i < _snap.Count; i++)
            {
                var e = _snap[i];
                var hp = e.HealthRef;
                if (hp == null || !hp.alive) continue;

                Vector3 ep = e.transform.position;
                float dx = ep.x - c.x, dz = ep.z - c.z;
                if (dx * dx + dz * dz > rSq) continue;                     // XZ 圆：不在圈内
                if (!Combat.HasClearPath(c, ep, e.transform)) continue;    // ⚠️ 隔墙不烧（与爆炸同一判据）

                Vector3 dir = ep - c; dir.y = 0f;
                hp.TakeDamage(_damagePerTick, dir.normalized, false, true);   // creditKill = true：烧死算玩家的
            }
        }

        // 自伤（用户 2026-10-16 定：烧得到自己）——creditKill = false（烧死自己不算"击杀"）
        if (_selfMul > 0f)
        {
            var pc = PlayerController.Instance;
            if (pc != null)
            {
                if (_playerHp == null) _playerHp = pc.GetComponent<Health>();
                if (_playerHp != null && _playerHp.alive)
                {
                    Vector3 pp = pc.transform.position;
                    float dx = pp.x - c.x, dz = pp.z - c.z;
                    if (dx * dx + dz * dz <= rSq && Combat.HasClearPath(c, pp, pc.transform))
                    {
                        Vector3 dir = pp - c; dir.y = 0f;
                        _playerHp.TakeDamage(_damagePerTick * _selfMul, dir.normalized, false, false);
                    }
                }
            }
        }
    }

    /// <summary>同屏上限：多了就销毁最老的几片（**先移出 `All` 再 Destroy** → 列表立刻变短，循环必然终止）。</summary>
    void EnforceLimit()
    {
        int excess = All.Count - MaxConcurrent;
        for (int n = 0; n < excess; n++)
        {
            FireZone oldest = null;
            for (int i = 0; i < All.Count; i++)
            {
                var z = All[i];
                if (z == null || z == this) continue;
                if (oldest == null || z._tEnd < oldest._tEnd) oldest = z;
            }
            if (oldest == null) return;          // 出口：除自己外没有可销毁的了
            All.Remove(oldest);                  // ⚠️ 先移除（Destroy 帧末才生效，不移除循环就退不出来）
            Destroy(oldest.gameObject);
        }
    }

    void OnDestroy()
    {
        All.Remove(this);
        if (_ownMat != null) Destroy(_ownMat);   // 谁创建谁销毁（卡上配的材质不归我们）
    }
}
