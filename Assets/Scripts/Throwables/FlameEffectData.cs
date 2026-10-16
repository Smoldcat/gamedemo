using UnityEngine;

/// <summary>
/// **火焰效果卡**（投掷物效果的一种，2026-10-16 新增）：在引爆点生成一片**持续燃烧的区域** ——
/// 按固定间隔对**圈内敌人**结算伤害（**隔墙不烧**），并不停地烧出火焰粒子。
///
/// ⭐ 定位 = **封锁型**（用户 2026-10-16 定）：伤害不高但烧得久、范围明显 → 逼对方绕路。
/// ⭐ 加一种"火"（蓝火 / 毒火 / 更强版本）= **复制一张卡改数值 + 换材质**，代码一行不动
///   （与「烟雾」「爆炸」两张卡同一套关系，见 `ThrowableEffectData`）。
/// ⚠️ 铁律（与所有效果卡一致）：卡里**只放配置**；"这片火还剩几秒、跳过几次伤害"归它生成的世界对象
///   （`FireZone`）—— 卡是菜谱，不许存冰箱里的东西。
/// ⚠️ **不挡视线**（用户 2026-10-16 定）：火只伤害，不做 `SmokeVolume` 那种视线遮挡。
/// </summary>
[CreateAssetMenu(menuName = "游戏/投掷物效果/火焰", fileName = "Effect_火焰")]
public class FlameEffectData : ThrowableEffectData
{
    [Header("火焰区域")]
    [Tooltip("燃烧半径（米，按 XZ 圆算）：圈内的敌人**每跳掉一次血**（与爆炸同为全额、没有距离衰减）。\n" +
             "⚠️ 这就是「封锁」的宽度 —— 比手雷（3）大、比烟（7）小是合理的（火是一片不是一片云）。")]
    [Min(0.5f)] public float radius = 3.5f;
    [Tooltip("**持续燃烧时间**（秒）：这段时间内不停发射火焰 + 每跳结算伤害。之后停止发射，残留火焰自然烧完。\n" +
             "封锁型建议长一些（8~12）：火不是用来秒人的，是用来「这段时间别站这儿」的。")]
    [Min(0.5f)] public float duration = 10f;
    [Tooltip("收起时长（秒）：停止发射后残余火焰的消散时间（建议 ≥ 粒子寿命，否则火会「啪」地消失）。")]
    [Min(0.2f)] public float fadeOut = 3f;

    [Header("伤害（每跳一次）")]
    [Tooltip("每跳伤害。参照：玩家 100 血、普通敌人约 100、坦克 350。\n" +
             "⚠️ 算「站满全程」的总伤害 = 本值 × (duration / tickInterval) —— 封锁型建议让它**能烧死但给得起逃跑时间**。")]
    [Min(0f)] public float damagePerTick = 10f;
    [Tooltip("结算间隔（秒）：0.5 = 每秒 2 跳。\n" +
             "⚠️ 每跳都会**弹一次伤害飘字**（这是 `Health.TakeDamage` 的既有行为）→ 间隔太短会刷屏，建议 ≥ 0.3。")]
    [Min(0.1f)] public float tickInterval = 0.5f;
    [Tooltip("**自伤倍率**：圈内的玩家也会被烧。1 = 全额（与手雷一致，用户 2026-10-16 定）；想放自己一马就调小。")]
    [Range(0f, 2f)] public float selfDamageMultiplier = 1f;

    [Header("火焰粒子（外观）")]
    [Tooltip("粒子材质：**推荐拖 `Assets/Art/FX/火焰.mat`**（跑一次菜单 `Tools/游戏/FX/导出火焰贴图` 就有了）。\n" +
             "留空 = 代码生成的火焰贴图 + 加法混合的兜底材质（照样是火，只是没进资源、不好被人替换）。")]
    public Material material;
    [Tooltip("单颗火焰粒子的寿命（秒）：0.5~1.2 比较像火苗；太长会变成一坨橘色团。")]
    [Min(0.1f)] public float particleLifetime = 0.75f;
    [Tooltip("单颗粒子的基准大小（米，半径 3.5m 时）：半径越大按比例放大。")]
    [Min(0.1f)] public float particleSize = 0.9f;
    [Tooltip("每秒发射几颗（半径 3.5m 时；半径更大按**面积**自动放大）。")]
    [Min(1f)] public float emitRate = 55f;
    [Tooltip("**向上窜的速度**（米/秒）：火苗往上舔的速度。0 = 只在原地闪（看着像贴纸）。")]
    [Min(0f)] public float riseSpeed = 1.1f;

    /// <summary>「按住右键看弹道」时的范围指示圈半径 = **燃烧半径**（勾了 showRangeIndicator 才画）。</summary>
    public override float DangerRadius => radius;

    public override void Apply(Vector3 point)
    {
        FireZone.Spawn(point, this);
    }
}
