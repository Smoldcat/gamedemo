using UnityEngine;

/// <summary>
/// 投掷物弹道（**唯一实现**）：轨迹预览与真实飞行**共用同一份"求解 + 步进"** ——
/// 这是"按住右键看到的轨迹 == 真扔出去的结果"（所见即所得）的技术底座（用户 2026-10-12 定）。
/// ⚠️ 谁都不许在别处再写一份抛物线 / 反射公式：预览（`PlayerThrowable`）与飞行（`ThrownGrenade`）都只调本类。
///
/// 抛物线口径（**乙方案：单条真抛物线**，用户 2026-10-12 选定）：
///   · 起点 = 出手点（手上，离地约 1.2m）；终点 = 落点（准星地面点，超最大距离时被调用方夹取）；
///   · **峰值高度 = 定值**（每类投掷物的卡上参数，绝对离地高度）；
///   · 数学上"起点比终点高"会让顶点**自动偏前几 %**（3m 峰值 / 10m 投掷偏约 0.6m）——
///     把曲线顺着手的方向往回延长，它就是一条"顶点在正中"的对称抛物线（用户已确认此理解）。
///   · 步进用**常加速度闭式解**（不是欧拉近似）：真飞行与预览逐点一致，落点 = 解出的落点。
/// </summary>
public static class ThrowableBallistics
{
    /// <summary>出手点离地高度（米）：起点比地面高这么多 → 顶点自动偏前的原因。</summary>
    public const float HandHeight = 1.2f;

    /// <summary>出手点相对玩家的**前向**偏移（米）：别从身体正中心起飞（看起来才像从手里扔出去）。</summary>
    public const float HandForward = 0.35f;

    /// <summary>落点平面高度（米）：手雷停稳后球心离地的高度（= 球半径，见 <see cref="BallRadius"/>）。</summary>
    public const float LandY = 0.10f;

    /// <summary>球体半径（米）：既当物理"皮肤厚度"（撞点回退这么多，球心停在表面上方），也当占位球尺寸。</summary>
    public const float BallRadius = 0.10f;

    /// <summary>最小投掷距离（米）：准星贴着自己时也不小于它（防"原地垂直起降"）。</summary>
    public const float MinDistance = 1.5f;

    /// <summary>模拟步长（秒）：预览与飞行都按它固定步进 —— 两边逐点一致的关键。</summary>
    public const float SimDt = 1f / 120f;

    /// <summary>预览最长模拟时间（秒）：够覆盖最大投掷距离 + 余量（防死循环）。</summary>
    public const float MaxPreviewTime = 4f;

    /// <summary>求解结果：出手速度 + 等效重力（都由"起点 / 落点 / 峰值 / 速度"反解出来）。</summary>
    public struct Solve
    {
        public Vector3 v0;        // 出手速度（世界向量，含方向与大小）
        public float gravity;     // 等效重力（正数，向下）
        public float flightTime;  // 到落点的理论飞行时间（秒）
    }

    /// <summary>
    /// 反解弹道（垂直平面内推导，x = 水平距离）：
    ///   y(x) = h0 + (2k/xv)·x − (k/xv²)·x²        （k = 峰值高 − 出手高 &gt; 0）
    ///   顶点水平位置  xv = d·(−k + √(k(k+Δ))) / Δ  （Δ = 出手高 − 落点高 &gt; 0，恒有 0 &lt; xv &lt; d）
    ///   等效重力 g = 2k·vx² / xv²；竖直初速 vy0 = 2k·vx / xv；水平速度 vx = 出手速度（全程匀速）
    /// ⚠️ 调用方需先把 `target` 夹到 [MinDistance, maxThrowDistance]（本函数只做数值兜底）。
    /// </summary>
    public static Solve SolveThrow(Vector3 origin, Vector3 target, float peakHeight, float speed, Vector3 fallbackDirH)
    {
        Vector3 flat = target - origin; flat.y = 0f;
        float d = flat.magnitude;
        Vector3 dirH = d > 0.0001f ? flat / d : fallbackDirH;
        if (dirH.sqrMagnitude < 0.0001f) dirH = Vector3.forward;
        dirH.y = 0f; dirH.Normalize();
        d = Mathf.Max(d, MinDistance);

        float h0 = origin.y;
        float delta = Mathf.Max(h0 - LandY, 0.05f);
        float k = Mathf.Max(peakHeight, h0 + 0.3f) - h0;      // > 0：峰值必须高于出手点

        float xv = d * (-k + Mathf.Sqrt(k * (k + delta))) / delta;
        xv = Mathf.Clamp(xv, 0.2f * d, 0.95f * d);            // 浮点兜底

        float vx = Mathf.Max(speed, 2f);
        float vy0 = 2f * k * vx / xv;
        float g = 2f * k * vx * vx / (xv * xv);

        Solve s;
        s.v0 = dirH * vx + Vector3.up * vy0;
        s.gravity = g;
        s.flightTime = d / vx;
        return s;
    }

    /// <summary>
    /// 推进一个步长（常加速度闭式解，位置没有欧拉近似误差）：先算位移，再用一条射线看这一步会不会撞。
    /// · 撞到 → `newPos` = **精确撞点** + 法向回退 `BallRadius`（球心停在表面外），返回 true
    /// · 没撞 → `newPos` = 当前位置 + 位移，返回 false
    /// ⚠️ 撞点的"皮肤厚度"就是球半径（薄墙也不会被穿过去：射线长度 = 本步位移）。
    /// </summary>
    public static bool Step(Vector3 pos, Vector3 vel, float gravity, float dt, LayerMask mask,
                            out Vector3 newPos, out Vector3 newVel, out RaycastHit hit)
    {
        Vector3 gVec = Vector3.down * gravity;
        Vector3 delta = vel * dt + gVec * (0.5f * dt * dt);
        newVel = vel + gVec * dt;
        hit = default(RaycastHit);

        float len = delta.magnitude;
        if (len <= 0.000001f) { newPos = pos; return false; }

        Vector3 dir = delta / len;
        if (Physics.Raycast(pos, dir, out hit, len, mask, QueryTriggerInteraction.Ignore))
        {
            newPos = hit.point + hit.normal * BallRadius;
            return true;
        }
        newPos = pos + delta;
        return false;
    }

    /// <summary>衰减弹性碰撞：速度按法向 / 切向分解 —— 法向**反向**并按 `normalRetain` 反弹（撞哪弹哪），
    /// 切向按 `tangentialRetain` 保留（摩擦）。</summary>
    public static Vector3 Reflect(Vector3 vel, Vector3 normal, float normalRetain, float tangentialRetain)
    {
        float vn = Vector3.Dot(vel, normal);
        Vector3 normalPart = normal * vn;
        Vector3 tangentPart = vel - normalPart;
        return -normalPart * Mathf.Clamp01(normalRetain) + tangentPart * Mathf.Clamp01(tangentialRetain);
    }

    /// <summary>这一步撞到的东西算不算"地面"（法向朝上）—— 主引信（碰地面才点火）的判据。</summary>
    public static bool IsGround(RaycastHit hit) => hit.normal.y > 0.5f;
}
