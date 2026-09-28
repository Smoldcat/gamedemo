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
    }

    public override void Tick(float dt)
    {
        // ① 清理已被销毁的（超时 / 命中消散的球，Unity 里会变成"假 null"）
        for (int i = _orbs.Count - 1; i >= 0; i--)
            if (_orbs[i] == null) _orbs.RemoveAt(i);

        if (_orbs.Count == 0) return;

        // ② 公转 + 把每颗球摆到"当前剩余球数"均分的位置上
        //    ⚠️ 每帧重算 index/count → 有球被派出去后，剩下的**自动重新均分圆环**（诸葛亮被动的观感）
        _orbitPhase += _d.orbitSpeed * Mathf.Deg2Rad * dt;
        for (int i = 0; i < _orbs.Count; i++)
            _orbs[i].SetOrbit(i, _orbs.Count, _orbitPhase);

        // ③ 到点派人：让"待命最久"的那颗去搜敌
        _dispatchTimer -= dt;
        if (_dispatchTimer > 0f) return;
        // ⚠️ Max 兜底：卡上填 0 时不该变成"每帧全派完"
        _dispatchTimer = Mathf.Max(0.0001f, _d.launchInterval);

        if (_orbs[0].TryDispatch()) _orbs.RemoveAt(0);   // 打出去了 → 从身上摘掉；没打到 → 继续挂着
    }

    /// <summary>卸下 / 换魔法：把还挂在身上的球全部清掉，别留下"无主法球"绕着空转。</summary>
    public override void OnUnequip() => ClearOrbs();

    void ClearOrbs()
    {
        for (int i = 0; i < _orbs.Count; i++)
            if (_orbs[i] != null) Destroy(_orbs[i].gameObject);
        _orbs.Clear();
        _dispatchTimer = 0f;
    }

    /// <summary>取玩家 transform。⚠️ 宿主可能已经销毁（换魔法 / 玩家死亡）→ 兜底用自己的 transform，
    /// 不然后面 `player.position` 会抛 NullReference。</summary>
    Transform ResolvePlayer() => host != null ? host.transform : transform;
}
