using UnityEngine;

/// <summary>
/// 魔法②「**条件斩杀**」的行为（被动魔法）。
///
/// 它本身只做一件小事：**玩家命中敌人时，给那个敌人挂一个 <see cref="MagicMark"/>**
/// （已有标记则刷新持续时长）。真正的"等条件满足 → 触发效果"全在那个标记里 ——
/// 因为标记要**活在敌人身上**，而魔法是活在玩家身上的，两者生命周期不同。
///
/// ⚠️ **成对纪律**：<see cref="OnUnequip"/> 必须把场上由本魔法挂的标记全部撤掉，
///    否则卸下魔法后，之前挂的印记还会继续生效（"幽灵效果"）。
/// </summary>
[MagicName("条件斩杀")]
public class ConditionMagic : MagicBehaviour
{
    ConditionMagicData _d;

    protected override void OnInit()
    {
        _d = data as ConditionMagicData;
        if (_d == null)
            Debug.LogError("[ConditionMagic] 本行为需要 ConditionMagicData 类型的卡，实际是：" +
                           (data != null ? data.GetType().Name : "null"), data);
    }

    public override void OnEquip()
    {
        // 被动魔法：装备即生效，不需要写什么 —— 命中时会由 PlayerMagic 转发到这里。
        // （留这个空实现是为了让"这里本该有钩子"这件事显式可见，而不是让人以为漏写了。）
    }

    /// <summary>玩家命中敌人（由 <see cref="PlayerMagic.NotifyHitEnemy"/> 转发，来源是 <see cref="Combat"/>）。
    /// 给敌人挂上/刷新印记。
    ///
    /// ⭐ **冷却中不挂**（用户 2026-09-29 定的方案 B）。为什么这样定：
    ///    · 本技能定位是**收尾斩杀**，不是"给敌人挂负面效果"；
    ///    · `markDuration` 已经压到 1 秒（比 `cooldown` 短）→ 冷却期间挂上的印记
    ///      **必然在冷却结束前就消散**，等于白挂（还多一次 AddComponent + 销毁）；
    ///    · 一次只能斩一个人 → 场上多留几个印记**没有实际价值**。
    /// ⚠️ 注意这里只挡"**新挂**"：冷却**开始前**已经挂上的印记**不主动撤** ——
    ///    它们才是"冷却一好立刻斩"的候补（这才是多印记唯一有价值的场景）。</summary>
    public override void OnPlayerHitEnemy(Health enemy, bool headshot)
    {
        if (_d == null || enemy == null || !enemy.alive) return;
        if (enemy.team != Health.Team.Enemy) return;
        if (host != null && !host.PassiveReady) return;      // ⭐ 冷却中 → 不挂新印记

        var mark = enemy.GetComponent<MagicMark>();
        if (mark == null) mark = enemy.gameObject.AddComponent<MagicMark>();
        mark.Attach(_d, host);      // ⚠️ 每次命中都调 = 刷新持续时长（用户 2026-09-28 定）
    }

    /// <summary>卸下 / 被替换 / 丢弃时：把场上由本魔法挂的印记全部撤掉（成对清理）。</summary>
    public override void OnUnequip()
    {
        MagicMark.RemoveAllFrom(host);
    }
}
