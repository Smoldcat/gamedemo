using UnityEngine;

/// <summary>
/// 武器数据（配置用）。
/// - 推荐做法：在 Project 窗口右键「Create > 游戏 > 武器数据」创建成 .asset 资源，在 Inspector 里改数值，
///   改完不用重新编译代码就能在游戏里生效。
/// - 代码兜底：Pistol()/SMG()/Shotgun()/Sniper() 提供一份内置默认值，即使一个资源都没有，项目也能正常跑。
/// </summary>
[CreateAssetMenu(fileName = "Weapon_新武器", menuName = "游戏/武器数据", order = 0)]
public class WeaponData : ScriptableObject
{
    public enum SoundKind
    {
        [InspectorName("普通")] Normal,
        [InspectorName("霰弹")] Shotgun,
        [InspectorName("狙击")] Sniper
    }

    public string weaponName = "手枪";
    public float damage = 30f;        // 单发伤害
    public float fireInterval = 0.25f;// 开火间隔（秒）
    public int pellets = 1;           // 一次发射的弹丸数（霰弹枪>1）

    [Header("爆头")]
    [Tooltip("爆头伤害倍率（默认 2）：准星落在敌人头部判定圆内开火、且这一发命中该敌人时，伤害 × 本值（1 = 不翻倍）。\n" +
             "  · 判定与准星颜色共用同一份逻辑（准星变绿 = 必定爆头），这里只管「打头伤害多大」。\n" +
             "  · ⚠️ 2026-09-20 从「敌人配置」移到了武器上：同一把枪打不同敌人倍率一致，便于按「枪」调手感。\n" +
             "  · 低散布武器（手枪 / 狙击）更容易稳定爆头，是这套数值的主要受益者。")]
    public float headshotMultiplier = 2f;

    [Tooltip("腰射散布（度）：**不按右键**（腰射）时的弹道散布角。0 = 完美精度")]
    public float hipSpread = 2f;
    [Tooltip("瞄准散布（度）：**按住右键瞄准**时的弹道散布角。与腰射完全独立配置\n" +
             "（散布是个角度，不同枪瞄准后收紧的幅度差异很大，写成倍率反而不直观）")]
    public float aimSpread = 1f;
    [Tooltip("【腰射 → 瞄准】的散布过渡时间（秒）：按下右键后，散布从「腰射散布」**线性**过渡到「瞄准散布」所需的时间。\n" +
             "  · 0 = 瞬时切换（旧行为）。\n" +
             "  · 语义 = **定速**推进：过渡到一半就松开，回落只用「过渡时间 × 剩余比例」——按得越浅回得越快。\n" +
             "  · ⚠️ 只有**散布**走过渡；后坐倍率 / 横向闸门 / 移动速度仍是按下右键**立即**生效。")]
    public float hipToAimTime = 0.3f;
    [Tooltip("【瞄准 → 腰射】的散布过渡时间（秒）：松开右键后，散布从「瞄准散布」**线性**回到「腰射散布」所需的时间。\n" +
             "  · 0 = 瞬时切换（旧行为）。\n" +
             "  · 它是**上界**（不是「每次都要等满」）：过渡到一半就松开时，按剩余比例缩短（只探了半个头就收回 → 只用一半时间）。")]
    public float aimToHipTime = 0.1f;
    public float bulletSpeed = 30f;   // 子弹速度
    [Tooltip("子弹存活时间（秒）。射程 = 子弹速度 × 存活时间")]
    public float bulletLife = 0.6f;   // 子弹存活时间（默认 0.6s：提速后缩短飞行时间，滞后感消失，射程=速度×0.6）
    public int magSize = 12;          // 弹匣容量
    public float reloadTime = 1.2f;   // 换弹时间
    public Color tracerColor = new Color(1f, 0.9f, 0.3f); // 曳光颜色
    public Color pickupColor = Color.yellow;              // 拾取物高亮色

    [Header("手感与表现")]
    public float gunLength = 0.55f;   // 持枪/拾取模型的枪身长度（狙击最长）
    public float shake = 0.07f;       // 开火镜头震动强度
    public SoundKind sound = SoundKind.Normal;   // Normal=普通枪声, Shotgun=霰弹, Sniper=狙击

    [Header("后坐力（纯屏幕系，可压枪）")]
    [Tooltip("腰射纵向后坐：**不按右键**（腰射）时，每次开火把准星沿「屏幕上 玩家→准星」的方向往外顶开多少米。\n" +
             "  · ⚠️「纵向」= 沿推出方向（往外推远），**不是屏幕上的「上下」**——俯视角下这两者不是一回事，别按字面理解。")]
    public float hipRecoilUp = 0.05f;
    [Tooltip("腰射横向后坐：**不按右键**（腰射）时，每次开火垂直于推出方向的**左右随机**偏移（米），通常比纵向小。\n" +
             "  · 它是唯一会让弹道真正打偏的分量（纵向只是把准星沿射线外推），也是「横向后坐上限」所限制的对象。")]
    public float hipRecoilJitter = 0.03f;
    [Tooltip("瞄准纵向后坐倍率：**按住右键瞄准**时，纵向后坐 × 本值。\n" +
             "  · 1 = 与腰射完全一样；< 1 = 瞄准更稳（建议从 0.5~0.7 起试）；0 = 瞄准时无纵向后坐。\n" +
             "  · 为什么用倍率而不是「瞄准纵向后坐」绝对值：倍率是配件的原生数学（补偿器 -15% 之类），\n" +
             "    将来加配件只需在这条链上再乘一层，不必为「腰射 / 瞄准」各维护一套绝对值。")]
    public float aimRecoilUpMultiplier = 0.6f;
    [Tooltip("瞄准横向后坐倍率：**按住右键瞄准**时，横向后坐 × 本值（语义同「瞄准纵向后坐倍率」）。\n" +
             "  ⚠️ 它**同时作用于横向闸门的上限**（recoilMaxSide），保证瞄准时横向带宽的相对松紧不变。")]
    public float aimRecoilJitterMultiplier = 0.6f;
    [Tooltip("横向（左右）后坐上限（米）。0 = 不限（只受屏幕边界约束）。\n" +
             "  · > 0 = **本梭子横向最多偏这么多米**：把每发**实际推出去的**横向量累加起来（标量账本），到顶就\n" +
             "    **只拒绝继续往外推**，绝不把已经推出去的位移搬回来（旧实现是\"重算总位移 + 整体搬回\"，方向一转会单发瞬跳几百像素，见交接文档 §4 #28）。\n" +
             "  · 太小时要配合「横向后坐」一起看：屏幕左右很宽（16:9 约 ±12 米），高射速枪的横向随机游走会累积到米级、变成压不住的乱飘。\n" +
             "  · 高射速 + 大横向后坐的枪建议 1 左右。\n" +
             "  · ⚠️ 瞄准时上限会按「瞄准横向后坐倍率」同比缩放（横向后坐变小，带宽也跟着变小）。")]
    public float recoilMaxSide = 0f;
    [Tooltip("松手后准星回正速度（米/秒）")]
    public float recoilRecover = 1.0f;
    [Tooltip("回正延迟（秒）：停止开火后，准星要静置多少秒才开始回正。\n" +
             "  · 0 = 不延迟：松手且鼠标未动就立即回正（原行为）。\n" +
             "  · 静置期内的任何扰动（再次开火 / 移动鼠标 / 换弹）都会重新计时；鼠标一动则本次回正直接作废。\n" +
             "  · 它只影响「什么时候开始回正」；回正本身的速度/总时长仍由「回正速度」+ 玩家组件的「回正时长上限」决定。")]
    public float recoilRecoverDelay = 0f;

    [Header("爆炸（0 = 不爆炸）")]
    [Tooltip("子弹命中/到点后的爆炸半径（米）。0 = 普通子弹无爆炸")]
    public float explosionRadius = 0f;
    [Tooltip("爆炸对范围内敌人造成的伤害（仅 explosionRadius > 0 时生效）")]
    public float explosionDamage = 0f;

    [Header("外观（留空 = 用代码生成的默认外观）")]
    [Tooltip("这把枪掉在地上时的模型 Prefab")]
    public GameObject pickupModelPrefab;
    [Tooltip("这把枪拿在玩家手里时的模型 Prefab。留空则直接复用上面的「拾取模型」，所以通常只配一个就够了")]
    public GameObject heldModelPrefab;
    [Tooltip("这把枪的子弹/曳光模型 Prefab。留空则用代码生成的曳光条，颜色跟随 tracerColor")]
    public GameObject bulletPrefab;
    [Tooltip("武器图标（背包/HUD 显示用 Sprite）。留空则用程序生成的形状图标")]
    public Sprite icon;

    /// <summary>背包里武器形状图标的类型。Auto = 按武器名/枪身长度自动推测。</summary>
    public enum IconKind { Auto, Handgun, SMG, Shotgun, Sniper, Rifle, Generic }
    [Tooltip("背包里的图标形状。Auto = 按武器名/枪身长度自动推测")]
    public IconKind iconKind = IconKind.Auto;

    /// <summary>
    /// 运行时复制一份。
    /// 敌人会改造自己的射击武器（伤害随波次成长），必须用副本，绝不能直接改资源本体，
    /// 否则在编辑器里会把 .asset 文件改坏（改动会被写回磁盘）。
    /// </summary>
    public WeaponData Clone()
    {
        // 用 Instantiate 让 Unity 自己复制所有序列化字段（含以后新增的），
        // 而不是逐个字段手抄——手抄漏字段不会报错，只会静默丢数据
        //（本项目已因此踩坑两次：bulletLife、iconKind）。
        var c = Instantiate(this);
        c.name = string.IsNullOrEmpty(weaponName) ? name : weaponName;
        return c;
    }

    /// <summary>
    /// 配置数值合法性兜底（仅编辑器生效：Inspector 改完立即夹回合法范围）。
    /// 这些量误填 0 / 负数都会造成「不报错但行为崩坏」的隐蔽 Bug：
    ///   fireInterval = 0 → 每帧发射一发（子弹洪水 + 弹药瞬间清空）
    ///   pellets = 0      → 扣弹药、播音效、加后坐都走了，但一发子弹都不出
    ///   recoilRecover = 0 → 后坐永不回正（准星与弹道持续错位）
    ///   hipSpread/aimSpread 为负 → 散布区间反向
    ///   hipToAimTime/aimToHipTime 为负 → 过渡方向反向（会「瞬间倒退」到目标，等于瞬时切换）
    ///   maxHp 见 EnemyConfig 的同类处理（0 会让血条算出 NaN）
    /// 只用 if 判断而不是无条件赋值，避免把合法值也标成「已修改」而脏化资源。
    /// </summary>
    void OnValidate()
    {
        if (fireInterval < 0.01f) fireInterval = 0.01f;
        if (pellets < 1) pellets = 1;
        if (hipSpread < 0f) hipSpread = 0f;
        if (aimSpread < 0f) aimSpread = 0f;
        if (hipToAimTime < 0f) hipToAimTime = 0f;   // 0 = 瞬时切换（旧行为）
        if (aimToHipTime < 0f) aimToHipTime = 0f;
        if (bulletSpeed < 0.1f) bulletSpeed = 0.1f;
        if (bulletLife < 0.01f) bulletLife = 0.01f;
        if (magSize < 1) magSize = 1;
        if (reloadTime < 0.01f) reloadTime = 0.01f;
        if (gunLength < 0.01f) gunLength = 0.01f;
        if (damage < 0f) damage = 0f;
        if (headshotMultiplier < 1f) headshotMultiplier = 1f;   // 倍率 < 1 = 惩罚爆头，不是设计意图；下限 1
        if (shake < 0f) shake = 0f;
        if (hipRecoilUp < 0f) hipRecoilUp = 0f;
        if (hipRecoilJitter < 0f) hipRecoilJitter = 0f;
        if (aimRecoilUpMultiplier < 0f) aimRecoilUpMultiplier = 0f;
        if (aimRecoilJitterMultiplier < 0f) aimRecoilJitterMultiplier = 0f;
        if (recoilMaxSide < 0f) recoilMaxSide = 0f;
        if (recoilRecover < 0.01f) recoilRecover = 0.01f;
        if (recoilRecoverDelay < 0f) recoilRecoverDelay = 0f;
        if (explosionRadius < 0f) explosionRadius = 0f;
        if (explosionDamage < 0f) explosionDamage = 0f;
    }

    // ---- 内置默认武器（代码兜底，缺资源时使用）----
    // 手枪：  32/发      均衡入门，4 发放倒 100 血近战兵
    // 冲锋枪：13/发      射速快弹匣大（理论 144 DPS），近战持续输出强，远距离散布吃亏
    // 霰弹枪：11×7=77/发 贴脸一枪 77，两枪放倒任何敌人；距离拉远弹丸散开伤害骤降
    // 狙击枪：120/发     完美精度+贯穿全图的弹速，前几波敌人一枪一个；射速慢、弹匣小、换弹久
    //
    // ⚠️ 四个工厂都做了缓存：以前每次调用都 CreateInstance 一个新 ScriptableObject，
    // 而 CloneShootWeapon 在 EnemyConfig.shooterWeapon 留空时每次生成远程兵都会调用 SMG()，
    // 等于每生成一只远程兵泄漏 2 个 SO（本体 + 副本）。
    // 拿到的是同一个共享实例，**不要直接改它的数值字段**（要改请先 Clone()）。
    // 玩家的弹匣等运行时状态存在别处（PlayerBackpack.Slot / Clone 副本），所以「只读共享」是安全的。
    static WeaponData _cachedPistol, _cachedSMG, _cachedShotgun, _cachedSniper;

    /// <summary>静态状态清理（供 StaticReset 调用）：关闭 Reload Domain 时丢弃上一局的缓存实例。
    /// （其实 `!= null` 判空也能识别已销毁对象并自愈，这里显式清理是为了遵守 StaticReset 的登记约定。）</summary>
    internal static void ResetStatics()
    {
        _cachedPistol = null;
        _cachedSMG = null;
        _cachedShotgun = null;
        _cachedSniper = null;
    }

    public static WeaponData Pistol()
    {
        if (_cachedPistol != null) return _cachedPistol;
        var w = CreateInstance<WeaponData>();
        w.weaponName = "手枪"; w.damage = 32f; w.fireInterval = 0.24f; w.pellets = 1;
        w.headshotMultiplier = 2f;
        w.hipSpread = 1.5f; w.aimSpread = 0.6f;
        w.hipToAimTime = 0.3f; w.aimToHipTime = 0.1f;   // 与字段默认值一致（显式写出，避免以后改默认值时这条链被静默影响）
        w.aimRecoilUpMultiplier = 0.6f; w.aimRecoilJitterMultiplier = 0.6f;
        w.bulletSpeed = 32f; w.magSize = 12; w.reloadTime = 1.0f;
        w.tracerColor = new Color(1f, 0.92f, 0.35f); w.pickupColor = Color.yellow;
        w.gunLength = 0.45f; w.shake = 0.07f; w.sound = SoundKind.Normal;
        _cachedPistol = w;
        return w;
    }
    public static WeaponData SMG()
    {
        if (_cachedSMG != null) return _cachedSMG;
        var w = CreateInstance<WeaponData>();
        w.weaponName = "冲锋枪"; w.damage = 13f; w.fireInterval = 0.09f; w.pellets = 1;
        w.headshotMultiplier = 2f;
        w.hipSpread = 4.5f; w.aimSpread = 1.8f;
        w.hipToAimTime = 0.3f; w.aimToHipTime = 0.1f;
        w.aimRecoilUpMultiplier = 0.6f; w.aimRecoilJitterMultiplier = 0.6f;
        w.bulletSpeed = 36f; w.magSize = 30; w.reloadTime = 1.5f;
        w.tracerColor = new Color(0.5f, 1f, 1f); w.pickupColor = Color.cyan;
        w.gunLength = 0.55f; w.shake = 0.05f; w.sound = SoundKind.Normal;
        _cachedSMG = w;
        return w;
    }
    public static WeaponData Shotgun()
    {
        if (_cachedShotgun != null) return _cachedShotgun;
        var w = CreateInstance<WeaponData>();
        w.weaponName = "霰弹枪"; w.damage = 11f; w.fireInterval = 0.75f; w.pellets = 7;
        w.headshotMultiplier = 2f;
        w.hipSpread = 9f; w.aimSpread = 3.5f;
        w.hipToAimTime = 0.3f; w.aimToHipTime = 0.1f;
        w.aimRecoilUpMultiplier = 0.6f; w.aimRecoilJitterMultiplier = 0.6f;
        w.bulletSpeed = 26f; w.magSize = 6; w.reloadTime = 1.8f;
        w.tracerColor = new Color(1f, 0.55f, 0.25f); w.pickupColor = new Color(1f, 0.5f, 0.1f);
        w.gunLength = 0.72f; w.shake = 0.20f; w.sound = SoundKind.Shotgun;
        _cachedShotgun = w;
        return w;
    }
    public static WeaponData Sniper()
    {
        if (_cachedSniper != null) return _cachedSniper;
        var w = CreateInstance<WeaponData>();
        w.weaponName = "狙击枪"; w.damage = 120f; w.fireInterval = 1.1f; w.pellets = 1;
        w.headshotMultiplier = 2f;
        w.hipSpread = 0f; w.aimSpread = 0f;   // 狙击本来就完美精度，瞄准不改变散布
        w.hipToAimTime = 0.3f; w.aimToHipTime = 0.1f;   // 0→0：过渡不可见，仅为配置一致性显式写出
        w.aimRecoilUpMultiplier = 0.6f; w.aimRecoilJitterMultiplier = 0.6f;
        w.bulletSpeed = 55f; w.magSize = 4; w.reloadTime = 2.2f;
        w.tracerColor = new Color(0.85f, 0.45f, 1f); w.pickupColor = new Color(0.8f, 0.4f, 1f);
        w.gunLength = 0.95f; w.shake = 0.30f; w.sound = SoundKind.Sniper;
        _cachedSniper = w;
        return w;
    }
}
