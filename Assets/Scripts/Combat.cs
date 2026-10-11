using UnityEngine;

/// <summary>命中结算的形态。**加新弹种 = 在这里加一个枚举值 + `Combat.ResolveHit` 里加一个 case**，不用动 `Bullet`。</summary>
public enum HitMode
{
    /// <summary>直击伤害（可爆头）</summary>
    Direct,
    /// <summary>命中 / 到期后爆炸，造成范围伤害</summary>
    Explode,
    // 将来可加：Pierce（穿透）、Chain（连锁）、Burn（燃烧）…
}

/// <summary>一次命中结算的结果 —— 告诉子弹接下来该怎么办</summary>
public enum HitResult
{
    /// <summary>穿过去（友军 / 尸体）：子弹继续飞</summary>
    PassThrough,
    /// <summary>已结算（打墙 / 打中敌人 / 爆炸）：子弹该回收了</summary>
    Consumed,
}

/// <summary>
/// 一颗子弹携带的全部伤害参数：由 <see cref="Bullet"/> 在生成时按 `WeaponData` 填好，命中时交给 <see cref="Combat"/> 结算。
/// 把这些从 `Bullet` 的字段里独立出来，是为了让"命中后干什么"集中在一处 —— 加新弹种不用动 `Bullet`。
/// </summary>
public struct ShotProfile
{
    public HitMode mode;
    public float damage;              // 直击伤害
    public float effectiveRange;      // 有效射程（米）：**≤ 0 = 不衰减**（见 Combat.DamageAtDistance）
    public float beyondRangeDamage;   // 超出有效射程后的伤害倍率（0~1，1 = 不衰减）
    public float explosionRadius;     // 爆炸半径（mode = Explode 且 > 0 时生效）
    public float explosionDamage;     // 爆炸对范围内敌人的伤害
    public Health.Team team;          // 阵营：只打另一边；友军不挡子弹
    public Color color;               // 命中特效颜色（= 武器曳光色）
    public EnemyAI headshotTarget;    // 开火瞬间锁定的爆头目标（**只有打到它**才吃倍率）
    public float headshotMultiplier;  // 爆头倍率
}

/// <summary>
/// **命中结算层**：把"命中了一个东西之后发生什么"从 `Bullet` 里抽出来，集中成一处的规则。
///
/// 为什么要有这一层：`Bullet` 原来同时管 运动 / 射线 / 命中分类 / 伤害 / 爆头 / AOE / 特效 ——
/// 想加"穿透弹 / 弹片 / 燃烧"就必须进 `Bullet` 里加 if，改一次就可能碰坏现有弹种。
/// 现在 `Bullet` 只负责"移动 + 打一条射线"，命中后交给这里（`ResolveHit`）。
///
/// ⚠️ **规则只能有一份实现**，都只在本类里出现，不要复制到别处：
///   ① 隔墙不掉血（`HasClearPath`，自爆兵 AOE 也复用这里）；
///   ② 爆头只认"开火瞬间锁定的那个目标"；
///   ③ **爆炸 AOE 不参与爆头**（范围伤害统一结算）；
///   ④ **有效射程 → 伤害衰减**（`DamageAtDistance`；玩家子弹与敌人子弹共用这一份，
///      **爆炸 AOE 不受它影响** —— 爆炸走自己的半径规则）。
/// </summary>
public static class Combat
{
    /// <summary>爆炸 AOE 的快照缓冲（复用同一个 List，避免每次爆炸都 new 一个 → GC 峰值）。
    /// 纯容器、每次用前 Clear，跨会话残留无害，不需要 StaticReset 登记。</summary>
    static readonly System.Collections.Generic.List<EnemyAI> _aoeSnapshot = new System.Collections.Generic.List<EnemyAI>();

    /// <summary>
    /// **有效射程 → 伤害倍率**（唯一实现）：飞行距离在 `shot.effectiveRange` 之内 = 原伤害；
    /// 超出 = `damage × beyondRangeDamage`。`effectiveRange ≤ 0` 或 `beyondRangeDamage ≥ 1` = **不衰减**（旧行为）。
    /// ⚠️ 玩家子弹与敌人子弹**共用这一份**（差别只在数据来源：`WeaponData` / `EnemyConfig`）。
    /// 📌 当前是**阶梯**：临界点上会有伤害跳变（射程内 100%、射程外一律 ×倍率）。
    ///    将来要升级成"从有效射程到 X 倍距离之间线性滑落"，**只改这个函数** ——
    ///    调用方（`Bullet`）与数据结构（`ShotProfile` / 两侧配置）都不用动，这正是把它单独抽出来的原因。
    /// </summary>
    public static float DamageAtDistance(ShotProfile shot, float traveledDistance)
    {
        // 关闭态（默认）走零成本快路径：绝大多数子弹连一次比较都省了
        if (shot.effectiveRange <= 0f || shot.beyondRangeDamage >= 1f) return shot.damage;
        if (traveledDistance <= shot.effectiveRange) return shot.damage;
        return shot.damage * Mathf.Clamp01(shot.beyondRangeDamage);
    }

    /// <summary>
    /// 结算一次命中。`dir` 是子弹的归一化方向（用作受击来向）。
    /// `traveledDistance` = 子弹从出膛到命中点飞了多远（米），供「有效射程衰减」用
    /// （由 `Bullet` 在命中那一帧用"出膛点 → 命中点"量一次，不是每帧累计）。
    /// ⚠️ 这个参数**刻意不给默认值**：它必须是调用方真的量出来的距离，
    ///    给个 `0` 的默认值会让将来新增的弹种静默地"永不衰减"（又一种不报错的坑）。
    /// 返回 `Consumed` = 子弹应被回收；`PassThrough` = 子弹继续飞（友军 / 尸体）。
    /// </summary>
    public static HitResult ResolveHit(RaycastHit hit, ShotProfile shot, Vector3 dir, float traveledDistance)
    {
        // ① 爆炸弹：命中即爆（直击与范围伤害统一走爆炸）
        if (shot.mode == HitMode.Explode && shot.explosionRadius > 0f)
        {
            Explode(hit.point, shot);
            return HitResult.Consumed;
        }

        // ② 墙 / 障碍物（没有 Health）：出火花，子弹结束
        var h = hit.collider.GetComponentInParent<Health>();
        if (h == null)
        {
            Effects.Impact(hit.point, shot.color, 0.5f);
            return HitResult.Consumed;
        }

        // ③ 尸体 / 友军：穿过去（既不当墙挡子弹，也不扣血）
        if (!h.alive || h.team == shot.team) return HitResult.PassThrough;

        // ④ 打中敌人：**只有命中"开火瞬间锁定的那个敌人"才算爆头**（打中挡枪的别人 = 普通伤害）
        bool headshot = shot.headshotTarget != null
                        && hit.collider.GetComponentInParent<EnemyAI>() == shot.headshotTarget;
        // 有效射程衰减作用在"这一发的最终伤害"上（爆头再在这个基数上乘倍率 —— 乘法可交换，关键是别只在非爆头那支上算）
        float dmg = DamageAtDistance(shot, traveledDistance);
        h.TakeDamage(headshot ? dmg * shot.headshotMultiplier : dmg, dir, headshot);
        if (h.team == Health.Team.Enemy) Crosshair.HitMark(headshot);   // 爆头命中 → 播爆头反馈音
        Effects.Impact(hit.point, shot.color, 0.9f);

        // 魔法系统的**被动钩子**（2026-09-28）：玩家打中敌人时，通知玩家身上的被动魔法（例：印记斩杀）。
        // ⚠️ 放在 `TakeDamage` **之后**：此时血量已是新值，"血量是否低于阈值"的判定才准确。
        // ⚠️ 只在**玩家开火**时转发（敌人子弹不该触发玩家的被动魔法）。
        // ⚠️ `h.alive` —— 已死的就不用挂印记了。
        if (shot.team == Health.Team.Player && h.team == Health.Team.Enemy && h.alive)
        {
            // ⚠️ 用 `!= null`（Unity 的 == 重载认"假 null"），**不要**写 `Instance?.`：
            //    `?.` 走的是引用比较，销毁后到 OnDestroy 清空之间的窗口里会调进已销毁对象（2026-10-10 修）
            var pm = PlayerMagic.Instance;
            if (pm != null) pm.NotifyHitEnemy(h, headshot);
        }

        return HitResult.Consumed;
    }

    /// <summary>爆炸：表现 + 范围伤害（隔墙不掉血）。**爆炸不参与爆头**（范围伤害统一结算）。</summary>
    public static void Explode(Vector3 point, ShotProfile shot)
    {
        // 表现：大火球 + 白色冲击 + 爆炸范围圈
        Effects.Impact(point + Vector3.up * 0.3f, new Color(1f, 0.6f, 0.15f), 2.6f);
        Effects.Impact(point + Vector3.up * 0.3f, new Color(0.95f, 0.95f, 0.9f), 1.8f);
        Effects.BlastRing(point, shot.explosionRadius);
        Sfx.Explosion();   // 爆炸声（自爆兵在它自己的 Explode 里调同一份实现）

        if (shot.team == Health.Team.Player)
        {
            bool hitAny = false;   // 一个敌人都没炸到（打空）时不该播命中反馈，否则"叮"一声会让人以为打中了
            // 先快照再遍历：伤害可能致死 → EnemyAI.OnDead → All.Remove，直接枚举会抛异常
            _aoeSnapshot.Clear();
            _aoeSnapshot.AddRange(EnemyAI.All);
            for (int i = 0; i < _aoeSnapshot.Count; i++)
            {
                var e = _aoeSnapshot[i];
                if (e == null) continue;
                var h = e.GetComponent<Health>();
                if (h != null && h.alive
                    && Vector3.Distance(point, e.transform.position) <= shot.explosionRadius
                    && HasClearPath(point, e.transform.position, e.transform))
                {
                    h.TakeDamage(shot.explosionDamage, (e.transform.position - point).normalized);
                    hitAny = true;
                    // 魔法系统的被动钩子：爆炸 AOE 也算【玩家命中】（例：RPG 炸到也能挂印记）。
                    // 本分支已经在 `shot.team == Player` 里，所以不必再判阵营；`h.alive` 同上。
                    if (h.alive) { var pm2 = PlayerMagic.Instance; if (pm2 != null) pm2.NotifyHitEnemy(h, false); }
                }
            }
            if (hitAny) Crosshair.HitMark();
        }
        else
        {
            var pc = PlayerController.Instance;
            if (pc != null)
            {
                var php = pc.GetComponent<Health>();
                if (php != null && php.alive
                    && Vector3.Distance(point, pc.transform.position) <= shot.explosionRadius
                    && HasClearPath(point, pc.transform.position, pc.transform))
                {
                    php.TakeDamage(shot.explosionDamage, (pc.transform.position - point).normalized);
                    Sfx.Hurt();
                }
            }
        }
    }

    /// <summary>
    /// 从 from 看向 to 是否无遮挡 —— 爆炸范围伤害的**隔墙判定**。
    /// 起点抬到胸口高度、终点抬到目标位置，避免地面误判；命中目标自身不算遮挡（RPG 直击）。
    /// ⚠️ 掩码 = `Layers.Blocker`：**只有墙 / 箱子 / 地面能挡爆风，其它角色的身体不挡**
    ///    （与"敌人视线不被同伴挡"是同一套规则；§4 #24 记的"已知残留副作用"已由此清掉）。
    /// ⚠️ **唯一实现**：`Bullet`（经 `Combat`）与自爆兵 AOE 都调这里，不要再复制第二份。
    /// </summary>
    public static bool HasClearPath(Vector3 from, Vector3 to, Transform target)
    {
        Vector3 d = to - from;
        float dist = d.magnitude;
        if (dist <= 0.01f) return true;
        if (Physics.Raycast(from + Vector3.up * 0.3f, d.normalized, out RaycastHit hit, dist,
                            Layers.Blocker, QueryTriggerInteraction.Ignore))
            return hit.transform.IsChildOf(target) || target.IsChildOf(hit.transform);
        return true;
    }
}
