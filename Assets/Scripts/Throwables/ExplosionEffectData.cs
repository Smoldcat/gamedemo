using UnityEngine;

/// <summary>
/// **爆炸效果卡**（手雷类投掷物的第一张效果卡）：引爆时调 <see cref="Combat.Explode"/> 结算
/// —— 表现（火球 / 冲击 / 爆炸圈）/ 音效 / **隔墙不掉血** / 命中反馈 / "爆炸算玩家命中"的魔法钩子
/// **全部白拿**，本类只是一层薄封装（把卡上的数值装进 `ShotProfile`）。
///
/// ⚠️ 与 RPG 的关系：RPG 走的是武器自己的链路（`WeaponData.explosionRadius/Damage` → 子弹 → `Combat.Explode`），
///    两边的**核心结算规则是同一条**（这正是"规则只有一份实现"要的效果）；
///    而**差异**（自伤 / 以后可能的破片 / 击退）只属于各自那条链 —— 手雷要改，只改本类，不碰 RPG。
/// </summary>
[CreateAssetMenu(menuName = "游戏/投掷物效果/爆炸", fileName = "Effect_爆炸")]
public class ExplosionEffectData : ThrowableEffectData
{
    [Header("爆炸")]
    [Tooltip("爆炸半径（米）：圈内**全额伤害**（与 RPG / 自爆兵同一套规则：没有距离衰减）")]
    [Min(0.1f)] public float radius = 4f;
    [Tooltip("爆炸伤害（圈内全额）。参照：玩家 100 血、普通敌人约 100、坦克 350")]
    [Min(0f)] public float damage = 120f;
    [Tooltip("**自伤倍率**：0 = 炸不到自己（与 RPG 现状一致）；1 = 全额（手雷的经典风险：贴脸扔 / 弹回来会炸到自己）")]
    [Range(0f, 2f)] public float selfDamageMultiplier = 1f;

    /// <summary>范围指示圈要画的半径 = 爆炸半径（勾了「显示范围指示圈」时才用得上）。</summary>
    public override float DangerRadius => radius;

    public override void Apply(Vector3 point)
    {
        var shot = new ShotProfile
        {
            mode = HitMode.Explode,
            team = Health.Team.Player,          // 玩家阵营：只打敌人（自伤由 selfDamageMultiplier 单独控制）
            explosionRadius = radius,
            explosionDamage = damage,
            selfDamageMultiplier = selfDamageMultiplier,
            color = new Color(1f, 0.6f, 0.15f),
        };
        Combat.Explode(point, shot);
    }
}
