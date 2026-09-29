using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 魔法①「**投射物群**」的行为 —— 参考**王者荣耀诸葛亮的被动**（用户 2026-09-28 定）。
///
/// 逻辑链（这就是全部）：
///   ① <see cref="Cast"/>：一次性生成 N 颗法球，**全部挂在玩家身上**（绕圈待命，不索敌、不攻击）；
///   ② <see cref="Tick"/>：每帧把挂着的球摆到圆环上（**被派出去的球会自动让位、剩下的重新均分**）；
///      同时每 `launchInterval` 秒**派一颗球**去做判断 ——
///        · 以玩家为圆心 `searchRadius` 内**随机**找到活敌人 → 这颗飞出去追；
///        · **没找到 → 这颗继续挂在身上**（等下个间隔再判断）。
///
/// ⚠️ 关键设计（用户特别强调）：索敌只发生在"**轮到本球的那一刻**"，不是施法时提前分配 ——
///    所以"前一颗球还在路上、目标就死了"时，后面的球**根本不会被派出**（不会追到尸体位置）。
/// ⚠️ 法球在 <see cref="MagicProjectile"/> 里自己跑飞行 / 命中 / "目标死了飞向尸体再消散"，
///    本类只管【挂载摆位 + 定时派人】两件事。
/// </summary>
[MagicName("投射物群")]
public class ProjectileGroupMagic : MagicBehaviour
{
    ProjectileMagicData _d;

    /// <summary>当前**挂在玩家身上**的法球（已经派出去的球不在这个列表里）。</summary>
    readonly List<MagicProjectile> _orbs = new List<MagicProjectile>();

    float _orbitPhase;        // 公转相位（所有挂载球共享 → 保证均匀分布 + 同步旋转）
    float _dispatchTimer;     // 距离下一次"派人"还有多久
    float _sinceDispatch;     // 距**上一次真正派出**过去了多久（派出瞬间清零）—— 给"补位延迟"用

    protected override void OnInit()
    {
        _d = data as ProjectileMagicData;
        if (_d == null)
            Debug.LogError("[ProjectileGroupMagic] 本行为需要 ProjectileMagicData 类型的卡，实际是：" +
                           (data != null ? data.GetType().Name : "null"), data);
    }

    public override bool CanCast()
    {
        if (_d == null) return false;
        // 不做"必须有敌人才让放"的限制：没敌人时法球就挂在身上转、到时限消散，
        // 这也是合理表现（而且方便调手感）。以后想限制，在这里加一句 DenyCast 即可。
        return true;
    }

    public override void Cast()
    {
        if (_d == null) return;

        if (_d.castSound != null) Sfx.Play(_d.castSound);

        ClearOrbs();     // 再按一次 E：先把上一批还挂在身上的清掉（避免叠加成一堆）

        Transform player = ResolvePlayer();
        int n = Mathf.Max(1, _d.orbCount);
        _orbitPhase = 0f;

        for (int i = 0; i < n; i++)
        {
            var orb = MagicProjectile.CreateOrbiting(_d, player, i, n, _orbitPhase);
            if (orb != null) _orbs.Add(orb);
        }

        // ⚠️ 0 = 下一个 Tick 就派第一颗（按下 E 就有反应）。
        //    用户说的是"间隔一到才开始搜寻" —— t=0 也算"第一个间隔到点"，之后严格按 launchInterval 走。
        _dispatchTimer = 0f;

        // ⚠️ 刚施法 → 视作"刚刚派出过"：这样补位延迟从 0 起算。
        //    （不影响开局：新球的 `_orbitAngle` 还是 NaN，第一次摆位会直接吸附到均分位置，不走补位逻辑。）
        _sinceDispatch = 0f;
    }

    public override void Tick(float dt)
    {
        // ① 清理已被销毁的（超时 / 命中消散的球，Unity 里会变成"假 null"）
        for (int i = _orbs.Count - 1; i >= 0; i--)
            if (_orbs[i] == null) _orbs.RemoveAt(i);

        if (_orbs.Count == 0) return;

        // ② 公转 + 摆位
        //    ⚠️ 是否【允许补位】由 `catchUpDelay` 决定（2026-09-29 用户定）：
        //       距上一次"真正派出"还不到 catchUpDelay 秒 → **先不补位**（各球守原位，空位看得见）；
        //       超过之后（说明这一波已经派完、范围内没了敌人）→ 剩下的球一起平滑补位。
        //       catchUpDelay = 0   → 老行为（立刻补位）
        //       catchUpDelay 很大  → 等价于"永不补位"（球自己 6 秒就消散，永远等不到）
        _orbitPhase += _d.orbitSpeed * Mathf.Deg2Rad * dt;
        _sinceDispatch += dt;
        bool allowCatchUp = _sinceDispatch >= _d.catchUpDelay;
        for (int i = 0; i < _orbs.Count; i++)
            _orbs[i].SetOrbit(i, _orbs.Count, _orbitPhase, allowCatchUp);

        // ③ 到点派人：让"待命最久"的那颗去搜敌
        _dispatchTimer -= dt;
        if (_dispatchTimer > 0f) return;
        // ⚠️ Max 兜底：卡上填 0 时不该变成"每帧全派完"
        _dispatchTimer = Mathf.Max(0.0001f, _d.launchInterval);

        if (_orbs[0].TryDispatch())
        {
            _orbs.RemoveAt(0);        // 打出去了 → 从身上摘掉
            _sinceDispatch = 0f;      // ⭐ 刚派出一颗 → 补位延迟**重新计时**（连续派发期间就不会补位）
        }
        // 没打到 → 继续挂着：**不**重置 `_sinceDispatch`（没有新球出去，该补位就补位）
    }

    /// <summary>卸下 / 换魔法：把还挂在身上的球全部清掉，别留下"无主法球"绕着空转。</summary>
    public override void OnUnequip() => ClearOrbs();

    void ClearOrbs()
    {
        for (int i = 0; i < _orbs.Count; i++)
            if (_orbs[i] != null) Destroy(_orbs[i].gameObject);
        _orbs.Clear();
        _dispatchTimer = 0f;
        _sinceDispatch = 0f;
    }

    /// <summary>取玩家 transform。⚠️ 宿主可能已经销毁（换魔法 / 玩家死亡）→ 兜底用自己的 transform，
    /// 不然后面 `player.position` 会抛 NullReference。</summary>
    Transform ResolvePlayer() => host != null ? host.transform : transform;
}
