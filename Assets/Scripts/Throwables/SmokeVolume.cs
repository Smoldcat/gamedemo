using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 世界里的**一片烟云**：起烟（粒子）→ 持续 → 自然消散，同时对外提供**遮挡敌人视线**的查询。
///
/// ⭐ 为什么用「静态注册表 + 线段判定」，而不是「给烟加碰撞体、把新层加进 SightMask」：
///   · **不动工程设置**：加层要改 `TagManager` + 物理碰撞矩阵，一旦没做全就会出现
///     "射线穿墙"或"玩家被烟挡住走不进去"这类怪问题（本项目 Layers 的注释专门警告过）；
///   · 烟**本来就不该有实体**：玩家 / 敌人 / 子弹都能自由进出（这是设计要的）；
///   · 与项目既有范式一致：拾取物 / 敌人 / 印记 全是「静态注册表 All」（见 `StaticReset`）；
///   · 判定**只有一个调用点**：`EnemyAI.CanSeePlayer()`（敌人视线的唯一判据）→ 那里加一行即可，
///     所有敌人行为（近战 / 远程 / 狙击 / 自爆）都吃 `seesPlayer`，**自动全覆盖**。
/// ⚠️ 本注册表必须登记到 `StaticReset`（漏登记 → 关闭 Reload Domain 时重开一局会残留上一局的死引用）。
/// ⚠️ 粒子渲染顺序：材质队列必须 **早于视野暗幕（Transparent+100）**，否则"视野外的烟不会被压暗"这条规则会被破坏。
///
/// 📌 **形状模型（2026-10-13 改：固定圆柱）**：粒子出生就铺满一个**圆柱**（半径 = `_clampR × fillRatio`、
///    高 = `smokeHeight`），不往外发散；每帧 `ClampBounds` 把粒子夹在圆柱 [0.05, smokeHeight] × `_clampR` 内
///    → 可见烟**永不出设定半径**。挡视线用的是更小的 `_blockRadius = radius × blockRatio`（圈内 90% 才挡，毛边不算）。
/// </summary>
public class SmokeVolume : MonoBehaviour
{
    /// <summary>所有存活的烟云（视线判定遍历它）。</summary>
    public static readonly List<SmokeVolume> All = new List<SmokeVolume>();

    /// <summary>静态清理（供 `StaticReset` 调用）。</summary>
    public static void ResetStatics() => All.Clear();

    /// <summary>基准半径（米）：粒子参数按 `radius / 基准` 做**面积**缩放（大白话：烟大一倍，粒子数量大概翻四倍）。</summary>
    const float BaseRadius = 4f;

    /// <summary>**同屏烟云上限**（性能护栏）：超过就**让最老的一片提前开始消散**（不是拒绝生成 —— 手感上"新烟照常出"）。
    /// ⚠️ 为什么需要：烟的代价是**填充率**（半透明大片互相重叠）。上限 = 4 是"每片都还好看"与"帧率守得住"
    ///    之间的取值（当初按经验定的护栏）。
    /// 📌 **2026-10-13「半分辨率烟」实验的实测口径**（见交接文档「最近变更」）：编辑器游戏视图 **1020×491**
    ///    下，**0 片 vs 4 片（≈1370 颗）的帧时间与 GPU 时间都测不出差别**（GPU 帧 ≈0.79ms；连"粒子面积
    ///    ×6.25"的正对照也测不出）→ **本机这个分辨率下烟不是瓶颈**（帧是 CPU 限）。
    /// ⚠️ 旧注释里写的"**实测每片 ≈ 1ms（编辑器 1020×502）**"与本次实测**对不上**，口径存疑
    ///    （很可能是 1080p 或含 CPU 粒子模拟的口径）—— **别拿它当性能基准**；真要做性能决策请按上面这条口径重测。</summary>
    const int MaxConcurrent = 4;

    /// <summary>提前消散时的"加速系数"：被顶掉的那片按剩余时间的这个比例快速收掉（1 = 立刻消失，太小会"啪"一下没了）。</summary>
    const float EvictFadeRatio = 0.25f;

    float _radius;
    float _blockRadius;   // 挡视线半径 = _radius × blockRatio（圈内 blockRatio 倍才挡，见 SmokeEffectData）

    /// <summary>[DEBUG-ONLY] **可见 / 占地半径**（米）。只读，供调试可视化 / 上层查询用。
    /// 登记见 `Assets/Debug/README.md`；发布前 `grep [DEBUG-ONLY]` 可一次找全。</summary>
    public float Radius => _radius;
    /// <summary>[DEBUG-ONLY] **挡视线半径**（米）= `Radius × blockRatio` —— 敌人真正丢视野的范围。
    /// 只读；调试的「范围指示器」画的就是它（与投掷预览 `SmokeEffectData.DangerRadius` 同一口径）。</summary>
    public float BlockRadius => _blockRadius;
    float _clampR;        // XZ 夹取半径：_radius 扣掉单颗烟最大半宽 → 可见外缘≈_radius
    float _tStopEmit;      // 停止生成粒子的时刻（之后剩下的粒子自然消散）
    float _tBlockUntil;    // **遮挡失效**的时刻（消散后段不再挡）
    float _tEnd;           // 销毁时刻
    ParticleSystem _ps;
    float _yMin;           // 粒子的**硬下限**（地面：不许沉到地下被地面挡住白画）
    float _yMax;           // 粒子的**硬上限**（天花板：不许顶到相机 —— 俯视角下这条最要紧）
    bool _evicted;         // 是否已被"同屏上限"顶掉（顶过的不再被挑中 → 保证那个循环有限次）
    static ParticleSystem.Particle[] _buf;   // 夹取用的复用缓冲（静态复用 = 零逐帧分配；纯数组、不持有场景对象 → 刻意不清）

    /// <summary>生成一片烟云（唯一入口：`SmokeEffectData.Apply`）。返回创建出来的烟云（测试 / 调试用）。</summary>
    public static SmokeVolume Spawn(Vector3 ground, SmokeEffectData cfg)
    {
        if (cfg == null) return null;
        var go = new GameObject("Smoke_" + (string.IsNullOrEmpty(cfg.effectName) ? "烟雾" : cfg.effectName));
        // ⚠️ **只取 XZ、y 一律贴地**（2026-10-13 修）：投掷物可能在**空中**引爆（保底 B：出手 8 秒必炸；
        //    或撞墙后在半空炸）——若照抄它的 y，烟会**飘在半空**，而且 Y 夹取的上下限是"地面系"的绝对高度，
        //    空中生成的烟会被硬拽下来（看起来像烟在往下沉）。地面高度沿用 `ThrowableBallistics.LandY` 的
        //    同一套假设（世界地面 y ≈ 0）。
        go.transform.position = new Vector3(ground.x, 0f, ground.z);
        var v = go.AddComponent<SmokeVolume>();
        v.Build(cfg);
        v.EvictIfTooMany();     // 同屏上限：多了就顶掉最老的一片
        return v;
    }

    /// <summary>同屏上限护栏：超过 <see cref="MaxConcurrent"/> 时，把**最老的若干片**提前消散。
    /// ⚠️ 判定用 `_tEnd`（谁结束得早谁老），只动它们自己的时间戳 —— 不影响新烟的手感。
    /// ⚠️⚠️ **实现必须"有限次循环"**：`All` 里要**等对象被销毁**才会少一项，而 `BeginEvict` 只是改时间戳、
    ///    **不会**让它立刻退出 `All` —— 所以**绝不能**写 `while (All.Count > MaxConcurrent)`（那会死循环、
    ///    直接把编辑器卡死，2026-10-13 我踩过：MCP 调用静默超时、Unity 主线程被占满）。
    ///    正确做法：按"超出的数量"顶掉有限几片，并用 <see cref="_evicted"/> 防止重复挑同一片。</summary>
    void EvictIfTooMany()
    {
        // ⚠️ 统计的是「**还活着（没被顶过）**」的片数，**不是 `All.Count`** ——
        //    被顶掉的片子要等它自然消散才会离开 `All`；若用 `All.Count`，那些"正在消散的"也会被计入，
        //    于是每来一片新烟都会再顶掉一批 → **全部被顶光**（实测 7 片里 6 片被顶，2026-10-13 修）。
        int active = 0;
        for (int i = 0; i < All.Count; i++)
        {
            var v = All[i];
            if (v != null && !v._evicted) active++;
        }
        int excess = active - MaxConcurrent;
        if (excess <= 0) return;
        for (int n = 0; n < excess; n++)
        {
            SmokeVolume oldest = null;
            for (int i = 0; i < All.Count; i++)
            {
                var v = All[i];
                if (v == null || v == this || v._evicted) continue;
                if (oldest == null || v._tEnd < oldest._tEnd) oldest = v;
            }
            if (oldest == null) return;      // 出口：除自己以外没有可顶的了（超出的只可能是自己/已顶过的）
            oldest._evicted = true;
            oldest.BeginEvict();
        }
    }

    /// <summary>提前消散：立刻停止生成，并把"结束 / 遮挡失效"时刻压缩到眼前（按 <see cref="EvictFadeRatio"/>）。</summary>
    void BeginEvict()
    {
        float now = Time.time;
        if (_tEnd - now <= 0.5f) { if (_ps != null) _ps.Stop(); _tStopEmit = now; _tBlockUntil = now; _tEnd = now + 0.5f; return; }
        float remain = (_tEnd - now) * EvictFadeRatio;
        float fade = Mathf.Min(remain, _tEnd - _tStopEmit);
        _tStopEmit = now;
        _tBlockUntil = now + fade * 0.5f;
        _tEnd = now + Mathf.Max(fade, 0.4f);
    }

    void Build(SmokeEffectData cfg)
    {
        // 纯表现层：烟**单独占一层 `Smoke`**（不在任何射线掩码里 —— 子弹 / 视线 / 绕障都打不到它；
        //   与 `Effects.SetFxLayer` 同一约定，只是从共用的 FX 层里独立出来 —— 将来烟若需要单独的
        //   渲染处理（排序 / 特殊材质 / 副相机），不会连带爆炸 / 枪口火光 / 激光 / 弹孔）。
        // ⚠️ 也不会污染视野遮罩：视野专用相机 `cullingMask = 0`（只执行命令缓冲画多边形），不渲染任何场景物体。
        // ⚠️ 主相机 `cullingMask = 全部` → 新层默认照常被渲染，**不需要改任何相机设置**。
        if (Layers.Smoke >= 0) gameObject.layer = Layers.Smoke;
        else if (Layers.FX >= 0) gameObject.layer = Layers.FX;   // 兜底：层没定义时退回 FX（ValidateOnce 会报错提示）

        _radius = Mathf.Max(cfg.radius, 0.5f);
        _blockRadius = _radius * Mathf.Clamp(cfg.blockRatio, 0.5f, 1f);   // 挡视线用：满圆的 blockRatio 倍

        // ⚠️ 缩放口径（2026-10-13 修）：`k` 只负责"**数量 / 面积**"，尺寸与漂移**不能跟着线性涨** ——
        //    否则"半径调大一倍"会变成"粒子也大一倍 + 漂得更远"，云的实际直径按平方膨胀
        //    （实测半径 7.5 时云暴到 12.2m，等于 24 米巨云）。尺寸按 `√k`、漂移/湍流按 `0.6+0.4×√k` 缩放，
        //    于是**云的视觉半径 ≈ 卡上的 radius**（口径：半径 = 覆盖范围，别再让别的因素偷偷放大它）。
        float k = Mathf.Max(_radius / BaseRadius, 0.35f);          // 面积缩放系数（数量 / 密度）
        float sizeMul = Mathf.Sqrt(k);                             // 尺寸缩放系数（温和）
        float driftMul = 0.6f + 0.4f * sizeMul;                     // 漂移 / 湍流缩放系数（更温和）
        // ⚠️ XZ 夹取半径：把"单颗烟最大半宽"从 radius 里扣掉 → 粒子中心夹在 _clampR 内时，
        //    可见外缘（中心 + 半宽）刚好≈ radius。这样无论粒子调多大，可见圆柱都不会超出设定半径。
        float maxParticleHalf = cfg.particleSize * 1.3f * sizeMul * Mathf.Max(cfg.sizeGrow, 1f) * 0.5f;
        _clampR = Mathf.Max(_radius - maxParticleHalf, 0.25f * _radius);

        _ps = gameObject.AddComponent<ParticleSystem>();
        // ⚠️ `AddComponent<ParticleSystem>()` 出来的系统**默认就在播放**，此时设 `duration` 会报
        //    "Setting the duration while system is still playing is not supported"（实测每次起烟 2 条警告）。
        //    先彻底停掉（含清空粒子）→ 再配置 → 最后 `Play()`。
        _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = _ps.main;
        main.duration = Mathf.Max(cfg.duration, 0.5f) + 1f;
        main.loop = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(Mathf.Max(cfg.particleLifetime * 0.8f, 0.5f),
                                                            Mathf.Max(cfg.particleLifetime * 1.2f, 1f));
        // ⚠️ 固定圆柱模式：driftSpeed 默认 0 → startSpeed=0，粒子**不往外飞**，主体天生就在发射盘内
        //    （发射盘 = _clampR × fillRatio，已被 XZ 夹取兜底）。想让烟"从中心慢慢涨开"再把 driftSpeed 调大。
        //    ⚠️ 旧注释说的"视觉外缘≈11m vs radius 5m"是发散小球模型的问题；圆柱模型下可见半径由 _clampR 钉在 radius 附近。
        float driftMax = Mathf.Max(cfg.driftSpeed, 0f) * driftMul;
        main.startSpeed = new ParticleSystem.MinMaxCurve(driftMax * 0.4f, driftMax);
        main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Max(cfg.particleSize * 0.7f, 0.2f) * sizeMul,
                                                        Mathf.Max(cfg.particleSize * 1.3f, 0.3f) * sizeMul);
        // ⚠️ 顶点色的 **alpha = 每片烟的透明度**（0.35 × 浓度，淡入淡出也走它）；
        //    **RGB 必须保持纯白** —— `Particles/Standard Unlit` 会把顶点色乘进输出颜色，
        //    给非白 RGB 就等于给烟染色（2026-10-13 审查：曾为已删除的 UV 着色器留过"随机灰度"，
        //    换回内置材质后变成"每片烟随机变暗"，已还原为纯白）。
        main.startColor = new ParticleSystem.MinMaxGradient(
            new Color(1f, 1f, 1f, Mathf.Clamp(0.35f * cfg.density, 0.02f, 1f)));
        // ⚠️ 重力 = **0**（2026-10-13 改）：原来给 0.02 想"轻微下沉"，实测在 5 秒寿命里会把粒子压到
        //    **地面以下**（最低 -0.66m）—— 被地面挡住 = 白算、白画（粒子浪费、看着还变薄）。
        //    俯视角下烟本来就不该"掉"，它该是贴地的一层：**不沉**。固定圆柱模型下靠发射盘铺满整段高度。
        main.gravityModifier = 0f;
        // 粒子数上限：**由发射率 × 寿命反推**（2026-10-13 修）。
        // ⚠️ 原来是独立的常数（150×k²）→ 与发射率**各说各话**：卡上写"每秒 34 颗"，上限却只允许
        //    527 颗（=95/s），发射被**静默截断**（配的参数不生效、还查不出原因）。现在两者自洽。
        float rate = Mathf.Max(cfg.emitRate, 1f) * k * k;
        float lifeMax = Mathf.Max(cfg.particleLifetime * 1.2f, 1f);
        main.maxParticles = Mathf.Max(32, Mathf.CeilToInt(rate * lifeMax) + 16);
        main.simulationSpace = ParticleSystemSimulationSpace.World;                   // 世界空间：烟不会跟着玩家跑
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.2831855f);

        var em = _ps.emission; em.rateOverTime = rate;
        var shp = _ps.shape; shp.enabled = true;
        // ⚠️ 固定圆柱：本机 Unity 的 ShapeType 没有 Cylinder 枚举，所以改用 **Box** 发射体铺满整段高度，
        //    再靠每帧 XZ 夹取把方角削成圆 → 视觉上就是圆柱。半径 = _clampR × fillRatio，高 = smokeHeight。
        //    把 GameObject 抬高 smokeHeight/2，使盒子底面贴地，粒子落在 [0, smokeHeight]（再被下方 Y 夹取兜底）。
        shp.shapeType = ParticleSystemShapeType.Box;
        float boxR = _clampR * Mathf.Clamp(cfg.fillRatio, 0.5f, 1f);
        shp.scale = new Vector3(boxR * 2f, Mathf.Max(cfg.smokeHeight, 0.1f), boxR * 2f);
        transform.position += Vector3.up * (Mathf.Max(cfg.smokeHeight, 0.1f) * 0.5f);

        var col = _ps.colorOverLifetime; col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new GradientColorKey[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new GradientAlphaKey[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.25f),
                                     new GradientAlphaKey(0.7f, 0.6f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(grad);

        // 每片烟缓慢自转（"看起来像活的"，2026-10-13 用户："做活吧"）：方向逐粒子随机，整团就在翻。
        // ⚠️⚠️ **单位实测（2026-10-13 在 Play 里量的，别照抄网上说法）**：
        //    `rotationOverLifetime.z` 的**常量值是 弧度/秒**（填 1 → 57.2958°/秒）；
        //    而 `ParticleSystem.Particle.rotation` 读出来是 **度**（实测：`startRotation = π` → 读出 180）。
        //    所以卡上的"度/秒"必须乘 `Deg2Rad` 再填进来。
        //    · 实测依据：旋转模块填 1、粒子年龄 6.587s → 读出 557.4° = 180°(起始) + 57.2958×6.587 ✓
        var spin = _ps.rotationOverLifetime;
        if (cfg.spinDegPerSec > 0.01f)
        {
            spin.enabled = true;
            float rad = cfg.spinDegPerSec * Mathf.Deg2Rad;
            spin.z = new ParticleSystem.MinMaxCurve(-rad, rad);
        }
        else spin.enabled = false;

        // 长大曲线：起点 / 中段 / 末尾都按卡上的 `sizeGrow` 换算（1.6 → 0.45 / 1.05 / 1.6，与调参前的常数一致）
        float grow = Mathf.Max(cfg.sizeGrow, 1f);
        var sz = _ps.sizeOverLifetime; sz.enabled = true;
        sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.28f * grow), new Keyframe(0.35f, 0.65f * grow), new Keyframe(1f, grow)));

        // ⚠️ 这里原来有一行**硬编码的自转**：`rot.z = new MinMaxCurve(-0.25f, 0.25f)`（≈ ±14.3°/秒）。
        //    它写在上面那个"按卡上 `spinDegPerSec` 设置"的块**之后** → 会把卡上的值**覆盖**掉
        //    （2026-10-13 排查：卡上填 10 却读出 ±0.25 就是这个原因）。自转现在**只有一个来源** = 卡。

        // ⚠️ 固定圆柱：粒子出生已铺满 [0, smokeHeight] 整段高度，**不需要再往上爬**（旧 maxRise 逻辑已删）。
        //    "活"的翻卷靠 curl 噪声模块，被每帧 ClampBounds 兜在圆柱内。
        var vel = _ps.velocityOverLifetime;
        vel.enabled = false;

        // 位置硬边界放到每帧 ClampBounds 处理（XZ 夹到 _clampR、Y 夹到 [_yMin, _yMax]），这里不再用 limitVelocity。
        var lv = _ps.limitVelocityOverLifetime;
        lv.enabled = false;

        var nz = _ps.noise;                                   // 湍流：烟自己翻卷，避免"一堆圆片同时放大"的假感
        nz.enabled = true;
        nz.strength = Mathf.Max(cfg.curl, 0f) * driftMul;
        nz.frequency = Mathf.Max(cfg.curlFrequency, 0.01f);   // 频率 / 滚动速度都在卡上（0.12 是原来的代码常量）
        nz.scrollSpeed = Mathf.Max(cfg.curlSpeed, 0f);
        nz.damping = true; nz.octaveCount = 2;

        var rnd = _ps.GetComponent<ParticleSystemRenderer>();
        // ⚠️ 用 `sharedMaterial`（不是 `material`）：后者是"取一份实例化的材质副本"的入口，
        //    多片烟各拿一份副本 = 多份材质 + 不会自动回收；这里本来就是**共用同一份材质资源**。
        rnd.sharedMaterial = cfg.material != null ? cfg.material : FallbackMaterial();
        rnd.renderMode = ParticleSystemRenderMode.Billboard;
        // ⚠️ 队列：材质资源里必须设成 3000（Transparent）——**早于**视野暗幕（Transparent+100）。
        //    （这里刻意不写 `rnd.material.renderQueue = 3000`：那会为每片烟实例化一份材质副本、且不会自动回收。）

        _ps.Play();

        // ---- 硬边界（每帧 ClampBounds 用）----
        // 圆柱底面贴地、高 smokeHeight：粒子只许呆在 [0.05, smokeHeight] 的圆柱内；
        // XZ 由 ClampBounds 夹到 _clampR（可见烟永不出设定半径），Y 夹到这段（不入地、不顶相机）。
        _yMin = 0.05f;
        _yMax = Mathf.Max(cfg.smokeHeight, 0.1f);

        float now = Time.time;
        float fade = Mathf.Max(cfg.fadeOut, 0.5f);
        _tStopEmit = now + Mathf.Max(cfg.duration, 0.5f);
        _tEnd = _tStopEmit + fade;
        _tBlockUntil = _tStopEmit + fade * Mathf.Clamp01(cfg.blockTailRatio);   // 比例在卡上（默认 0.6）
    }

    static Material _fallback;

    /// <summary>没配材质时的兜底：灰色半透明（能看清范围与时长，但是没有烟团贴图）——"资源优先、代码兜底"同一套路。
    /// ⚠️ **必须显式设成"Fade（半透明）"**：`new Material(shader)` 出来的是着色器的**默认属性**，
    ///    而 `Particles/Standard Unlit` 的默认 `_Mode = 0 (Opaque)` —— 不设就会画成**一坨不透明的方块**，
    ///    比"没贴图"还糟（2026-10-13 自查发现）。
    /// 📌 它是"纯资源型静态缓存"（不持有场景对象、跨会话残留无害）→ 按 `StaticReset` 的说明**刻意不清**。</summary>
    static Material FallbackMaterial()
    {
        if (_fallback == null)
        {
            var sh = Shader.Find("Particles/Standard Unlit");
            if (sh == null) sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            _fallback = new Material(sh);
            _fallback.color = new Color(0.85f, 0.85f, 0.88f, 1f);
            if (_fallback.HasProperty("_Mode"))
            {
                _fallback.SetFloat("_Mode", 2f);                       // 2 = Fade
                _fallback.SetOverrideTag("RenderType", "Transparent");
                _fallback.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _fallback.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _fallback.SetInt("_ZWrite", 0);
                _fallback.DisableKeyword("_ALPHATEST_ON");
                _fallback.EnableKeyword("_ALPHABLEND_ON");
            }
            _fallback.renderQueue = 3000;
        }
        return _fallback;
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void Update()
    {
        // 暂停 / 结算：游戏时间停了（Time.time 不走）→ 什么都不用做，烟也跟着冻结
        if (GameState.Paused || GameState.GameOver) return;
        float now = Time.time;
        if (_ps != null && now >= _tStopEmit && _ps.isPlaying) _ps.Stop();   // 到期：停止生成，剩下的粒子自然消散
        ClampBounds();                                                     // 硬夹圆柱边界：不出圈、不入地、不顶相机
        if (now >= _tEnd) Destroy(gameObject);
    }

    /// <summary>把每颗粒子夹在**圆柱 [0.05, smokeHeight] × 半径 _clampR** 内**（越界同时把对应速度清零，
    /// 否则它下一帧还会继续撞墙、看起来"卡在边界上抖"）。
    /// ⚠️ XZ 夹到 _clampR → 可见烟**永不出设定半径**（主体死定在限定区域，边缘只靠粒子半宽逸散一点）；
    /// ⚠️ Y 夹到 [_yMin, _yMax] → 不入地、不顶相机。
    /// ⚠️ 只在真的越界时才 `SetParticles`（平时只读一次 → 正常帧几乎零开销）；
    /// ⚠️ 缓冲是**静态复用**的（不逐帧 new，避免 GC）。</summary>
    void ClampBounds()
    {
        if (_ps == null) return;
        int count = _ps.particleCount;
        if (count <= 0) return;
        if (_buf == null || _buf.Length < count) _buf = new ParticleSystem.Particle[Mathf.Max(count, 64)];
        int n = _ps.GetParticles(_buf);
        bool changed = false;
        float r2 = _clampR * _clampR;
        float cx = transform.position.x, cz = transform.position.z;
        for (int i = 0; i < n; i++)
        {
            var p = _buf[i];
            bool hit = false;
            Vector3 pos = p.position;
            Vector3 vel = p.velocity;   // ⚠️ velocity 是只读属性返回的副本，必须整体赋值，不能写 vel.x=0
            // Y 轴：不入地、不顶过烟高
            if (pos.y < _yMin) { pos.y = _yMin; vel.y = 0f; hit = true; }
            else if (pos.y > _yMax) { pos.y = _yMax; vel.y = 0f; hit = true; }
            // XZ 轴：夹到半径 _clampR 内（圆柱边界）
            float dx = pos.x - cx, dz = pos.z - cz;
            if (dx * dx + dz * dz > r2)
            {
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                float s = _clampR / d;
                pos.x = cx + dx * s;
                pos.z = cz + dz * s;
                vel.x = 0f; vel.z = 0f; hit = true;
            }
            if (hit) { p.position = pos; p.velocity = vel; _buf[i] = p; changed = true; }
        }
        if (changed) _ps.SetParticles(_buf, n);
    }

    /// <summary>「a ↔ b 之间有没有烟」—— **唯一入口**（`EnemyAI.CanSeePlayer` 调用）。
    /// 判定口径：只用 **XZ 平面**的圆 vs 线段（俯视角下高度差不影响"看不看得见"的手感）；
    /// 只要烟圆压住线段就算挡住（多个烟云叠着也算）。
    /// ⚠️ 只在"遮挡有效期"内成立：消散后段的烟不再挡视线（见 `_tBlockUntil`）。</summary>
    public static bool BlocksLine(Vector3 a, Vector3 b)
    {
        for (int i = 0; i < All.Count; i++)
        {
            var v = All[i];
            if (v == null) continue;
            if (Time.time >= v._tBlockUntil) continue;      // 还没开始 / 已经失效（0 初值 → 天然不挡）
            if (v.SegmentHits(a, b)) return true;
        }
        return false;
    }

    /// <summary>烟圆是否压住"a → b"这条线段（XZ 平面：点到线段最近距离 ≤ 半径）。
    /// ⚠️ 用的是 `_blockRadius`（= radius × blockRatio），圈内 blockRatio 倍才挡，外圈毛糙不算。</summary>
    bool SegmentHits(Vector3 a, Vector3 b)
    {
        Vector2 c = new Vector2(transform.position.x, transform.position.z);
        Vector2 p1 = new Vector2(a.x, a.z);
        Vector2 p2 = new Vector2(b.x, b.z);
        Vector2 d = p2 - p1;
        float len2 = d.sqrMagnitude;
        float t = len2 > 1e-4f ? Mathf.Clamp01(Vector2.Dot(c - p1, d) / len2) : 0f;
        Vector2 closest = p1 + d * t;
        return (c - closest).sqrMagnitude <= _blockRadius * _blockRadius;
    }
}
