using UnityEngine;

/// <summary>
/// 敌人配置（配置用）。把原本写死在 WaveManager 里的敌人数值/外形抽成可编辑的资源，
/// 与 WeaponData 同一套思路：代码兜底 + Inspector 直接调。
/// - 推荐做法：Project 右键「Create > 游戏 > 敌人配置」创建 .asset，拖进 WaveManager 的槽位
/// - 代码兜底：Melee()/Shooter() 提供默认值，资源缺失时项目照常跑
/// </summary>
[CreateAssetMenu(fileName = "Enemy_新敌人", menuName = "游戏/敌人配置", order = 0)]
public class EnemyConfig : ScriptableObject
{
    [Tooltip("显示名（Debug 日志用）")]
    public string displayName = "敌人";

    [Header("行为（怎么打）")]
    [Tooltip("由 Inspector 下拉选择；列表自动包含所有 EnemyBehaviour 子类，新增行为脚本后会自动出现")]
    public string behaviourTypeName = "EnemyMeleeBehaviour, Assembly-CSharp";

    [Header("掉落")]
    [Tooltip("死亡时掉落武器的概率（0=不掉；只有带武器的远程行为才掉）")]
    [Range(0f, 1f)]
    public float dropWeaponChance = 0.5f;

    [Header("体型（留空 = 用代码生成的胶囊体）")]
    [Tooltip("身体模型 Prefab。留空则用代码生成带颜色胶囊体")]
    public GameObject bodyPrefab;
    [Tooltip("模型整体缩放（针对代码生成的胶囊体；用 Prefab 时由 Prefab 自己决定）")]
    public float bodyScale = 0.85f;
    [Tooltip("代码生成胶囊体的颜色（用 bodyPrefab 时忽略）")]
    public Color bodyColor = new Color(0.85f, 0.25f, 0.2f);

    [Header("头部与爆头（外观 + 爆头判定锚点）")]
    [Tooltip("头部（球体）相对敌人根物体的本地偏移（世界单位）。默认 (0, 0.93, 0) ≈ 正好坐在身体顶部。\n⚠️ 将来导入真实模型时，按每个怪物单独调这里，不用改代码")]
    public Vector3 headOffset = new Vector3(0f, 0.93f, 0f);
    [Tooltip("头部球体半径（世界单位）。默认 0.30")]
    public float headRadius = 0.30f;
    [Tooltip("头部颜色（与身体区分，一眼看出头顶在哪）")]
    public Color headColor = new Color(0.95f, 0.86f, 0.72f);
    [Tooltip("面部薄片颜色（贴在头的正前方，用来一眼看出敌人面朝哪边）。用高对比亮色")]
    public Color faceColor = new Color(1f, 0.95f, 0.35f);

    [Header("血量")]
    [Tooltip("基础血量")]
    public float maxHp = 100f;

    [Header("行动（近战/远程共用）")]
    [Tooltip("发现玩家后到第一次攻击的停顿（反应时间，秒）")]
    public float reactionTime = 0.4f;
    [Tooltip("巡逻速度")]
    public float patrolSpeed = 2.2f;
    [Tooltip("索敌视野距离")]
    public float sightRange = 11f;
    [Tooltip("视野角度（巡逻时生效；追击时全向感知）")]
    public float fov = 150f;
    [Tooltip("丢失视线多久后放弃追击（秒）")]
    public float loseSightTime = 3f;
    [Tooltip("追击速度")]
    public float chaseSpeed = 3.8f;

    [Header("近战行为")]
    [Tooltip("贴身攻击一次造成的伤害")]
    public float meleeDamage = 12f;
    [Tooltip("近战攻击距离")]
    public float attackRange = 1.4f;
    [Tooltip("近战攻击冷却（秒）")]
    public float meleeCooldown = 0.9f;

    [Header("远程行为（仅远程兵用）")]
    [Tooltip("远程兵与玩家保持的距离")]
    public float keepDistance = 7f;
    [Tooltip("一次连发打几发（三点射的弹数）")]
    public int burstCount = 3;
    [Tooltip("两次连发之间的间隔（秒）")]
    public float burstGap = 1.4f;

    [Header("远程枪（仅远程兵用）")]
    [Tooltip("远程兵手里的枪模型。留空用代码生成")]
    public GameObject gunPrefab;
    [Tooltip("远程兵的基础武器数据（数值会被下面的射速/伤害等覆盖）。留空使用默认冲锋枪")]
    public WeaponData shooterWeapon;
    public float shooterDamage = 8f;
    public float fireInterval = 0.13f;
    public float bulletSpeed = 20f;
    public float spread = 5f;
    public Color tracerColor = new Color(1f, 0.4f, 0.2f);

    [Header("自爆（仅自爆兵用）")]
    [Tooltip("爆炸伤害半径（米）")]
    public float explodeRadius = 4f;
    [Tooltip("爆炸对玩家造成的伤害")]
    public float explodeDamage = 60f;
    [Tooltip("靠近玩家后到自爆的预警时间（秒），期间身体闪烁警告")]
    public float fuseTime = 0.8f;

    [Header("狙击（仅狙击手用）")]
    [Tooltip("瞄准蓄力时间（秒）：红线出现到开枪的间隔")]
    public float sniperAimTime = 1f;
    [Tooltip("狙击冷却（秒）：开枪后到下一次能瞄准的间隔")]
    public float sniperCooldown = 2.5f;

    [Header("波次成长（只对新刷的敌人生效）")]
    [Tooltip("每波血量增加量")]
    public float hpGrowthPerWave = 4f;
    [Tooltip("血量成长封顶")]
    public float hpGrowthCap = 24f;
    [Tooltip("每波近战/远程伤害增加量")]
    public float dmgGrowthPerWave = 1f;
    [Tooltip("伤害成长封顶")]
    public float dmgGrowthCap = 6f;

    /// <summary>按当前波次算出实际血量</summary>
    public float HpAtWave(int wave) => maxHp + Mathf.Min((wave - 1) * hpGrowthPerWave, hpGrowthCap);

    /// <summary>按当前波次算出实际近战伤害</summary>
    public float MeleeDamageAt(int wave) => meleeDamage + Mathf.Min((wave - 1) * dmgGrowthPerWave, dmgGrowthCap);

    /// <summary>按当前波次算出实际远程伤害</summary>
    public float ShooterDamageAt(int wave) => shooterDamage + Mathf.Min((wave - 1) * dmgGrowthPerWave, dmgGrowthCap);

    System.Type _behaviourType;

    /// <summary>把行为类名字符串解析成类型（只解析一次，带缓存）。解析失败返回 null（调用方回落到近战）</summary>
    public System.Type BehaviourType
    {
        get
        {
            if (_behaviourType == null && !string.IsNullOrEmpty(behaviourTypeName))
                _behaviourType = System.Type.GetType(behaviourTypeName);
            return _behaviourType;
        }
    }

    /// <summary>
    /// 配置数值合法性兜底（仅编辑器生效：Inspector 改完立即夹回合法范围）。
    /// 最危险的是 maxHp = 0：EnemyAI 每物理帧算 `_hp.hp / _hp.maxHp` → NaN → 血条 sizeDelta 变 NaN。
    /// 其余（burstCount=0 → 三点射打不出弹、bulletSpeed=0 → 子弹原地不动）同理。
    /// 只用 if 判断而不是无条件赋值，避免把合法值也标成「已修改」而脏化资源。
    /// </summary>
    void OnValidate()
    {
        if (maxHp < 1f) maxHp = 1f;
        if (bodyScale < 0.01f) bodyScale = 0.01f;
        if (reactionTime < 0f) reactionTime = 0f;
        if (patrolSpeed < 0f) patrolSpeed = 0f;
        if (chaseSpeed < 0f) chaseSpeed = 0f;
        if (sightRange < 0.1f) sightRange = 0.1f;
        if (fov < 1f) fov = 1f;
        if (loseSightTime < 0.1f) loseSightTime = 0.1f;
        if (meleeDamage < 0f) meleeDamage = 0f;
        if (attackRange < 0.1f) attackRange = 0.1f;
        if (meleeCooldown < 0.01f) meleeCooldown = 0.01f;
        if (keepDistance < 0f) keepDistance = 0f;
        if (burstCount < 1) burstCount = 1;
        if (burstGap < 0.01f) burstGap = 0.01f;
        if (shooterDamage < 0f) shooterDamage = 0f;
        if (fireInterval < 0.01f) fireInterval = 0.01f;
        if (bulletSpeed < 0.1f) bulletSpeed = 0.1f;
        if (spread < 0f) spread = 0f;
        if (explodeRadius < 0f) explodeRadius = 0f;
        if (explodeDamage < 0f) explodeDamage = 0f;
        if (fuseTime < 0.01f) fuseTime = 0.01f;
        if (sniperAimTime < 0.01f) sniperAimTime = 0.01f;
        if (sniperCooldown < 0.01f) sniperCooldown = 0.01f;
        if (hpGrowthPerWave < 0f) hpGrowthPerWave = 0f;
        if (hpGrowthCap < 0f) hpGrowthCap = 0f;
        if (dmgGrowthPerWave < 0f) dmgGrowthPerWave = 0f;
        if (dmgGrowthCap < 0f) dmgGrowthCap = 0f;
        if (headRadius < 0.05f) headRadius = 0.05f;
        if (headOffset.y < 0f) headOffset.y = 0f;
    }

    // ---- 内置默认敌人（代码兜底，缺资源时使用）----
    public static EnemyConfig Melee()
    {
        var c = CreateInstance<EnemyConfig>();
        c.displayName = "近战兵";
        c.maxHp = 100f;
        c.meleeDamage = 12f;
        c.bodyColor = new Color(0.85f, 0.25f, 0.2f);
        c.bodyScale = 0.85f;
        return c;
    }

    public static EnemyConfig Shooter()
    {
        var c = CreateInstance<EnemyConfig>();
        c.displayName = "远程兵";
        c.maxHp = 80f;
        c.meleeDamage = 12f;
        c.bodyColor = new Color(0.92f, 0.55f, 0.15f);
        c.bodyScale = 0.85f;
        c.shooterDamage = 8f;
        c.fireInterval = 0.13f;
        c.bulletSpeed = 20f;
        c.spread = 5f;
        c.tracerColor = new Color(1f, 0.4f, 0.2f);
        return c;
    }
}