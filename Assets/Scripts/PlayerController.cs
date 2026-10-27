using UnityEngine;

/// <summary>
/// 玩家控制：WASD 移动、Shift 点按切换跑/走、右键瞄准（移动变慢）、始终朝向准星、拾取/切换武器、开火换弹。
/// 移动速度优先级 = **瞄准 &gt; 跑 &gt; 走**；且**开一枪会强制退出奔跑**（见 <see cref="CurrentMoveSpeed"/> / <see cref="Fire"/>）。
/// 需要在 Inspector 里拖好三个引用：gunVisual（枪的挂载点）、gunTip（枪口）、mainCamera。
/// 玩家物体上还需要有 Rigidbody 和 CapsuleCollider（物理移动用）。
/// </summary>
public class PlayerController : MonoBehaviour
{
    public static PlayerController Instance;

    [Header("引用（必须拖）")]
    [Tooltip("枪的挂载点：枪模型会挂到这个空物体下面（它本身不显示任何东西，缩放必须保持 1，否则模型会被拉变形）")]
    public Transform gunVisual;
    [Tooltip("枪口发射点：开火时子弹从这里射出。\n位置在换枪时会自动对齐到「挂在 gunVisual 上的模型最前端」，正常情况下不需要手调")]
    public Transform gunTip;
    [Tooltip("主相机。留空时运行时会自动找 Camera.main\n（Player.prefab 里故意留空 —— 预制体不能引用场景物体）")]
    public Camera mainCamera;

    // ---- 后坐动画（可选，2026-09-25）----
    // 给「Gun 挂载点」挂一个 **legacy `Animation`** 组件（clip 只驱动 localPosition.z），开火时这里重播它。
    // 所有武器模型都挂在同一个 `Gun` 下 → **一个动画对全部武器生效**，不用给 8 个模型 prefab 各配一套。
    Animation _gunAnim;
    Vector3 _gunBaseLocalPos;   // Gun 的基准本地位置（Awake 记录）：换枪时若动画还在播，用它复位（见 UpdateGunVisual）

    [Header("移动")]
    [Tooltip("行走速度（米/秒）。按 Shift 在「跑 / 走」之间点按切换。\n" +
             "⚠️ 开一枪会强制退出奔跑（掉回行走），要再按一次 Shift 才能继续跑")]
    public float walkSpeed = 4f;
    [Tooltip("奔跑速度（米/秒）。开局默认处于奔跑状态。\n" +
             "⚠️ 只在「没瞄准、也没在开枪」时生效 —— 开一枪就会退出奔跑")]
    public float runSpeed = 7.5f;
    [Tooltip("瞄准速度（米/秒）：**按住右键瞄准时**的移动速度。\n" +
             "  · 要比「行走速度」更慢 —— 这是瞄准的代价（否则全程按着右键就是最优解，腰射就没意义了）。\n" +
             "  · 速度优先级：**瞄准 > 跑 > 走**（瞄准期间不管 Shift 状态，一律用本值）")]
    public float aimSpeed = 2.5f;
    [Tooltip("加速度（米/秒²）：越大起步 / 刹车越干脆，越小越「滑」")]
    public float accel = 40f;

    [Header("武器")]
    [Tooltip("当前快捷槽的武器（**运行时镜像**：由背包的当前槽同步过来，HUD / 准星都读它）。\n" +
             "⚠️ 在编辑器里改这里没有意义 —— 一进游戏就会被 LoadMirrorFromSlot 覆盖")]
    public WeaponData weapon;
    [Tooltip("当前快捷槽的弹匣余弹（**运行时镜像**：切槽时与背包互写）。\n" +
             "⚠️ 在编辑器里改这里没有意义 —— 一进游戏就会被覆盖")]
    public int magAmmo;
    [Tooltip("弹匣里装的是**哪一张弹药卡**（= 这一匣子弹的等级；**运行时镜像**，与 `magAmmo` 同生共死）。\n" +
             "null = 未知 → 装填时按「该口径**有货的最低等级**」自动选。\n" +
             "⚠️ 开火伤害倍率、按 T 换级时旧弹退给谁、HUD 显示哪一级的备弹 —— 全读它")]
    public AmmoData magAmmoId;

    [Header("武器背包")]
    [Tooltip("3 快捷槽 + 库存 + 血包的背包。挂到玩家身上或由本组件自动创建")]
    public PlayerBackpack backpack;

    [Header("魔法")]
    [Tooltip("玩家侧的魔法管理器（装备同步 / 冷却 / 释放入口 / 命中转发）。\n" +
             "⚠️ **不需要手拖**：Start 时若为空会自动 AddComponent 创建（与背包同一套做法）。")]
    public PlayerMagic magic;

    [Header("投掷物")]
    [Tooltip("投掷物管理器（G 槽：持弹模式 / 选择器 / 轨迹预览 / 投出）。\n" +
             "⚠️ **不需要手拖**：Start 时若为空会自动 AddComponent 创建（与背包 / 魔法同一套做法）。")]
    public PlayerThrowable throwables;

    [Header("弹药等级选择器")]
    [Tooltip("弹药等级（子弹档位）选择器（T 槽：开/关选择器、滚轮选档、再按 T / 左键提交换级，仿投掷物选择器）。\n" +
             "⚠️ **不需要手拖**：Start 时若为空会自动 AddComponent 创建（与背包 / 魔法 / 投掷物同一套做法）。")]
    public AmmoSelector ammoSel;

    [HideInInspector] public bool running = true;
    [HideInInspector] public bool reloading;
    /// <summary>「按 T 换级」的目标卡 —— **非 null = 本次换弹是换级**（完成时要"退旧弹 + 装新弹"）。
    /// 为什么要有它：换级复用换弹那套计时与 `reloading` 状态（用户定："更换需要换弹"），
    /// 完成时靠它区分该走哪条分支；⚠️ 切枪 / 丢弃会走 `LoadMirrorFromSlot`，那里必须清掉它
    /// （否则"换到一半切了枪"会把旧弹退掉、却不再装新弹）。</summary>
    AmmoData _switchTarget;

    /// <summary>本次换弹是不是"按 T 换级"（只读）。HUD / 准星用它把文案从"换弹中…"改成"换弹药中…"，
    /// 免得玩家分不清"我在装弹"还是"我在换等级"（2026-09-27 独立审计建议）。</summary>
    public bool switchingAmmo => _switchTarget != null;

    [Header("后坐推出（平滑过渡）")]
    [Tooltip("后坐推力的过渡速度（米/秒）。每发后坐不再瞬移到位，而是以这个速度把准星平滑推过去：\n大后坐（如 0.5m）看得见过程，小后坐（0.05m）近似瞬时。设得越小越'拖沓'、越像连续漂移")]
    public float recoilKickSpeed = 8f;

    [Header("鼠标（锁定模式下的像素换算）")]
    [Tooltip("鼠标轴增量 → 屏幕像素 的换算系数。一般**不用手调**：运行时会在未锁定的帧里自动实测校准\n（同一帧同时读'光标像素位移'与'轴增量'，相除即精确系数 —— InputManager 的灵敏度没有运行时 API 可读）。\n本值只是初值/兜底，取 1 ÷（InputManager 里 Mouse X 的 sensitivity），本工程 = 10")]
    public float mousePixelScale = 10f;

    /// <summary>是否已通过实测校准过换算系数（校准过就一直用轴增量，与光标位置彻底解耦 = 无限行程）</summary>
    bool _scaleCalibrated;

    /// <summary>校准样本的最小轴增量（平方）：太小则比例不可靠（除出来的噪声大）</summary>
    const float CalibMinAxisSq = 0.25f;

    [Header("后坐上限（自动 = 屏幕边界）")]
    [Tooltip("准星距屏幕边缘至少保留的余量，按屏幕短边的比例。\n" +
             "  · 默认 0 = 不留余量，准星可以贴到屏幕最边缘（用户指定的默认值）。\n" +
             "  · 调大 = 留出「屏幕短边 × 本值」的余量作为缓冲，例如 0.1 = 10%。\n" +
             "后坐上限每帧由它反算，所以换分辨率不用重调；填 0.45 以上会被夹回")]
    [Range(0f, 0.45f)] public float recoilScreenMargin = 0f;

    [Header("回正")]
    [Tooltip("回正时长上限（秒）。小幅偏移按武器资源的「回正速度」原速回（更快）；\n若按原速会超过这个时长，就把速度提到「起始偏移 ÷ 本值」，保证最多这么久回到标记点")]
    public float recoilRecoverMaxTime = 1.5f;
    [Tooltip("回正「手抖余量」= **屏幕短边（横屏下即屏幕高度）的比例**（无量纲，不是像素）。\n" +
             "  · 从按下左键记点起，鼠标的**累计净位移**不超过「短边 × 本值」就算手抖、**不打断回正**；超过才作废标记点。\n" +
             "  · 0 = 任何移动都打断（原始行为，脚本默认值即为 0）。\n" +
             "  · 换算锚点：0.011 ≈ 720p(高 720) 下 8 像素、1080p 约 12px。参考上限：爆头判定半径 = 屏高 × 0.0125。\n" +
             "  · 为什么用比例而不是像素：同一手抖在高分辨率上产生的像素更多，写死像素会让余量随分辨率变紧（与「爆头半径」「屏幕边缘余量」同一套做法）。\n" +
             "  · 为什么用「累计净位移」而不是「每帧位移」：手抖是来回振荡的（净位移≈0）、真实瞄准是单向累积的；\n" +
             "    而且累计量与帧率无关 —— 每帧阈值会随帧率失真（见 §4 #12 注意1）。\n" +
             "  · ⚠️ 代价：小于余量的有意微调也会被回正一起抹掉（这是余量的必然）。")]
    [Range(0f, 0.05f)] public float recoilRecoverJitterRatio = 0f;

    [Header("瞄准判定（头部 / 身体）")]
    [Tooltip("【头部 · 判定圈口径】默认 1 = 判定圈**恰好等于屏幕上画出来的那个头球**（所见即所判）。\n" +
             "  · 怎么算：把「敌人头心」与「头心 + 相机右方向 × 头半径」各投影到屏幕，取两点像素距离 = 头在屏幕上的可见半径；\n" +
             "    再乘本值就是判定圈半径。所以准星压在头上（哪怕只压在头的边缘）就必定爆头。\n" +
             "  · 为什么不用固定像素圈：固定像素圈**不随相机 FOV 变**，而敌人的头在屏幕上会随 FOV 变大变小 →\n" +
             "    两者必然越差越远（FOV 45 时判定圈只有可见头的 ~60%，出现「准星明明压在头上却不爆头」）。\n" +
             "    现在判定圈与头**同源** → 改 FOV / 分辨率 / 宽高比 / 距离都**不会**改变爆头平衡。\n" +
             "  · 判定时机：**开火那一瞬间**（之后敌人怎么移动都不改变结果，只会变成打空）。\n" +
             "  · <1（如 0.6）= 要求比看到的头更精准 → 难度更高；\n" +
             "  · ≤0 = **回退旧口径**（全敌人共用一个固定像素圈 = 屏幕高 × headshotRadiusRatio），此时本值失效。\n" +
             "  · ⚠️ 上限 1.5：头心与敌人中心在屏幕上的间距 ≈ 头可见半径 ÷ 0.645，再大就会把「瞄准身体中心」也判成爆头。")]
    public float headshotGateScale = 1f;

    [Tooltip("【头部 · 旧口径（仅在 headshotGateScale ≤ 0 时生效）】爆头判定圆的屏幕半径 = 屏幕高度 × 本值。\n" +
             "  · 0.0125 = 720p 屏幕下约 9 像素；值越大 → 判定圈越大 → 越好爆头。\n" +
             "  · 当初为什么用「屏幕比例」而不是米：子弹是水平直线、3D 上打不到头顶，判定只能跟着「屏幕上看到的头」走；\n" +
             "    好处是与距离、分辨率都无关（远处不会更难，距离的难度由武器散射自然提供）。\n" +
             "  · 代价：它**不跟随相机 FOV**，且恒小于「屏幕上看到的头」→ 已被 headshotGateScale 取代，这里只作回退。")]
    public float headshotRadiusRatio = 0.0125f;

    [Tooltip("【身体 · 单位=世界米数】判定「准星指着的这发子弹会打中它的身体」：\n" +
             "从枪口沿「枪口 → 瞄准点」方向的**射线**（水平面内），到敌人**中心的距离** ≤ 本值（米）。\n" +
             "  · 用的判据和 Bullet 的真实命中**完全同一个** → 填 0.4（敌人碰撞体半径）时「判为身体 ⟺ 这一枪会打中」精确成立。\n" +
             "  · 默认 0.9（比真实命中宽松）；想让它精确等于命中范围就调到 0.4。\n" +
             "  · 为什么用「米」而不是屏幕比例：这个判定贴在敌人身上、随距离在屏幕上自然缩放，和碰撞体一样；\n" +
             "    若用屏幕比例，远处会出现「准星指在空地上也算指着敌人」。\n" +
             "  ⚠️ 2026-10-17：准星「指着敌人变色」已移除 ⇒ 本值目前**只服务于调试可视化**\n" +
             "     （面板「视觉」页的「判定圈」会按这个半径在每个敌人脚下画一圈）。\n" +
             "     想直观校准受击区域：进 Play 打开那个开关、看通道即可。")]
    public float bodyAimRadius = 0.9f;

    [Header("瞄准死区")]
    [Tooltip("【单位=世界米数】死区半径：准星点离「瞄准基准点」小于这个距离时，方向**冻结在最后一次有效值**\n" +
             "（准星失去瞄准作用、只保留开火）。它掐的是一个自激旋转：\n" +
             "  · 病根：朝向 ← 由「基准点 → 准星」方向决定，而基准点随朝向转 → 跨帧反馈环；\n" +
             "    准星贴近基准点时环的增益 > 1 → 每帧甩向新角度（表现就是「玩家原地疯狂旋转」）。\n" +
             "  · 几何：阈值 ≈ 基准点到玩家中心的距离。基准点已取「枪轴上离玩家最近的点」→ 实测 ≈ 0.45m，\n" +
             "    这就是安全下限；本值默认 0.6m（= 1.25 倍余量）。\n" +
             "  · ⚠️ 调到 0.45m 以下会重新落进自激区；调到 0 = 关闭保护（只在想复现它时这么干）。\n" +
             "  · 手感调节就在这一个数：俯视角下 0.6m ≈ 玩家周围屏幕上 7px，平时几乎踩不到。\n" +
             "  · 调大 = 更安全（但准星贴近玩家时会「枪不听使唤」的区域变大）；调小 = 更跟手（但可能抖）。")]
    [Min(0f)] public float aimDeadZoneRadius = 0.6f;

    // ==================== 子弹纵向扰动（2026-10-18）====================
    // 目的：让弹道在 y 方向轻微上/下偏，增加俯视角下的空间感（原来所有子弹都飞在同一个 0.55 平面）。
    // ⚠️ 只影响**玩家开火**（写在 `Fire()` 里）；敌人子弹、玩家朝向、激光、爆头判定全部不受影响。

    [Header("子弹纵向扰动（普通子弹）")]
    [Tooltip("【普通子弹】单发向上偏的最大角度（度）。\n\n" +
             "分布 = 三角分布（两次均匀随机相加）：中间概率高、两端趋近 0，典型值约为上限的 1/3。\n" +
             "每颗弹丸独立掷角（霰弹的多颗会各自偏）。\n" +
             "向上/向下的**概率**按 上偏 : 下偏 两个参数的比例分配（不是各 50%；所以下偏角设 0 → 100% 向上）。\n\n" +
             "按 0.6° 换算的世界抬高量：\n" +
             "10m ≈ 10.5cm\n20m ≈ 20.9cm\n30m ≈ 31.4cm\n40m ≈ 41.9cm\n\n" +
             "⚠️ 敌人身体胶囊顶约 1.17m（0.55 + 0.62）：0.6° 要 59m 才越顶 → 40m 内不会出现瞄了打不到。\n" +
             "⚠️ 爆炸类武器（RPG，射程 27m）的命中点同时是爆炸中心：up 超过 0.74° 时，27m 处的爆炸点" +
             "会高到让「隔墙不炸」判定从 1.2m 墙顶越过。默认 0.6° 安全。")]
    [Min(0f)] public float bulletUpAngle = 0.6f;

    [Tooltip("【普通子弹】单发向下偏的最大角度（度）。分布同上一项；这个值越小，向下偏的**概率**也越低。\n\n" +
             "按 0.15° 换算：\n10m ≈ 2.6cm\n20m ≈ 5.2cm\n30m ≈ 7.9cm\n40m ≈ 10.5cm\n\n" +
             "⚠️ 子弹固定飞在 0.55m，而可交互箱子（box(0~3)）的碰撞体顶面只有 0.50m —— 向下余量仅 5cm。\n" +
             "0.15° 在 19m 外就会跌破箱顶 → 远距离隔着箱子射击时，约 5% 的子弹会被箱子挡住（时中时不中）。\n" +
             "想完全避免：设 0 ~ 0.07°。")]
    [Min(0f)] public float bulletDownAngle = 0.15f;

    [Header("子弹纵向扰动（爆头弹）")]
    [Tooltip("【爆头弹】向上偏的角度（度）。爆头弹 = 开火瞬间判为爆头、弹道已改指向该敌人的那一发。\n" +
             "三角分布、每颗弹丸独立，且**只用爆头这对参数**（不叠加普通那对）。\n" +
             "下偏角设 0 时 = 100% 向上偏（不会出现「一半没偏」）。\n\n" +
             "按 0.7° 换算：\n10m ≈ 12.2cm\n20m ≈ 24.4cm\n30m ≈ 36.7cm\n40m ≈ 48.9cm\n\n" +
             "⚠️ 敌人的头没有碰撞体（爆头是屏幕空间判定），子弹最高只能打到身体胶囊顶（约 1.17m）：\n" +
             "0.7° 在 40m 处抬到约 1.04m，仍稳稳命中身体；超过 1° 会开始从头顶越过（打空）。")]
    [Min(0f)] public float headshotUpAngle = 0.7f;

    [Tooltip("【爆头弹】向下偏的角度（度）。设 0 = 100% 向上偏（推荐）。\n" +
             "该值越大，向下偏的概率越高（概率按 上偏 : 下偏 的比例分配）。\n" +
             "⚠️ 与普通向下同一个箱顶约束：0.15° 在 19m 外可能被 0.5m 高的箱子挡住。")]
    [Min(0f)] public float headshotDownAngle = 0f;

    Rigidbody _rb;
    Health _hp;
    Vector2 _moveInput;
    Vector3 _aimPoint;
    float _nextNoAmmoWarnTime;              // "没有弹药"提示的下次允许时间（节流：空弹时按住左键会每帧重试换弹）
    Vector3 _bulletDir = Vector3.forward;   // 本帧弹道方向（由 UpdateBulletDir 缓存；死区内保持上次有效值）
    bool _bulletDirValid;                   // 是否已经解算过至少一次（没解算过时用 transform.forward 兜底）
    float _nextFireTime, _reloadUntil;

    // ---- 后坐：纯屏幕系 + 「锚点跟准星」模型 ----
    // 坐标系历史（踩坑见交接文档 §4 #18）：曾用"人物朝向 × 标量"和"瞄准方向 × 标量"两种表示，
    // 都因为"偏移的方向挂在一个玩家附近会翻转的方向上"而失败（准星甩尾 / 靠不近玩家）。
    // 现在：**准星位置本身就是唯一真相**（一个屏幕坐标），鼠标只贡献"每帧位移增量"，
    // 后坐直接把准星推走 —— 于是不存在"准星 vs 鼠标"的错位（锚点天然等于准星，见 §4 #20）。
    Vector2 _crosshairTarget;   // 准星**目标**屏幕坐标（鼠标增量 + 后坐冲量）：表示"要到达哪"，回正也作用于它
    Vector2 _crosshairEff;      // 准星**实际**屏幕坐标：每帧以 recoilKickSpeed 匀速追目标值（kick 平滑）。瞄准点/弹道/准星都用它
    Vector2 _lastMousePos;      // 上一帧鼠标位置（取增量用；同时用于判断"鼠标是否在动"）
    Vector2 _mouseDelta;        // 本帧鼠标增量（窗口外/失焦时为 0，防止回到窗口内时产生巨大位移）
    Vector2 _mouseDeviation;    // 自记点以来的鼠标**累计净位移**（像素）：手抖余量的判据（累计量，与帧率无关）
    Vector2 _mark;              // **回正标记点**：按下左键那一刻准星所在的位置（回正的目标）。同一时刻最多一个
    bool _markValid;            // 标记点是否存在（松手后回正用完即清、鼠标一动即清、换弹/切枪即清）
    Vector2 _latRefDir = Vector2.right; // 本梭子的「左右基准方向」= 玩家屏幕位置 → 标记点（**由玩家动作离散更新**，见 CaptureLatRef）
    float _latAccum;            // 横向账本（标量，像素）：只累加"每发实际推出去的横向量"，供 recoilMaxSide 封顶
    Vector2 _lastPushDir = Vector2.up;  // 上一次后坐推出方向（准星几乎压在玩家身上时方向无意义，沿用上次）
    float _pxPerM = 60f;        // 瞄准点处 1 世界米对应的屏幕像素数（透视下随深度变，每帧实测，见 UpdateAimPoint）
    bool _recovering;           // 是否正处于一次回正过程中（决定 _recoverSpeed 要不要重新取值）
    float _recoverSpeed;        // 本次回正的速度（像素/秒）：在"回正开始"那一刻算一次并锁住，见 UpdateCrosshair
    float _undisturbedSince;    // 「最后一次扰动」的时刻（开火 / 鼠标移动 / 换弹）。静置满 recoilRecoverDelay 才允许开始回正
    bool _aiming;               // 是否正按住右键瞄准（腰射 ↔ 瞄准）：决定「散布过渡的目标方向 / 当前后坐 / 移速」，不改弹道算法
    float _adsProgress;         // 「腰射 ↔ 瞄准」的**散布过渡进度** 0~1（0 = 完全腰射，1 = 完全瞄准），由 AdvanceAds 每帧定速推进
                                // ⚠️ 只有 `CurrentSpread` 由它派生；后坐 / 移速 / 横向闸门仍按 `_aiming` **瞬间**切换（本轮刻意如此）
    float _scrollAccum;         // 滚轮切槽的"整格"累加器：高精度滚轮一帧可能只给 0.x，攒够 ±1 才切一次（见 CycleSlot）
    bool _aimNeedsRepress;      // 「切枪后必须重新按下右键」锁存：换枪那一刻置位（见 LoadMirrorFromSlot），
                                // 右键仍按着也保持腰射；松开右键即解除 → 再按一次才进入瞄准（2026-10-11）
    float _fireLockUntil;       // 「切枪动作」的锁火截止时刻（Time.time）：换手那一刻起算 —— 起算的**唯一入口**是
                                // `BeginHandSwap()`（换枪分支 + 投掷物 ExitMode）；到点前不能开火 / 起手换弹；
                                // 对外只暴露判据 `FireLocked`（时长 = GameAssets.switchWindup）（2026-10-12）

    /// <summary>鼠标增量的「噪声死区」（平方像素）：小于它的单帧增量当作浮点噪声，不计入净位移。
    /// ⚠️ 它**只是死区**，不是"是否在动"的判据 —— 判据是 `recoilRecoverJitterRatio`（**累计净位移** vs 屏幕短边比例，帧率/分辨率无关）。
    /// 背景：历史上一度用 2px/帧当判据 → 高帧率下慢速精瞄每帧位移 < 2px 被误判静止、回正照常启动（§4 #12 注意1）。</summary>
    const float MouseMoveEpsSq = 0.01f;

    /// <summary>单帧鼠标位移的合理上限（平方）。只用来挡住"垃圾坐标"造成的甩飞，正常越快的手也远达不到。</summary>
    static float MaxMouseDeltaSq => (Screen.width + Screen.height) * (Screen.width + Screen.height);

    // 血包的回血量已改为**数据驱动**：从 `MedkitData.healAmount` 读
    // （`PlayerBackpack.UseMedkit()` 会把用掉的那张定义卡返回给调用方）。
    // ⚠️ 不要再在这里加回写死的常量 —— 那就是"同一个量两处定义"的老坑（§4 #15）。

    /// <summary>准星在屏幕上的实际位置（左下角原点，像素）＝过渡值。
    /// **单向数据源**：准星显示、瞄准点、弹道都由它派生 —— 不存在第二个"锚点"，所以永远不会出现"准星与光标错位"。</summary>
    public Vector2 CrosshairScreenPos => _crosshairEff;

    /// <summary>把准星**直接搬到**指定屏幕坐标（供 `Crosshair` 在"关闭背包"时继承虚拟光标的位置）。
    /// 这是合法路径 —— 开局也是直接设 `_crosshairTarget/_crosshairEff`（见 `Awake`）。
    /// ⚠️ **必须同时清掉下面这 5 个状态**，否则准星会自己朝"旧标记点"跑回去：
    ///    `_markValid`（回正标记点）／`_recovering`（回正进行中）／`_mouseDeviation`（手抖余量的累计净位移）／
    ///    `_latAccum`（本梭子横向账本）／`_undisturbedSince`（静置期从现在起算）。
    /// 位置会走准星自己的安全区夹取（与每帧那两行同一个 `ClampToSafeRect`）→ 摆不到屏幕外。
    /// ⚠️ 刻意不动 `_aiming`：暂停分支本来每帧就复位它，按住/松开右键的真实状态会在恢复的下一帧自己接上。</summary>
    public void SetCrosshairScreenPos(Vector2 screenPos)
    {
        float m = Mathf.Min(Screen.width, Screen.height) * Mathf.Clamp(recoilScreenMargin, 0f, 0.45f);
        _crosshairTarget = ClampToSafeRect(screenPos, m);
        _crosshairEff = _crosshairTarget;      // 目标值 = 过渡值 → 不产生任何"追赶"位移

        _markValid = false;
        _recovering = false;
        _mouseDeviation = Vector2.zero;
        _latAccum = 0f;
        _undisturbedSince = Time.time;
    }

    /// 当前准星指向的世界坐标（供相机前探使用）
    public Vector3 AimPoint => _aimPoint;

    /// <summary>枪口的世界坐标（**唯一实现**）：有 `gunTip` 就用它，否则退化成"玩家位置 + 前方 0.6m"。
    /// ⚠️ 原先 `Fire()` 与 `GetAimState()` **各抄了一份完全相同的表达式**（2026-10-15 收敛到这里）——
    ///    以后要改枪口口径（例如多枪管 / 挂点改名）只改这一处。
    /// ⚠️ 需要"子弹飞行平面"高度时**仍要自己压平**：`Fire()` 里有 `muzzle.y = GameConst.EntityPlaneY`，
    ///    本属性**不做**压平（它同时被用于取 XZ 的场合，压平会让 y 失去意义）。</summary>
    public Vector3 MuzzlePos => gunTip != null ? gunTip.position : transform.position + transform.forward * 0.6f;

    /// <summary>本帧的**弹道方向**（已压平 y、已归一化）—— **唯一实现**：
    /// 朝向、开火（`Fire`）、命中判定（`GetAimState`）、激光（`LaserBeam`）全部读这里，禁止各自复刻。
    ///
    /// 由 <see cref="UpdateBulletDir"/> 每帧算一次并缓存（缓存是为了「死区冻结」需要状态）；
    /// 方向基准点是 <see cref="AimBasis"/>（枪轴上离玩家中心最近的点），**不是**枪口。
    /// ⚠️ **不能**用 `transform.forward` 当弹道方向：枪口在玩家身体右侧，沿 forward 发射是一条
    /// **平行线**、会恒偏右打不准星（"不跟手"，历史踩坑）。</summary>
    public Vector3 BulletDir
    {
        get
        {
            // 兜底：`UpdateBulletDir()` 还没跑过时（别的组件先读，或本组件这帧被跳过），按同一套算法现场算一次，
            // 避免把字段初值 `Vector3.forward`（世界 +Z）当成弹道方向。不写缓存 → 属性保持"只读、无副作用"。
            if (_bulletDirValid) return _bulletDir;
            float d;
            return RawAimDir(out d);
        }
    }

    /// <summary>是否正按住右键瞄准（腰射 ↔ 瞄准）。</summary>
    public bool Aiming => _aiming;

    // ==================== 闪光弹：玩家减速（2026-10-18）====================

    float _slowUntil;      // 减速截止时刻
    float _slowMul = 1f;   // 减速倍率（1 = 正常；0.5 = 半速）

    /// <summary>被闪光弹闪到时的**移动减速**（与敌人的那套参数完全分开，由效果卡单独给）。
    /// <paramref name="percent"/> = 减速百分比（0.5 = 速度剩一半）；时长到了自动恢复。</summary>
    public void ApplySlow(float percent, float seconds)
    {
        if (percent <= 0.001f || seconds <= 0.001f) return;
        _slowMul = Mathf.Clamp01(1f - Mathf.Clamp01(percent));
        _slowUntil = Mathf.Max(_slowUntil, Time.time + seconds);   // 重复命中取更长的那个
    }

    /// <summary>当前移动速度上限（米/秒）= **基准档 × 当前武器的速度系数**。**优先级：瞄准 &gt; 跑 &gt; 走**。
    /// 设计意图：让「瞄准」有代价（移动变慢），否则全程瞄准就是最优解；
    /// 而「开枪会退出奔跑」（见 <see cref="Fire"/>）保证腰射时也不会一边冲刺一边输出。
    /// 武器系数（`WeaponData`）：`moveSpeedMultiplier` 只管**行走 / 奔跑**，`aimMoveSpeedMultiplier` 只管**瞄准**
    /// —— 两档**互不叠加**（各管一档）；空手 / 缺省 = 1 = 纯基准速度。
    /// ⚠️ **唯一入口**：别在别处再写一遍"哪档 × 什么速度"（将来若改成"负重公式"，也只改这一行）。
    /// ⚠️ 系数**没有上限**（设计自由），但请守住不变量：`aimSpeed × aimMoveSpeedMultiplier ≤ walkSpeed × moveSpeedMultiplier`
    ///    —— 否则某把枪"按住右键"既更准又更快，腰射对它形同虚设（§3.11 的设计意图）。
    /// ⚠️ 取用时 `Mathf.Max(0f, ...)` 是**兜底**：手改 YAML 塞进负数会让移动方向反掉（`OnValidate` 只在编辑器生效）。</summary>
    public float CurrentMoveSpeed
    {
        get
        {
            // ⚠️ 持弹模式（手里是投掷物、枪收起）：移速 = 走 / 跑基准，**不看当前武器的速度系数**
            //    （用户 2026-10-12 定：手雷不受"这把枪跑得慢"这种武器性格影响）
            bool throwing = throwables != null && throwables.Active;
            float mulAim = (!throwing && weapon != null) ? Mathf.Max(0f, weapon.aimMoveSpeedMultiplier) : 1f;
            float mulMove = (!throwing && weapon != null) ? Mathf.Max(0f, weapon.moveSpeedMultiplier) : 1f;
            // ⭐ 闪光弹减速（2026-10-18）：玩家被闪到时也会变慢 —— 百分比/时长由效果卡**单独**给
            //    （与敌人那套分开调）。放在最后乘 ⇒ 对"瞄准/跑/走"三档一律生效。
            float slow = Time.time < _slowUntil ? _slowMul : 1f;
            return (_aiming ? aimSpeed * mulAim : (running ? runSpeed : walkSpeed) * mulMove) * slow;
        }
    }

    /// <summary>「腰射 ↔ 瞄准」的散布过渡进度：0 = 完全腰射，1 = 完全瞄准。
    /// 由 <see cref="AdvanceAds"/> 每帧按 `WeaponData.hipToAimTime` / `aimToHipTime` 定速推进；
    /// 对外暴露只为"将来做抬枪动画 / HUD 提示"用。
    /// ⚠️ 后坐倍率 / 移动速度 / 横向闸门**不看它**（仍看 `_aiming`，瞬间切换）。</summary>
    public float AdsProgress01 => _adsProgress;

    /// <summary>当前散布角（度）：在「腰射散布」与「瞄准散布」之间按**过渡进度**插值。
    /// ⚠️ **唯一入口**：开火（`Fire`）与散布圈（`Crosshair`）都读这里 —— 不要在别处各自读
    ///    `weapon.hipSpread` / `weapon.aimSpread`，否则"画出来的圈"与"实际弹道"会脱节
    ///    （与 §3.3「准星画在哪子弹就落在哪」是同一条原则）。
    /// ✅ 过渡**只有这一份实现**（`_adsProgress`）→ "屏幕上的圈在收缩"与"子弹在变准"天然同步。</summary>
    public float CurrentSpread => (weapon == null || (throwables != null && throwables.Active))
        ? 0f                                                    // 空手 / 持弹（枪收起）：没有散布这回事 → 散布圈自然隐藏
        : Mathf.Lerp(weapon.hipSpread, weapon.aimSpread, _adsProgress);

    /// <summary>当前纵向后坐（米）= 腰射纵向后坐 × 瞄准倍率（未瞄准时倍率 = 1）。</summary>
    public float CurrentRecoilUp => weapon == null ? 0f : weapon.hipRecoilUp * RecoilUpMultiplier;

    /// <summary>当前横向后坐（米）= 腰射横向后坐 × 瞄准倍率（未瞄准时倍率 = 1）。</summary>
    public float CurrentRecoilJitter => weapon == null ? 0f : weapon.hipRecoilJitter * RecoilJitterMultiplier;

    /// <summary>当前纵向后坐倍率（未瞄准 / 空手 = 1）。</summary>
    public float RecoilUpMultiplier => (_aiming && weapon != null) ? Mathf.Max(0f, weapon.aimRecoilUpMultiplier) : 1f;

    /// <summary>当前横向后坐倍率（未瞄准 / 空手 = 1）。
    /// ⚠️ **同时也用来同比缩放横向闸门上限**（`recoilMaxSide`），保证瞄准时横向带宽的相对松紧不变。</summary>
    public float RecoilJitterMultiplier => (_aiming && weapon != null) ? Mathf.Max(0f, weapon.aimRecoilJitterMultiplier) : 1f;

    /// <summary>当前换弹进度 0~1（未在换弹时返回 0）。准星用它画圆形进度环。</summary>
    public float ReloadProgress01
    {
        get
        {
            if (!reloading || weapon == null || weapon.reloadTime <= 0f) return 0f;
            return Mathf.Clamp01(1f - (_reloadUntil - Time.time) / weapon.reloadTime);
        }
    }

    void Awake()
    {
        // 单例防护：出现重复实例（如场景重载的极端时序）时丢弃后出现的那个
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        _rb = GetComponent<Rigidbody>();
        if (_rb != null)
        {
            // 允许**绕 Y 旋转**（MoveRotation 需要它），仍冻结 X/Z 旋转与 Y 位移：
            // 玩家模型是"绕 Y 对称的胶囊"，所以放开 Y 不会影响物理外观，但让"朝向"能走物理接口。
            _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ | RigidbodyConstraints.FreezePositionY;
            // 明确开启插值：物理 50Hz、渲染帧率更高 → 不开插值位置就是"每 1/50 秒跳一次"（看起来像小范围瞬移/掉帧）
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        _lastMousePos = Input.mousePosition;
        // 开局：准星落在一个**明确**的位置（之后只由"增量"驱动，绝对位置不再影响准星）。
        // ⚠️ 游玩时鼠标是锁定状态，Input.mousePosition 恒等于屏幕中心 —— 而玩家角色也大致在屏幕中心，
        // 那样开局准星会压在玩家身上、朝向退化。所以锁定/光标不在窗口内时，默认放在"屏幕中心的斜上方"
        // （= 一个明确的初始朝向）；未锁定时（编辑器按 Esc 解锁）才用真实光标位置。
        Vector2 m0 = Input.mousePosition;
        bool inside0 = m0.x >= 0f && m0.x <= Screen.width && m0.y >= 0f && m0.y <= Screen.height;
        // 第二条独立判据：准星离屏幕中心太近时，也一律改用默认位置。
        // 为什么不能只信 Cursor.lockState：同一 GameObject 的 Awake 顺序 = 组件顺序，而 PlayerController
        // 排在 Crosshair 之前 → 首帧光标必然还没被锁上。此时鼠标若恰好停在屏幕中心（= 玩家角色所在），
        // 准星就会压在玩家身上、aimDir 长度≈0 → 开局朝向退化（鼠标一动即自愈）。
        // 加上"离中心足够远"这条后，无论 Awake 顺序或鼠标位置如何，开局准星都不会压在角色上。
        float minFromCenter = Screen.height * 0.12f;
        bool farFromCenter = (m0 - new Vector2(Screen.width * 0.5f, Screen.height * 0.5f)).sqrMagnitude
                             >= minFromCenter * minFromCenter;
        _crosshairTarget = _crosshairEff = (inside0 && farFromCenter && Cursor.lockState != CursorLockMode.Locked)
            ? m0
            : new Vector2(Screen.width * 0.5f, Screen.height * 0.72f);

        // 容错：没手动拖相机时自动找主相机（走 CameraCache，避免重复按 Tag 查找）
        if (mainCamera == null) mainCamera = CameraCache.Main;

        // gunVisual 只是枪的挂载点，必须保持缩放为 1，否则挂上去的模型会被拉伸变形
        if (gunVisual != null)
        {
            gunVisual.localScale = Vector3.one;

            // 后坐动画（可选）：记下基准位置 + 缓存 Animation 引用（没挂组件就是 null → 该功能整体静默关闭）
            _gunBaseLocalPos = gunVisual.localPosition;
            _gunAnim = gunVisual.GetComponent<Animation>();
        }

        // 背包必存在：场景里没挂就自动补一个（死亡重开场景重载会自动重建为空背包）
        // 注意：BackpackPanel/Crosshair 等 UI 组件已预挂在 Player Prefab 上（方案 A），
        // 不再在运行时补挂；此背包兜底仅防御「手动搭场景漏挂」的情况
        if (backpack == null) backpack = GetComponent<PlayerBackpack>();
        if (backpack == null) backpack = gameObject.AddComponent<PlayerBackpack>();

        LoadMirrorFromSlot();
    }

    /// <summary>把当前快捷槽(backpack.currentSlot)状态同步到 weapon/magAmmo 镜像,并刷枪模型。
    /// ⚠️ 提到 `internal`（2026-10-26）：训练图脚本 `TrainingStartWeapon` 在 `Start` 里改完背包后要补调一次
    ///    （它自己的 `Awake` 早于本类的 `Awake`，不能再靠反射——改方法名会静默失效）。</summary>
    internal void LoadMirrorFromSlot()
    {
        // 这些运行时状态必须先复位，不能放在下面的分支之后：
        // Player.prefab 里存着 reloading=1 之类的脏值，若走「空手 / 空槽」早退分支就不复位，
        // 首帧会处于「换弹中」（虽然下一帧会自愈，但属于侥幸，见交接文档 §5.2）。
        reloading = false;
        _switchTarget = null;   // 切枪 / 丢弃 → 取消"按 T 换级"（否则退弹会执行、却没有新弹可装）
        _nextFireTime = 0f;   // 切枪后立刻能开火，不继承上一把的射速冷却
        _reloadUntil = 0f;
        // 切枪：清掉回正标记与本次射击周期的推动量（准星本身不动 —— 它已是唯一真相，没有"偏移"要复位）
        _markValid = false;
        _recovering = false;
        _recoverSpeed = 0f;
        _latAccum = 0f;                  // 横向账本清零（基准方向在下次按下扳机时重取）
        _undisturbedSince = Time.time;   // 开局/切枪：静默期从此刻重新计时（新武器有自己的回正延迟）
        _mouseDeviation = Vector2.zero;  // 手抖余量窗口一并复位

        var s = (backpack != null
                 && backpack.slots != null
                 && backpack.currentSlot >= 0
                 && backpack.currentSlot < backpack.slots.Length)
            ? backpack.slots[backpack.currentSlot] : null;

        var prevWeapon = weapon;
        weapon = s != null ? s.weapon : null;
        magAmmo = s != null ? s.magAmmo : 0;
        magAmmoId = s != null ? s.ammoId : null;   // 弹匣等级与余弹**同源同生**（漏了它 = 换枪后等级漂移）

        // ⚠️ **"等级未知"必须在这里就落实成具体一张卡**，不能留到开火时才猜 ——
        //    否则同一个弹匣会同时存在三种说法（2026-09-27 独立审计查出）：
        //      · HUD 按 `LowestOwnedAmmo` **预览**成二级 → 显示 `30/30 Ⅱ`
        //      · `Fire` 因为 null → 倍率 1.0 → 打出去的是一级的伤害
        //      · 按 T 以为"已经是二级" → 提示"没有其他等级"
        //    触发路径很常见：捡地上的新枪 / 敌人掉的枪（`magAmmo = -1` 满弹、`ammoId = null`），
        //    而背包里当时只有二级弹。
        //    落实后**立即写回槽**，保证 镜像 / 槽 / HUD / 伤害 / T 永远读同一张卡。
        //    （`StartReload` 里也有一份同样的落实 —— 两处都走 `ResolveAmmo` 这个唯一出口，双保险。）
        if (weapon != null && weapon.ammoKind != AmmoKind.None && magAmmoId == null)
        {
            var pick = ResolveAmmo(weapon, null);
            if (pick != null)
            {
                magAmmoId = pick;
                if (backpack != null && backpack.currentSlot >= 0 && backpack.currentSlot < backpack.slots.Length)
                {
                    backpack.slots[backpack.currentSlot].ammoId = pick;
                    backpack.slots[backpack.currentSlot].magAmmo = magAmmo;
                }
            }
        }

        // 换枪（或变成空手）→ **换手**：散布过渡归零 + 走 `BeginHandSwap()`（重按右键 + 起算切枪锁火窗，
        // 这两件事的说明都在那个方法上）。于是"按住右键换枪"不会白嫖满瞄准精度、新枪默认保持腰射，
        // 而且**换手那一下打不出枪**（堵掉"按住左键猛切枪刷射速"）。
        // ⚠️ 判据是**武器实例是否变化**，且这里是「换手」判据的**唯一一处**：捡到同类武器只是进库存、
        //    手上那把没换（引用不变）时，既不打断正在进行的瞄准、也不锁火；按同一个槽号同理
        //    （按住数字键不会把自己锁住）。→ 1/2/3、滚轮、捡枪自动装备、丢弃后换枪**全部自动覆盖**。
        if (!ReferenceEquals(weapon, prevWeapon))
        {
            _adsProgress = 0f;
            BeginHandSwap();
        }

        // 任何分支都要刷枪模型：否则「丢掉手上的枪 / 空手」时旧模型会留在 gunVisual 上一直显示
        UpdateGunVisual();
    }

    /// <summary>切快捷槽前把当前弹匣写回背包,避免弹匣状态丢失(i = 目标槽; 空槽忽略)。
    /// `public`（2026-10-27）：快捷槽右键菜单的「装备」= 切换手持。</summary>
    public void SwitchSlot(int i)
    {
        if (backpack == null || i < 0 || i >= backpack.slots.Length) return;
        if (backpack.slots[i].weapon == null) return;   // 空槽不切
        if (throwables != null) throwables.OnWeaponSwitch();   // 切到枪 = 退出投掷模式
        if (ammoSel != null) ammoSel.OnWeaponSwitch();          // 切到枪 = 取消弹药等级选择器（目标口径会变）
        // 先把手上这把的弹匣存回去 —— **唯一实现**在 `SaveCurrentSlotAmmo()`，别在这里再抄一遍
        // （抄成两份的后果就是"以后改保存规则只改一处"，与 2026-09-26 那个复制 bug 同源）
        SaveCurrentSlotAmmo();
        backpack.currentSlot = i;
        LoadMirrorFromSlot();
    }

    /// <summary>滚轮循环切槽：沿 dir 方向找下一个**非空**槽（跳过空槽、绕圈找）；找不到别的武器就什么也不做</summary>
    void CycleSlot(int dir)
    {
        if (backpack == null) return;
        int n = backpack.slots.Length;
        for (int step = 1; step < n; step++)   // step 最大到 n-1：绕完一圈回到自己 = 没有别的武器
        {
            int i = ((backpack.currentSlot + dir * step) % n + n) % n;
            if (backpack.slots[i].weapon != null) { SwitchSlot(i); return; }
        }
    }

    void Start()
    {
        // 玩家死亡 → 置 GameOver，HUD 才会弹出死亡结算面板并停止游戏
        _hp = GetComponent<Health>();
        if (_hp != null) _hp.OnDeath += OnPlayerDeath;

        // 魔法管理器：兜底自动挂（**不需要**往场景 / prefab 里预配 —— 与背包同一套做法）。
        // ⚠️ 必须在 `Start` 而不是 `Awake`：确保 PlayerBackpack 已经 Awake 过，
        //    PlayerMagic.Awake 里那次 GetComponent<PlayerBackpack>() 才拿得到引用。
        if (magic == null) magic = GetComponent<PlayerMagic>();
        if (magic == null) magic = gameObject.AddComponent<PlayerMagic>();

        // 投掷物管理器：同样兜底自动挂（**不需要**往场景 / prefab 里预配）
        if (throwables == null) throwables = GetComponent<PlayerThrowable>();
        if (throwables == null) throwables = gameObject.AddComponent<PlayerThrowable>();

        // 弹药等级选择器：同样兜底自动挂（**不需要**往场景 / prefab 里预配）
        if (ammoSel == null) ammoSel = GetComponent<AmmoSelector>();
        if (ammoSel == null) ammoSel = gameObject.AddComponent<AmmoSelector>();

        // 按开局自带的武器刷新一次手上的枪模型（Inspector 里配了武器就要走这一步）
        UpdateGunVisual();
    }

    /// <summary>玩家死亡回调：置 GameOver</summary>
    void OnPlayerDeath() => GameState.GameOver = true;

    void OnDestroy()
    {
        if (_hp != null) _hp.OnDeath -= OnPlayerDeath;
    }

    /// <summary>
    /// 校验（仅编辑器生效）：把手感关键参数夹到合法范围。
    /// recoilKickSpeed = 0 会让「后坐过渡」永远追不上目标值 → 后坐永久滞后、准星与真实弹道持续错位。
    /// 只用 if 判断，避免把合法值也标成「已修改」。
    /// </summary>
    void OnValidate()
    {
        if (recoilKickSpeed < 0.01f) recoilKickSpeed = 0.01f;
        if (mousePixelScale < 0.01f) mousePixelScale = 0.01f;
        if (walkSpeed < 0f) walkSpeed = 0f;
        if (runSpeed < 0f) runSpeed = 0f;
        if (aimSpeed < 0f) aimSpeed = 0f;
        if (accel < 0f) accel = 0f;
        if (recoilScreenMargin < 0f) recoilScreenMargin = 0f;
        if (recoilScreenMargin > 0.45f) recoilScreenMargin = 0.45f;
        if (recoilRecoverMaxTime < 0.05f) recoilRecoverMaxTime = 0.05f;
        if (recoilRecoverJitterRatio < 0f) recoilRecoverJitterRatio = 0f;
        if (recoilRecoverJitterRatio > 0.05f) recoilRecoverJitterRatio = 0.05f;
        if (headshotGateScale < 0f) headshotGateScale = 0f;
        if (headshotGateScale > 1.5f) headshotGateScale = 1.5f;   // 再大就会把「瞄准身体中心」也判成爆头（见字段说明）
        if (headshotRadiusRatio < 0f) headshotRadiusRatio = 0f;
        if (bodyAimRadius < 0.05f) bodyAimRadius = 0.05f;
    }

    void Update()
    {
        if (GameState.GameOver || GameState.Paused)
        {
            _moveInput = Vector2.zero; // 死亡/暂停后清空输入，防止残留按键让尸体滑行或角色乱动
            _lastMousePos = Input.mousePosition;   // 刷新基准：否则暂停期间鼠标的移动会在恢复那一帧变成巨大增量、把准星甩飞
            _mouseDelta = Vector2.zero;
            _aiming = false;                       // 暂停/死亡时退出瞄准：否则恢复后残留瞄准态（右键状态在暂停期间读不到）
            _adsProgress = 0f;                     // 散布过渡进度一并归零：恢复后不会残留"半瞄准"的散布
            // ⚠️ 投掷物：这里必须"喂一拍"给它 —— 它的 `Tick` 里才有"暂停关选择器 / 死亡退出持弹模式"的收敛，
            //    而本分支会在 Tick 之前早退 → 不喂的话选择器列表会**一直留在屏幕上**（2026-10-12 修的 bug）。
            if (throwables != null) throwables.Tick(0f);
            return;
        }

        // ---- 移动输入 ----
        _moveInput = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        if (_moveInput.sqrMagnitude > 1f) _moveInput.Normalize();

        // ---- Shift 点按：切换 跑/走 ----
        if (Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift))
            running = !running;

        // ---- 数字键 1/2/3：切换快捷武器槽 ----
        if (Input.GetKeyDown(KeyCode.Alpha1)) SwitchSlot(0);
        if (Input.GetKeyDown(KeyCode.Alpha2)) SwitchSlot(1);
        if (Input.GetKeyDown(KeyCode.Alpha3)) SwitchSlot(2);

        // ---- G：投掷物（不在投掷模式 → 进入；已在 → 开关选择器）（2026-10-12）----
        // 逻辑全在 `PlayerThrowable`（与魔法同一套做法：本类只留一个按键入口）。
        if (Input.GetKeyDown(KeyCode.G) && throwables != null) throwables.OnKeyG();

        // 本帧是否"手里拿着投掷物"（决定 右键 / 左键 / R / T / 移速 的语义）—— 每帧只算一次
        bool throwing = throwables != null && throwables.Active;
        // 本帧是否"弹药等级选择器开着"（决定 左键 = 确认换级；不射击 / 不记后坐 / 不瞄准）—— 每帧只算一次
        bool ammoSelecting = ammoSel != null && ammoSel.SelectorOpen;

        // ---- 鼠标滚轮：优先级 = 投掷物选择器 > 弹药等级选择器 > 切武器槽 ----
        // 选择器开着时滚轮归它（选档）；否则循环切换武器槽。攒够"一整格"才动一次：标准鼠标一格 = ±1，
        // 高精度滚轮 / 触摸板给的是零头，不攒会切得飞快。
        float scrollY = Input.mouseScrollDelta.y;
        if (!(throwables != null && throwables.HandleScrollDelta(scrollY))
            && !(ammoSel != null && ammoSel.HandleScrollDelta(scrollY)))
        {
            _scrollAccum += scrollY;
            while (_scrollAccum >= 1f) { _scrollAccum -= 1f; CycleSlot(-1); }
            while (_scrollAccum <= -1f) { _scrollAccum += 1f; CycleSlot(+1); }
        }
        // 背包开关（Tab/B）由 BackpackPanel.Update 统一处理（此处不再监听，避免同帧双触发）

        // ---- 鼠标右键：瞄准（腰射 ↔ 瞄准）。只改「当前散布 / 当前后坐」，弹道算法完全不变 ----
        // 用 GetMouseButton（按住才算瞄准）。暂停/死亡分支已在上面 early-return 并把 _aiming 复位。
        // ⚠️ 状态推进收在 `UpdateAimState`（含"切枪后必须重新按下"的锁存），别把 `_aiming = ...` 写回来。
        // ⚠️ 持弹模式：右键是「弹道预览」、不是瞄准 → 不进瞄准态（预览绘制在 `PlayerThrowable.LateUpdate`）。
        UpdateAimState(Input.GetMouseButton(1) && !throwing && !ammoSelecting);
        // 散布过渡推进：**必须在本帧开火（HandleFire）与准星绘制（Crosshair.LateUpdate）之前** ——
        // 这样"这一枪用的散布"和"屏幕上画出来的圈"是同一个值（同源原则）。
        AdvanceAds(Time.deltaTime);

        // ---- Q 使用血包（未满血才可用；无血包/满血给提示，不静默）----
        if (Input.GetKeyDown(KeyCode.Q)) TryUseMedkit();

        // ---- 鼠标增量（每帧只取一次）：准星由"增量"驱动，不再由鼠标绝对位置驱动 ----
        _mouseDelta = TakeMouseDelta();
        // 手抖余量：把增量累加成「净位移」（死区以下的丢弃），由**累计量**判断"是否真的在动" —— 与帧率无关（§4 #12 注意1）
        if (_mouseDelta.sqrMagnitude > MouseMoveEpsSq) _mouseDeviation += _mouseDelta;
        // 余量 = 屏幕短边（横屏下即高度）的比例 → 与分辨率无关：每帧换算成像素再和净位移长度比
        float jitterTolPx = Mathf.Min(Screen.width, Screen.height) * recoilRecoverJitterRatio;
        bool mouseDisturbed = _mouseDeviation.sqrMagnitude > jitterTolPx * jitterTolPx;

        // ---- 准星（屏幕空间唯一真相）：鼠标增量 + 回正 + kick 平滑 + 夹取 ----
        UpdateCrosshair();

        // ---- 准星指向（把准星屏幕坐标投射到瞄准平面）----
        UpdateAimPoint();
        // ---- 弹道方向（唯一入口：每帧算一次并缓存；准星进死区时冻结）----
        UpdateBulletDir();
        // 朝向基准 = **弹道方向**（`BulletDir`）：朝向 / 弹道 / 激光三者同轴、玩家朝向与弹道平行
        // （用户 2026-09-26 定）；枪是本物体的子物体、跟着一起转，所以不必单独旋转枪。
        // ⚠️ `BulletDir` 的基准点是 `AimBasis()`（枪轴上离玩家最近的点）、**不是枪口** —— 理由见那个方法。
        //    基准点仍由上一物理步的旋转决定（`MoveRotation` 延迟生效）→ 仍是"每帧迭代一次"的压缩映射，
        //    但杠杆臂从枪口的 0.96m 缩到 0.45m → 自激区面积小了 4 倍多，剩下的由 `aimDeadZoneRadius` 兜住。
        // ⚠️ 后坐动画只驱动枪的 localPosition.z、不碰旋转，所以与本行无冲突。
        Vector3 aimDir = BulletDir;
        if (aimDir.sqrMagnitude > 0.001f)
        {
            // ⚠️ **不要用 `transform.rotation = ...`**：刚体开了插值后"物理系统接管 transform"，
            //    每帧直接写 transform 会被当作"外部改动/瞬移"，**把位置插值打断** → 位置退化成
            //    "每 1/50 秒跳一次"（玩家看起来就是"小范围瞬移 + 掉帧感"，实测报障）。
            //    正确接口是 `Rigidbody.MoveRotation` —— 官方说明它"符合刚体的插值设置"，会渲染中间帧的平滑过渡。
            var look = Quaternion.LookRotation(aimDir.normalized);
            if (_rb != null) _rb.MoveRotation(look);
            else transform.rotation = look;
        }

        // ---- 回正标记点 + 回正任务（必须放在 HandleFire 之前：标记点/静默期/横向基准都要用"开火前"的状态）----
        // ⚠️ 持弹模式 / 弹药选择器模式下左键都不是"开火"（投掷 / 确认换级）→ 标记点/回正任务按"没按键"处理（别记错后坐标）
        UpdateRecoveryTask(Input.GetMouseButtonDown(0) && !throwing && !ammoSelecting,
                            Input.GetMouseButton(0) && !throwing && !ammoSelecting, mouseDisturbed);

        // ---- 开火 / 投掷 / 确认弹药换级（互斥：左键语义随模式变）----
        if (throwing) throwables.HandlePrimaryInput();
        else if (ammoSel != null && ammoSel.SelectorOpen) { if (Input.GetMouseButtonDown(0)) ammoSel.Confirm(); }
        else HandleFire();

        // ---- 换弹（持弹模式下换弹 / 切弹药等级都无意义：枪不在手上）----
        if (Input.GetKeyDown(KeyCode.R) && weapon != null && !reloading && !throwing && magAmmo < weapon.magSize)
            StartReload();
        // ---- T：弹药等级选择器（按 T 开/关、滚轮选档、再按 T / 左键提交换级；仿投掷物选择器）----
        // 逻辑全在 `AmmoSelector`（`PlayerController` 只留这一个按键入口）；与 R 互斥（同一个 `reloading` 挡）。
        if (Input.GetKeyDown(KeyCode.T) && weapon != null && !reloading && !throwing && ammoSel != null)
            ammoSel.OnKeyT();
        if (reloading && Time.time >= _reloadUntil) FinishReload();

        // ---- E：释放魔法（主动魔法；被动魔法不需要按键，装备即生效）----
        // ⭐ 这里只留「一个按键入口」，逻辑全在 PlayerMagic 里（本类已经 1400+ 行，别再往里塞）。
        // ⚠️ E 曾经是"拾取"键；2026-09-28 起拾取/开箱/开商店统一为 F，E 让位给魔法。
        if (Input.GetKeyDown(KeyCode.E) && magic != null) magic.TryCast();

        // ---- 魔法：每帧推进（核对装备变化 + 冷却 + 行为自己的 Tick）----
        // 放在交互之前：交互（F）可能在这一帧开面板 → 下一帧起暂停，魔法自然停摆（与武器一致）。
        if (magic != null) magic.Tick(Time.deltaTime);

        // ---- 投掷物：每帧推进（库存驱动的自动递补 / 选择器超时 / 死亡或暂停时收敛）----
        if (throwables != null) throwables.Tick(Time.deltaTime);
        if (ammoSel != null) ammoSel.Tick(Time.deltaTime);

        // ---- 交互（**单一交互焦点**）：地上的武器 / 血包 / 箱子中"离玩家最近的那个"显示气泡并响应按键 ----
        //    ⚠️ 唯一入口：别在别的脚本里再自己扫一遍 —— 那会抢 HUD 里那唯一一个气泡
        InteractFocus.Tick(this);
    }

    void FixedUpdate()
    {
        if (GameState.GameOver || GameState.Paused) { if (_rb != null) _rb.velocity = Vector3.zero; return; }

        if (_rb == null) return;

        float speed = CurrentMoveSpeed;   // 瞄准 > 跑 > 走（见该属性的注释）
        Vector3 targetVel = new Vector3(_moveInput.x, 0f, _moveInput.y) * speed;
        var v = _rb.velocity;
        v.x = Mathf.MoveTowards(v.x, targetVel.x, accel * Time.fixedDeltaTime);
        v.z = Mathf.MoveTowards(v.z, targetVel.z, accel * Time.fixedDeltaTime);
        _rb.velocity = v;
    }

    /// <summary>准星推进（屏幕空间，模型核心）。顺序：鼠标增量 → 回正 → kick 平滑 → 夹取。
    /// **鼠标增量同时加到目标值与过渡值上**，所以鼠标永远 1:1 跟手（绝不被 kick 平滑拖慢）；
    /// 只有"后坐冲量"走进目标值，靠过渡值追它 —— 这就是"被推出去的那段过程"。</summary>
    void UpdateCrosshair()
    {
        // 鼠标增量：目标值与过渡值同时加（1:1 跟手）
        _crosshairTarget += _mouseDelta;
        _crosshairEff += _mouseDelta;

        // 回正：把"目标值"以锁定的速度推向标记点；到位即清点（回正任务完成）
        if (_recovering)
        {
            _crosshairTarget = Vector2.MoveTowards(_crosshairTarget, _mark, _recoverSpeed * Time.deltaTime);
            if ((_crosshairTarget - _mark).sqrMagnitude < 0.25f)
            {
                _crosshairTarget = _mark;
                _recovering = false;
                _markValid = false;      // 到位：标记点用掉（同一时刻最多存在一个）
            }
        }

        // kick 平滑：过渡值匀速追目标值（大后坐看得见推出过程、小后坐近似瞬时）。
        // 回正中若回正速度比 kick 快，过渡值也按回正速度追 —— 否则渲染出的回正会被 kick 限速、超过时长承诺。
        float kickPx = recoilKickSpeed * _pxPerM;
        float chase = (_recovering && _recoverSpeed > kickPx) ? _recoverSpeed : kickPx;
        _crosshairEff = Vector2.MoveTowards(_crosshairEff, _crosshairTarget, chase * Time.deltaTime);

        // 夹进屏幕安全区：准星永不出屏（屏幕系下的精确保证，目标值与过渡值一起夹）
        float m = Mathf.Min(Screen.width, Screen.height) * Mathf.Clamp(recoilScreenMargin, 0f, 0.45f);
        _crosshairTarget = ClampToSafeRect(_crosshairTarget, m);
        _crosshairEff = ClampToSafeRect(_crosshairEff, m);
    }

    /// <summary>取本帧鼠标增量（屏幕像素）。优先级：
    /// **① 已锁定 / 已校准 → 轴增量**：这是"无限行程"的关键 —— 压枪会消耗鼠标行程，
    /// 用光标绝对位移的话，长时间压枪后鼠标贴到屏幕边缘、准星再也拉不过去（见交接文档 §4 #21）；
    /// 轴增量不受光标位置限制，压多久都不会"走不了"。
    /// **② 未锁定且未校准 → 光标位移**：既当兜底，也用来"实测"轴增量的换算系数（同帧两路相除）。
    /// 两种情况都要挡住垃圾位移（锁定态下 Input.mousePosition 返回屏幕外的大数值，实测 (34174,-31649)）。</summary>
    Vector2 TakeMouseDelta()
    {
        bool locked = Cursor.lockState == CursorLockMode.Locked;

        Vector2 axis = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));

        Vector2 pos = Input.mousePosition;
        Vector2 posDelta = pos - _lastMousePos;
        _lastMousePos = pos;

        bool posValid = !locked
            && pos.x >= 0f && pos.x <= Screen.width && pos.y >= 0f && pos.y <= Screen.height
            && posDelta.sqrMagnitude <= MaxMouseDeltaSq;

        if (posValid) TryCalibrateMouseScale(posDelta, axis);   // 未锁定的有效帧：顺手校准

        if (locked || _scaleCalibrated) return axis * mousePixelScale;
        return posValid ? posDelta : Vector2.zero;
    }

    /// <summary>实测"轴增量 → 屏幕像素"的换算系数：同一帧里光标位移 ÷ 轴增量 就是它。
    /// 为什么能这么测：InputManager 的灵敏度在运行时读不到（无 API），但**光标位移本身是精确的像素量**，
    /// 两路数据同帧相除即可反推出系数 —— 于是准星的移动量与"未锁定时光标会移动的量"完全一致，不需要手调。
    /// 输入做成参数（而不是在函数里读 Input）：便于注入值单独验证。</summary>
    void TryCalibrateMouseScale(Vector2 posDelta, Vector2 axisDelta)
    {
        if (axisDelta.sqrMagnitude < CalibMinAxisSq) return;    // 轴增量太小：比例噪声大，丢弃
        float k = Mathf.Sqrt(posDelta.sqrMagnitude / axisDelta.sqrMagnitude);
        if (k < 0.1f || k > 200f) return;                       // 离谱比例：丢弃（防脏数据）
        // 已校准后只接受"与当前值同量级"的样本：否则一个离群样本（实测比例 100 的脏数据）会把系数拉偏 3 倍。
        // 允许 0.5~2 倍是为了仍能跟随"用户改了 InputManager 灵敏度"这类真实变化，只是收敛得慢一点。
        if (_scaleCalibrated && (k < mousePixelScale * 0.5f || k > mousePixelScale * 2f)) return;
        mousePixelScale = _scaleCalibrated ? Mathf.Lerp(mousePixelScale, k, 0.25f) : k;   // 多帧平滑收敛
        _scaleCalibrated = true;
    }

    /// <summary>把准星屏幕坐标反投影到瞄准平面（准星指向的世界坐标），并实测该点处的"像素/米"换算系数。
    /// 准星 = 唯一真相：弹道、朝向、准星显示全部由它派生，不存在第二个基准点。</summary>
    void UpdateAimPoint()
    {
        var cam = mainCamera != null ? mainCamera : CameraCache.Main;
        if (cam == null) return;

        Ray ray = cam.ScreenPointToRay(_crosshairEff);
        if (Mathf.Abs(ray.direction.y) >= 0.0001f)
        {
            float t = (GameConst.AimPlaneY - ray.origin.y) / ray.direction.y;
            if (t > 0f) _aimPoint = ray.origin + ray.direction * t;
        }

        // 实测该点处的 像素/米（沿相机右方向量 1 米的投影长度；俯角 60° 下纵横差异 ~6%，手感无感）
        Vector3 rightG = cam.transform.right; rightG.y = 0f;
        if (rightG.sqrMagnitude > 0.0001f)
        {
            float px = (cam.WorldToScreenPoint(_aimPoint + rightG.normalized) - cam.WorldToScreenPoint(_aimPoint)).magnitude;
            if (px > 1f) _pxPerM = px;
        }
    }

    /// <summary>**瞄准基准点**（世界坐标）：枪的轴线上、离玩家中心**最近**的那个点。
    ///
    /// 为什么不用枪口：朝向由「基准点 → 准星」方向决定，而基准点随朝向转 → 形成跨帧反馈环；
    /// 环的**发散阈值 = 基准点到玩家中心的距离**（杠杆臂越长越容易自激）。
    /// 实测：枪口离玩家 0.96m（死区得开 0.96m 才安全）→ 换成本点只要 0.45m（死区面积降到约 1/4.5）。
    ///
    /// 为什么必须落在「枪的轴线」上：轴线过挂点、沿朝向。轴上的点与枪口**共线**（实测夹角 0.000°），
    /// 于是「轴线指向准星」这个条件完全等价 → **朝向的解一点不变**、枪管照样精确指着准星、弹道零偏差。
    /// ⚠️ 换成不在轴上的点（例如「过玩家中心作垂线求交」）会改变解、把弹道带偏 —— 别再试。
    ///
    /// 实现：把挂点的本体坐标里「沿朝向的分量」（z）清零，再转回世界 = 沿轴线投影到垂足。
    /// 用 Quaternion 而非 `InverseTransformPoint`，避免被 transform 缩放污染。</summary>
    Vector3 AimBasis()
    {
        Vector3 p = gunVisual != null ? gunVisual.position : transform.position + transform.forward * 0.6f;
        Vector3 local = Quaternion.Inverse(transform.rotation) * (p - transform.position);
        local.z = 0f;
        return transform.position + transform.rotation * local;
    }

    /// <summary>每帧算一次 <see cref="BulletDir"/> 并缓存（**唯一入口**，在 `UpdateAimPoint()` 之后调用）。
    ///
    /// 死区语义：准星点离基准点 &lt; `aimDeadZoneRadius` 时**不更新方向**（保持最后有效值）——
    /// 那一刻准星失去瞄准作用、只保留开火；否则该距离下「基准点 → 准星」的方向会被反馈环放大成自激旋转。
    /// 冻结值一路传到 朝向 / 弹道 / 激光（它们都读 `BulletDir`）→ 表现是「枪锁住不动」，可预测。</summary>
    void UpdateBulletDir()
    {
        float dist;
        Vector3 raw = RawAimDir(out dist);

        if (dist < aimDeadZoneRadius)
        {
            // 死区：能冻就冻；从未解算过（开局第一帧就踩进死区）→ 用同一套算法的结果兜底，避免零方向
            if (!_bulletDirValid) { _bulletDir = raw; _bulletDirValid = true; }
            return;
        }

        _bulletDir = raw;
        _bulletDirValid = true;
    }

    /// <summary>「瞄准基准点 → 准星点」的原始方向（压平 y、已归一化），并回传两者的**水平距离**。
    /// **唯一实现**：`UpdateBulletDir()`（本帧缓存）与 `BulletDir` 的兜底路径共用这一份，避免两处算法漂移。
    /// 退化（两点几乎重合）时用玩家朝向兜底 —— 那时方向本来就没有意义，交给死区冻结。</summary>
    Vector3 RawAimDir(out float dist)
    {
        Vector3 toAim = _aimPoint - AimBasis();
        toAim.y = 0f;
        dist = toAim.magnitude;
        return dist > 0.0001f ? toAim / dist : transform.forward;
    }

    /// <summary>把屏幕坐标夹进安全区（四周留出 marginPx 余量）</summary>
    static Vector2 ClampToSafeRect(Vector2 p, float marginPx)
    {
        p.x = Mathf.Clamp(p.x, marginPx, Screen.width - marginPx);
        p.y = Mathf.Clamp(p.y, marginPx, Screen.height - marginPx);
        return p;
    }

    /// <summary>回正标记点 + 回正任务的推进。规则（用户设计）：
    ///   ① 按下左键那一帧 → 记下标记点 P = 那一刻准星所在位置
    ///   ② 射击期间不回正（后坐只累积，推力完整送达）
    ///   ③ 松手后**静置满 `weapon.recoilRecoverDelay` 秒**（期间不再次开火、不移动鼠标、不换弹）才**开始**回正；
    ///      准星在 ~recoilRecoverMaxTime 内回到 P，到位后 P 用掉。延迟为 0 时即旧行为（松手且鼠标未动就立即回正）
    ///   ④ 鼠标**累计净位移超过 `recoilRecoverJitterRatio`（手抖余量 = 屏幕短边比例）** 时：
    ///      · **还在开火** → P **搬到你调整后的准星位置**（于是松手后回正撤销的是"你最后一次动手之后"那段后坐；
    ///        全程不动手时 P 就是按下扳机那一刻，与旧行为一致；连压枪时 P≈松手时的准星 → 回正距离≈0，也与旧行为一致）
    ///      · **已经松手** → P 作废（回正不能跟鼠标抢控制）。净位移在余量以内视为手抖，**不打断**。同一时刻最多一个 P
    ///   ⑤ 开火 / 换弹会刷新静默计时（= 刷新"最后一次扰动"），所以连射是"松手那一刻"才开始计延迟
    /// 为什么需要 P：本模型里锚点恒等于准星，没有"偏移归零"可当目标，所以回正必须显式给一个目标。
    /// 输入做成参数（而不是直接在函数里读 Input）：这样按下/移动两条分支可以用注入值单独验证。</summary>
    void UpdateRecoveryTask(bool fireDown, bool fireHeld, bool mouseDisturbed)
    {
        if (fireDown)
        {
            _mark = _crosshairEff;      // ① 记点（此刻准星还没被这一发推动，正是"按下扳机时瞄的地方"）
            _markValid = true;
            _latAccum = 0f;             // ② 横向账本清零，并取"本梭子的左右基准方向"
            CaptureLatRef();
            _mouseDeviation = Vector2.zero;   // 新手抖余量窗口：净位移从记点这一刻重新累计
            _recovering = false;        // 新射击周期：上一次回正作废
        }
        else if (_markValid && mouseDisturbed)
        {
            if (fireHeld)
            {
                // ④-a 开火中动手（主动修正 / 转火）→ **不作废，而是把标记点搬到你调整后的准星位置**。
                // 语义：松手后回正撤销的是"你最后一次动手之后"积起来的那段后坐。
                //   · 全程不动手 → 标记点仍是按下扳机那一刻 → 与旧行为完全一致（点射免费不变）
                //   · 一直压枪 → 每帧都在更新，标记点≈松手时的准星 → 回正距离≈0 → 也与旧行为一致
                // ⚠️ 必须搬"当前准星位置"（含后坐位移），**不能**搬"扣掉后坐的瞄准位置"——
                //    后者会把玩家的压枪当成"要回正掉的偏移"，松手时反而朝压枪方向把准星拽回去。
                _mark = _crosshairEff;
                CaptureLatRef();                  // 标记点搬了 → 左右基准方向跟着重取（仍然是"由玩家动作离散更新"）
                _mouseDeviation = Vector2.zero;   // 从新位置重新累计（否则本帧之后每帧都会再搬一次 → 等于连续跟随）
            }
            else
            {
                _markValid = false;     // ④-b 已经松手才动手 → 仍按旧规则作废
                _recovering = false;
            }
        }

        // ⑤「最后一次扰动」推进：只要本帧仍在开火 / 鼠标**真的**在动（超余量）/ 换弹，就把静默计时刷新到现在。
        // 开火中每帧刷新 → 松手那一刻开始计延迟；点射时则是最后一发那一刻开始计。
        // ⚠️ 这里用 mouseDisturbed（超余量）而非单帧量：否则手抖会把静默期无限往后推、回正永远不开始。
        if (fireHeld || mouseDisturbed || reloading) _undisturbedSince = Time.time;

        // ③ 回正延迟（WeaponData.recoilRecoverDelay）：停止开火后必须静置够这么久，才允许开始回正。
        // 0 = 立即（旧行为）。它只决定"何时开始"；回正速度/总时长仍由下面的 _recoverSpeed 逻辑决定。
        float delay = weapon != null ? Mathf.Max(weapon.recoilRecoverDelay, 0f) : 0f;
        bool quietEnough = Time.time - _undisturbedSince >= delay;

        bool canRecover = _markValid && !fireHeld && !reloading && quietEnough;   // 松手 + 点还在 + 静置够久
        if (canRecover)
        {
            if (!_recovering)
            {
                _recovering = true;
                float baseSpeed = (weapon != null ? weapon.recoilRecover : 1f) * _pxPerM;   // 米/秒 → 像素/秒
                float maxTime = Mathf.Max(recoilRecoverMaxTime, 0.05f);
                // 速度 = max(武器原速, 起始距离 ÷ 时长上限) → 小幅按原速回、大幅最多 maxTime 秒回完。
                // 必须在开始时算一次并锁住：每帧重算「剩余距离 ÷ 上限」会让速度随剩余距离衰减，总时间被拉长。
                _recoverSpeed = Mathf.Max(baseSpeed, (_crosshairTarget - _mark).magnitude / maxTime);
            }
        }
        else
        {
            _recovering = false;
        }
    }

    /// <summary>
    /// 取"本梭子的左右基准方向" = 玩家屏幕位置 → 准星（标记点）。供 recoilMaxSide 的横向闸门判断"哪边是左右"。
    /// ⚠️ **只在按下扳机 / 开火中动手（标记点搬家）时调用** —— 由玩家动作**离散**更新，绝不每帧重算：
    ///    玩家就在屏幕正中，准星靠近玩家时"玩家→准星"方向会快速旋转甚至翻转（实测 5px 距离下一发之间能从 0° 跳到 90°），
    ///    每帧重算它正是 §4 #28 那个"单发瞬跳几百像素"的根因。退化（准星几乎压在玩家身上）时沿用上一次。
    /// </summary>
    void CaptureLatRef()
    {
        var cam = mainCamera != null ? mainCamera : CameraCache.Main;
        if (cam == null) return;
        Vector3 ps = cam.WorldToScreenPoint(transform.position);
        if (ps.z <= 0f) return;
        Vector2 d = _crosshairEff - new Vector2(ps.x, ps.y);
        if (d.sqrMagnitude < 4f) return;      // 距离 < 2px：方向无意义，沿用上次
        _latRefDir = d.normalized;
    }

    /// <summary>推进「右键瞄准」状态：`按住右键 && 不需要重新按下`。
    /// 「切枪后必须重新按下」的锁存在换枪时置位（`LoadMirrorFromSlot`）→ 右键仍按着也保持腰射；
    /// **松开右键即解除**（于是下次按下就是"重新按了一次"、正常进入瞄准）。
    /// ⚠️ 这是 `_aiming` 的**唯一写入点**（暂停 / 死亡早退分支里的复位除外）；别在别处再写一份。</summary>
    void UpdateAimState(bool rmbHeld)
    {
        if (!rmbHeld) _aimNeedsRepress = false;
        _aiming = rmbHeld && !_aimNeedsRepress;
    }

    /// <summary>
    /// 推进「腰射 ↔ 瞄准」的散布过渡（**定速线性**，唯一实现）。
    ///
    /// 为什么是 `MoveTowards` 定速、而不是"按剩余距离重算时间"：
    ///   定速 ⇔ **剩余时间 = 过渡时间 × 剩余比例** —— 半途松开时"按得越浅回得越快"，
    ///   于是 `aimToHipTime` 是**上界**（不是"每次都要等满"）。这正是设计要的语义，
    ///   而且天然支持"半途打断继续"：从当前进度往反方向走，不跳变、不重置。
    /// ⚠️ 时间 ≤ 0 → 直接取目标值 = **瞬时切换（旧行为）**，也就是"想回到改动前"的开关。
    /// ⚠️ 只驱动 `CurrentSpread`；后坐倍率 / 横向闸门 / 移动速度仍按 `_aiming` 瞬间切换。
    /// </summary>
    void AdvanceAds(float dt)
    {
        if (weapon == null) { _adsProgress = 0f; return; }   // 空手：没有"抬枪"这件事
        float target = _aiming ? 1f : 0f;
        float time = _aiming ? weapon.hipToAimTime : weapon.aimToHipTime;
        _adsProgress = time > 0f ? Mathf.MoveTowards(_adsProgress, target, dt / time) : target;
    }

    void HandleFire()
    {
        if (weapon == null) return;
        // ⚠️ 「切枪动作」没做完（收枪 + 出枪）→ **不能开火**：这是"切枪前摇"的**唯一闸门**（判据 `FireLocked`）。
        //    早退在这里 = 连"空弹自动换弹"那支也不会走（动作没做完，枪还没在手上）。
        if (FireLocked) return;
        if (Input.GetMouseButton(0) && !reloading && Time.time >= _nextFireTime)
        {
            if (magAmmo <= 0) { StartReload(); return; }
            Fire();
            _nextFireTime = Time.time + weapon.fireInterval;
        }
    }

    /// <summary>开火：按弹丸数发射子弹，并播放枪口火光、音效与镜头后坐</summary>
    void Fire()
    {
        magAmmo--;
        // ⚠️ 每发写回槽（2026-10-27）：否则"打剩 1 发 → 拖进背包"读到的是旧弹匣（回满 8 发）。
        //    弹匣随枪 = 任何"离开手持"的路径都必须能读到真实余量。
        SaveCurrentSlotAmmo();
        // 开枪强制退出奔跑（掉回「行走」，要重新按 Shift 才能再跑）—— 这就是「腰射时用行走速度」的由来。
        // 与塔科夫同类做法；这里从简：不禁止「跑着开枪」，只是**开枪即掉速**。
        running = false;

        // 后坐力（纯屏幕系 + 锚点跟准星）：每发沿"屏幕上 玩家→准星"的方向把**准星**顶开一段，
        // 外加垂直于它的左右随机。只有"新一发"沿当前方向加入，已推出去的位移不会被重新旋转；
        // 换到反方向开火时新后坐自然抵消旧位移（与真实压枪直觉一致）。
        if (weapon != null)
        {
            var cam = mainCamera != null ? mainCamera : CameraCache.Main;
            if (cam != null)
            {
                Vector3 ps = cam.WorldToScreenPoint(transform.position);
                Vector2 dir = ps.z > 0f ? _crosshairEff - (Vector2)ps : _lastPushDir;
                if (dir.sqrMagnitude < 1f) dir = _lastPushDir;   // 准星几乎压在玩家身上：方向无意义，沿用上次
                dir.Normalize();
                _lastPushDir = dir;
                Vector2 side = new Vector2(-dir.y, dir.x);       // 沿"推出方向"的横向（左负右正）

                // 这一发的推力：沿推出方向「当前纵向后坐」米 + 横向随机「当前横向后坐」米。
                // 当前值 = 腰射值 × 瞄准倍率（按住右键瞄准时更稳）；倍率见 RecoilUpMultiplier / RecoilJitterMultiplier。
                Vector2 inc = dir * (CurrentRecoilUp * _pxPerM)
                            + side * (Random.Range(-CurrentRecoilJitter, CurrentRecoilJitter) * _pxPerM);

                // ---- 横向闸门（WeaponData.recoilMaxSide）----
                // 语义 = "本梭子横向最多偏 N 米"：把**每发实际推出去的横向量**累加成一个**标量账本**，
                // 到顶就**只把这一发的横向分量削掉（最多削到 0）**，绝不回头搬运已经推出去的位移。
                // ⚠️ 绝不能改回"把已累计的总位移按当前方向重新分解、再把差额搬回准星"——
                //    基准方向会随玩家压枪/转火旋转，那样**一次开火就能把准星瞬移几百像素**（§4 #28 的"无形的屏障"）。
                // ⚠️ "左右"用 `_latRefDir`（本梭子的固定基准，**由玩家动作离散更新**），不是每帧的 dir。
                Vector2 refSide = new Vector2(-_latRefDir.y, _latRefDir.x);
                float incLat = Vector2.Dot(inc, refSide);
                if (weapon.recoilMaxSide > 0f)
                {
                    // 上限同乘「瞄准横向后坐倍率」：瞄准时横向后坐变小，横向带宽也跟着变小，相对松紧不变
                    float cap = weapon.recoilMaxSide * RecoilJitterMultiplier * _pxPerM;
                    float want = _latAccum + incLat;                  // 推完之后账本会到哪
                    float cut = Mathf.Clamp(want, -cap, cap) - want;  // 需要从这一发里削掉多少
                    // 只允许"削"（把 incLat 朝 0 收），**不允许反向加**：把 cut 夹进对应的半区
                    float lim = Mathf.Abs(incLat);
                    if (incLat > 0f) cut = Mathf.Clamp(cut, -lim, 0f);
                    else if (incLat < 0f) cut = Mathf.Clamp(cut, 0f, lim);
                    else cut = 0f;
                    incLat += cut;
                    inc += refSide * (incLat - Vector2.Dot(inc, refSide));   // 用削完的值覆盖 inc 的横向分量
                }
                _latAccum += incLat;          // 账本只记"实际发出的横向量"
                _crosshairTarget += inc;      // 准星只吃"这一发的增量"，永不搬运旧位移
            }
        }
        _recovering = false;       // 射击期间不回正（松手且鼠标未动时才开始）

        Vector3 muzzle = MuzzlePos;   // 唯一实现（2026-10-15 收敛；原来 Fire 与 GetAimState 各抄一份）
        muzzle.y = GameConst.EntityPlaneY;   // 压平到子弹飞行平面

        // 爆头判定：**开火那一瞬间**做一次，整发有效（霰弹的多颗弹丸共享这一个结果）。
        // 走 `GetAimState`（判定口径的唯一实现）—— ⚠️ 2026-10-17 起准星不再变色，这里是它**唯一**的消费方。
        EnemyAI headshotTarget = null;
        EnemyAI aimTarget;
        if (GetAimState(out aimTarget) == AimState.Head) headshotTarget = aimTarget;
        // 爆头倍率**属于武器**（2026-09-20 从敌人配置 EnemyConfig 移过来）：
        // 同一把枪打不同敌人倍率一致，便于按「枪」调手感；判定本身仍是"准星在敌人头上"（GetAimState）。
        float headshotMult = Mathf.Max(1f, weapon.headshotMultiplier);

        // 弹道基准方向：从枪口指向准星点（而非玩家 forward）—— 整个项目的**唯一实现**在 `BulletDir` 属性里。
        // 枪口在玩家身体右侧（本地 X≈0.22），沿 forward 发射会恒偏右打不准星（"不跟手"）；
        // `LaserBeam` 读同一份，所以激光与弹道走同一条线。后坐漂移已包含在准星点里，压枪机制不变。
        Vector3 baseDir = BulletDir;

        // ⚠️ **爆头弹道修正（必须有，别再删）**：被判定为爆头的那一发，改为直接指向「被锁定敌人」的**身体中心**。
        // 为什么必须：准星压在敌人头顶时，准星落点会落到敌人**身后**；对**侧向**的敌人，这个落点会偏到**旁边**
        //   （相机越靠后偏得越多）→ 子弹从旁边飞过去。实测最坏偏到碰撞体半径的 **1.44 倍**（必空）；
        //   更麻烦的是"哪些敌人算侧向"随**宽高比**变化 → 不修的话，玩家换个分辨率就会莫名打空。
        //   （这条是 2026-09-19 用"跨宽高比几何扫描"挖出来的：70°/65° 时踩线成立 0.372 vs 0.4，
        //     相机一后退或视野一变宽就成片失效。）
        // 改完的效果：弹道穿过敌人中心 → 命中余量从 **2.8cm** 变成**整个碰撞体半径 0.4m**，
        //   且与宽高比/相机取景**完全无关**；只有"敌人在子弹飞行期间横向跑开 >0.4m"才会空（和普通子弹一样的正常打空）。
        // 设计取舍（用户 2026-09-19 确认）：判定为爆头的这一发，命中它身上**任何部位**都算爆头伤害 ——
        //   本来就是「开火瞬间投票」的语义，俯视角下玩家也看不出子弹具体擦过哪儿。
        if (headshotTarget != null)
        {
            Vector3 toTarget = headshotTarget.transform.position - muzzle;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.0001f) baseDir = toTarget.normalized;
        }

        // 弹药等级的伤害倍率（一级 1.0 / 二级 1.1）—— 整发有效，在 `Bullet` 里与爆头倍率一起相乘结算。
        // ⚠️ 读的是**弹匣里那一张卡**（`magAmmoId`），不是"背包里最好的那一级"：
        //    玩家在 HUD 上看到的等级、这一发打出的伤害、打完退回的弹，必须是同一张卡。
        // ⚠️ **口径对得上才认**（防脏数据）：`magAmmoId` 是 public、Inspector 可以手改；将来若再出现一次等级漂移，
        //    症状会是"手枪吃着步枪弹的倍率"这种最难查的静默失衡 → 不合法一律按 1.0。
        bool ammoOk = magAmmoId != null && magAmmoId.ammoKind == weapon.ammoKind;
        float ammoMult = ammoOk ? Mathf.Max(0f, magAmmoId.damageMultiplier) : 1f;

        // 爆头弹是否用「爆头」那一对扰动参数（循环外算一次：霰弹 7 颗不必重复判空）
        bool tiltAsHeadshot = headshotTarget != null;
        for (int i = 0; i < weapon.pellets; i++)
        {
            float angle = Random.Range(-CurrentSpread, CurrentSpread);
            Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * baseDir;
            // 纵向扰动：放在水平散布**之后**（绕 Y 的散布会保持 y 分量不变，两种旋转正交）。
            dir = ApplyVerticalTilt(dir, tiltAsHeadshot);
            Bullet.Spawn(muzzle, dir, weapon, Health.Team.Player, headshotTarget, headshotMult, ammoMult);
        }

        Effects.MuzzleFlash(muzzle, transform.forward);
        Sfx.Shot(weapon);
        CameraRig.Shake(weapon.shake);   // 每把武器独立的镜头后坐强度
        PlayGunKick();                   // 枪的「后坐动画」（挂在 Gun 上的 Animation，可选；没挂就什么都不做）
    }

    /// <summary>
    /// 子弹纵向扰动（2026-10-18）：让弹道在 y 方向轻微上/下偏，增加俯视角下的空间感
    /// （原来所有子弹都飞在 `GameConst.EntityPlaneY` = 0.55 这一个水平面上）。
    ///
    /// **为什么放在这里、而不是 `Bullet` 里**：
    ///   · 只影响玩家开火这一处（敌人子弹完全不变）；
    ///   · **不碰 `BulletDir`** —— 玩家朝向 / 激光 / 爆头判定继续读水平方向，零联动。
    ///
    /// **分布 = 三角分布**（两次均匀随机相加，值域 [-1,1]，中间高、两端概率密度趋近 0）：
    ///   大多数子弹只有上限的 1/3 左右，偶尔才偏到上限；不会像均匀分布那样"每发都偏同样量级"。
    ///
    /// **旋转轴 = `Cross(dir, up)`**（垂直于弹道的水平横轴，不是世界 X 轴）：
    ///   斜着射时也不会带出横向偏移；绕它转正角 = 向上（已验算：dir=+Z 时正向旋转得到 (0,+sin,cos)）。
    ///
    /// ⚠️ 40m 内的安全边界（数值依据写在 4 个字段的 Tooltip 里）：
    ///   · 向上：普通 0.6° 要 59m 才越过敌人胶囊顶（1.17m）；
    ///   · 向下：余量仅 5cm（子弹 0.55 / 可交互箱子顶 0.50），0.15° 在 19m 外开始会被箱子挡；
    ///   · 爆炸类武器（RPG，射程 27m）的命中点同时是爆炸中心：`up > 0.74°` 会让隔墙判定从 1.2m 墙顶越过。
    /// </summary>
    /// <param name="headshot">本次开火是否判为爆头（决定用哪一对参数）</param>
    Vector3 ApplyVerticalTilt(Vector3 dir, bool headshot)
    {
        float up = headshot ? headshotUpAngle : bulletUpAngle;
        float down = headshot ? headshotDownAngle : bulletDownAngle;
        float total = up + down;
        if (total <= 0f) return dir;                     // 两个都关：零开销直接返回

        // ① 方向：按 up : down 的比例决定（down = 0 → 100% 向上；up = 0 → 100% 向下）。
        //    ⚠️ 不能写成 "r >= 0 ? r*up : r*down"：三角分布在 0 两侧的概率各 50%，
        //    那样 down = 0 时会有一半子弹**完全不偏**（2026-10-18 实测 8000 次、中位数 = 0 抓到）。
        //    用 Random.Range（float 版上界不包含）而不是 Random.value * total：
        //    Random.value 可能返回 1.0，down = 0 时会让这一颗误走「向下」分支（概率 2^-24，后果只是不偏）。
        bool upward = Random.Range(0f, total) < up;

        // ② 幅度：三角分布取绝对值 —— 0 附近概率最高、上限处概率密度趋近 0，**永远不会超过上限**。
        float mag = Mathf.Abs(Random.value + Random.value - 1f);
        float tilt = (upward ? up : -down) * mag;        // 正 = 向上，负 = 向下
        if (Mathf.Abs(tilt) < 0.0001f) return dir;       // 几乎为 0：省一次四元数乘法

        Vector3 side = Vector3.Cross(dir, Vector3.up);   // dir × up = 弹道的水平横轴
        if (side.sqrMagnitude < 1e-8f) return dir;       // 弹道垂直向上/下（本作不会出现）→ 原样返回
        return Quaternion.AngleAxis(tilt, side.normalized) * dir;
    }

    /// <summary>准星当前指向敌人的状态（由 <see cref="GetAimState"/> 产出）。
    /// ⚠️ 2026-10-17 起准星「指着敌人变色」已移除 ⇒ 目前**只有 `Fire()` 消费 `Head`**（决定爆头）；
    ///    `Body` 仍会被算出但**暂无消费方**（调试可视化用的是 `bodyAimRadius` 本身，见 `DebugOverlay`）。</summary>
    public enum AimState
    {
        None,   // 没指着任何敌人
        Body,   // 指着敌人的身体
        Head    // 指着敌人的头部（爆头）
    }

    /// <summary>
    /// **统一的「准星指着敌人哪个部位」判定 —— 唯一实现**。
    /// ⚠️ 2026-10-17：原先 `Crosshair` 也调它给准星着色（白/红/绿），**那条已整条移除** ⇒ 现在**只有 `Fire()`** 调它。
    ///
    /// 规则：
    /// - **头部（屏幕空间）**：判定圈半径 = **该敌人头部在屏幕上的投影半径** × `headshotGateScale`（默认 1 = 所见即所判），
    ///   准星屏幕坐标落在圈内即判爆头 —— 判定圈与"屏幕上看到的头"同源，FOV / 分辨率 / 宽高比 / 距离怎么变都同步。
    ///   （`headshotGateScale ≤ 0` 时回退旧口径：全敌人共用一个固定像素圈 = 屏幕高 × `headshotRadiusRatio`。）
    ///   多个敌人同时满足取**屏幕距离最小**者。
    /// - **身体（世界空间 · 按弹道判定）**：从枪口沿「枪口 → 瞄准点」方向的**射线**，到敌人**中心的垂直距离** ≤ `bodyAimRadius`（米）。
    ///   ⚠️ 这里用的是「点到直线（射线）的距离」，**不是**「点到点的距离」—— 后者会把"准星指着敌人旁边"也算命中（见 §4 #26）；
    ///   与 `Bullet` 的直击命中共用**同一个判据**，所以 `bodyAimRadius = 0.4`（碰撞体半径）时"判为身体 ⟺ 这一枪会命中"成立。
    ///   📌 **本条暂无消费方**（准星变色已移除）—— 刻意留着它和 `bodyAimRadius`，因为调试工具要按它画判定范围。
    ///   ⚠️ 例外：**被判为爆头的那一发**弹道会被改成指向锁定敌人的中心（见 `Fire()` 的爆头弹道修正）——
    ///   那时用"准星落点"预测会偏到旁边，但那一发本来就以"指着头部"为准，不会互相矛盾。
    ///   多个敌人同时满足取**垂直距离最近**者。
    /// - **头部优先**于身体（头部体积小、更有意义）。
    /// - 跳过尸体；相机后方（`WorldToScreenPoint().z ≤ 0`）的头部投影直接忽略。
    /// - 判定用 `_crosshairEff`（过渡值）——与屏幕上画出来的准星完全一致。
    /// </summary>
    public AimState GetAimState(out EnemyAI target)
    {
        target = null;
        var cam = mainCamera != null ? mainCamera : CameraCache.Main;
        if (cam == null) return AimState.None;

        // 弹道基准：唯一实现在 `BulletDir`（与 Fire() 同一份 —— 以前这里复刻了一遍、靠注释提醒"必须保持一致"）。
        Vector3 muzzle = MuzzlePos;   // 唯一实现（2026-10-15 收敛；原来 Fire 与 GetAimState 各抄一份）
        Vector3 bulletDir = BulletDir;
        bulletDir.y = 0f;
        if (bulletDir.sqrMagnitude < 0.0001f) bulletDir = Vector3.forward; else bulletDir.Normalize();
        Vector3 muzzleXZ = new Vector3(muzzle.x, 0f, muzzle.z);

        // 判定圈口径（二选一）：
        //  · gateOnVisual（默认）：贴合可见头 —— 每个敌人各自算「屏幕投影半径 × headshotGateScale」；
        //  · 回退：全敌人共用一个固定像素圈 = 屏幕高 × headshotRadiusRatio（旧行为）。
        bool gateOnVisual = headshotGateScale > 0f;
        float gateLegacy = Screen.height * Mathf.Max(headshotRadiusRatio, 0f);
        bool headOn = gateOnVisual || gateLegacy >= 0.5f;   // 半径太小直接关掉（避免"必中"）
        float headBestSq = float.MaxValue;
        EnemyAI headBest = null;

        float bodyR = Mathf.Max(bodyAimRadius, 0f);
        float bodyRSq = bodyR * bodyR;
        float bodyBestSq = float.MaxValue;
        EnemyAI bodyBest = null;

        var list = EnemyAI.All;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e == null) continue;
            var h = e.HealthRef;                   // EnemyAI 已缓存，不必逐个 GetComponent
            if (h == null || !h.alive) continue;   // 跳过尸体

            // ① 头部（屏幕空间）：判定圈 = 头在屏幕上的可见半径 × 倍率（默认 1 = 所见即所判）
            var head = e.head;
            if (headOn && head != null)
            {
                Vector2 hc;
                float visibleR;
                if (HeadScreenCircle(cam, head, out hc, out visibleR))
                {
                    float gate = gateOnVisual ? visibleR * headshotGateScale : gateLegacy;
                    if (gate >= 0.5f)
                    {
                        float dSq = (hc - _crosshairEff).sqrMagnitude;
                        if (dSq <= gate * gate && dSq < headBestSq) { headBestSq = dSq; headBest = e; }
                    }
                }
            }

            // ② 身体：点到**射线**的距离（clamp 到枪口前方，敌人躲在玩家身后不会被误判成红）
            Vector3 ePos = new Vector3(e.transform.position.x, 0f, e.transform.position.z);
            Vector3 toE = ePos - muzzleXZ;
            float along = Vector3.Dot(toE, bulletDir);
            if (along < 0f) along = 0f;
            Vector3 closest = muzzleXZ + bulletDir * along;
            float bSq = (ePos - closest).sqrMagnitude;
            if (bSq <= bodyRSq && bSq < bodyBestSq) { bodyBestSq = bSq; bodyBest = e; }
        }

        if (headBest != null) { target = headBest; return AimState.Head; }
        if (bodyBest != null) { target = bodyBest; return AimState.Body; }
        return AimState.None;
    }

    /// <summary>
    /// 头部在屏幕上的**圆心**与**可见半径（像素）** —— 爆头判定圈的来源，唯一实现。
    ///
    /// 半径怎么来：把「头心」与「头心 + 相机右方向 × 头半径」各投影到屏幕，取两点的像素距离。
    /// 相机右方向天然垂直于视线，所以这个距离就是**该深度处一个世界半径对应多少像素** ——
    /// 与 `UpdateAimPoint` 里实测 `_pxPerM` 用的是同一套手法，因此判定圈与渲染出来的头球恒等，
    /// FOV / 分辨率 / 宽高比 / 距离怎么变都同步（这是 2026-09-23 从「固定像素圈」改过来的原因）。
    /// 返回 false = 头部在相机后方或退化，调用方应跳过。
    /// </summary>
    /// <summary>爆头判定圈的屏幕投影（**开火判定与调试可视化唯一同源实现**）。
    /// ⚠️ 2026-10-16 由 `static` 提为 `internal`：调试工具要画"同一个圈"，**复刻公式必然会漂**
    ///    （第一版就漏了 4 处细节：`Max(radius,0.01f)`、`b.z &lt;= 0`、`gate &gt;= 0.5f` 的门限、`headshotGateScale ≤ 0` 时的回退口径）。
    ///    提为 internal 后直接调它 → "看到的圈 = 判定的圈"永远成立。</summary>
    internal static bool HeadScreenCircle(Camera cam, EnemyHead head, out Vector2 center, out float radiusPx)
    {
        center = Vector2.zero;
        radiusPx = 0f;
        Vector3 c = head.WorldPosition;
        Vector3 a = cam.WorldToScreenPoint(c);
        if (a.z <= 0f) return false;                                  // 相机后方：投影结果无效
        Vector3 b = cam.WorldToScreenPoint(c + cam.transform.right * Mathf.Max(head.radius, 0.01f));
        if (b.z <= 0f) return false;
        center = new Vector2(a.x, a.y);
        radiusPx = new Vector2(b.x - a.x, b.y - a.y).magnitude;
        return radiusPx > 0.0001f;
    }

    void StartReload()
    {
        if (weapon == null || reloading) return;
        // ⚠️ 「切枪动作」没做完 → **不能起手换弹**（与禁开火同一个窗口、同一个判据 `FireLocked`）
        if (FireLocked) return;
        // ⚠️ 不吃弹药的枪（`AmmoKind.None`，如 RPG）永远能换；其余枪**当前这一级一颗备弹都没有就拒绝换弹**并提示 ——
        //    否则会出现"吭哧换完，弹匣还是空的"，玩家会以为卡了。
        // ⚠️ 分级系统（2026-09-27）起这里只看**弹匣当前等级**那张卡的库存，不再用按口径的总和：
        //    一级打完时不会自动拿二级顶上（想换级请按 T —— 那是一次完整的换弹）。
        if (weapon.ammoKind != AmmoKind.None && backpack != null)
        {
            var cur = ResolveAmmo(weapon, magAmmoId);
            if (cur != null) magAmmoId = cur;     // "未知等级"顺手落实成具体那张（HUD / 退弹都读它）
            if (cur == null || backpack.CountAmmo(cur) <= 0)
            {
                // ⚠️ **提示必须节流**：打空弹匣后按住左键会**每帧**重试换弹（见 `HandleFire`），
                //    不节流的话中央公告会被每帧刷新 → 永远不消失（且盖住波次 / 拾取公告、每帧产生新字符串）。
                if (Time.time >= _nextNoAmmoWarnTime)
                {
                    _nextNoAmmoWarnTime = Time.time + 0.8f;
                    string what = cur != null ? cur.DisplayName : AmmoData.DisplayOf(weapon.ammoKind);
                    HUD.Announce("没有" + what + "了", 1.0f, 26, new Color(1f, 0.62f, 0.42f));
                }
                return;
            }
        }
        reloading = true;
        _reloadUntil = Time.time + weapon.reloadTime;
        _markValid = false;        // 换弹：回正标记作废（本次射击周期结束）
        _recovering = false;
        _undisturbedSince = Time.time;
        _mouseDeviation = Vector2.zero;
        _latAccum = 0f;
        Sfx.ReloadStart(weapon);   // 换弹开始：拔弹匣 / 拉护木 / 开栓
    }

    void FinishReload()
    {
        reloading = false;

        // 「按 T 换级」走独立分支：**退旧弹 + 装新弹**在这里一次做完（原子）
        if (_switchTarget != null)
        {
            var target = _switchTarget;
            _switchTarget = null;
            FinishSwitchAmmo(weapon, target);
            return;
        }

        if (weapon == null) { magAmmo = 0; magAmmoId = null; return; }

        if (weapon.ammoKind == AmmoKind.None || backpack == null)
        {
            magAmmo = weapon.magSize;      // 不吃弹药的枪（RPG）/ 没背包 → 直接补满
        }
        else
        {
            // 按缺口从**弹匣当前等级**那张卡扣备弹；不足则**能补多少补多少**（不会白扣，扣多少补多少）。
            // ⚠️ 只扣当前等级（用户 2026-09-27 定）：一级打完不会自动拿二级补，换级得按 T。
            var cur = ResolveAmmo(weapon, magAmmoId);
            int need = Mathf.Max(0, weapon.magSize - magAmmo);
            int got = (need > 0 && cur != null) ? backpack.TakeAmmo(cur, need) : 0;
            magAmmo += got;
            magAmmoId = cur;               // 记下这一匣是哪一级（伤害倍率 / T 换级退弹 / HUD 都读它）
            SaveCurrentSlotAmmo();         // 补好的弹匣立刻写回槽（切枪/丢弃前都会再存一次，这里是双保险）
        }

        Sfx.ReloadEnd(weapon);             // 换弹完成：插匣上膛 / 推栓
    }

    /// <summary>解析"这一匣该算哪张弹药卡"：`id` 有效（口径对得上）就用它；
    /// 否则（null / 脏数据）→ 取该口径**背包里有货的最低等级**。
    /// ⚠️ 这是"等级未知"的**唯一出口** —— HUD / 伤害倍率 / 换弹扣弹 / 换级退弹都先过它，
    ///    免得五个地方各写一遍"空的时候算哪一级"（那必然有一处会忘）。
    /// 返回 null = 这个口径确实什么都没有；`None` 口径（RPG）永远返回 null。</summary>
    AmmoData ResolveAmmo(WeaponData w, AmmoData id)
    {
        if (w == null || w.ammoKind == AmmoKind.None) return null;
        if (id != null && id.ammoKind == w.ammoKind) return id;   // 认它（口径必须对得上 —— 防脏数据串口径）
        return backpack != null ? backpack.LowestOwnedAmmo(w.ammoKind) : null;
    }

    /// <summary>弹药等级选择器提交：换到**选定的那一档**（`target`）。由 `AmmoSelector.CloseSelector` 调用。
    ///
    /// **切换 = 一次换弹**（用户 2026-09-27 定）：这里只做**预检 + 记下目标卡**，
    /// 真正的"退旧弹 / 装新弹"在 `FinishSwitchAmmo` 里完成时**一次性**执行（原子）——
    /// 中途被打断（切枪 / 丢弃会走 `LoadMirrorFromSlot` 清掉 `_switchTarget`）就等于什么都没发生，
    /// 绝不会出现"旧弹退了、新弹没装上"这种半截状态。
    /// 预检走 `CanSwapMag`：判定基准是"**取走新弹之后**"的背包状态（用户定的语义）。
    /// 与旧版区别：旧版 `StartSwitchAmmo` 自己算"下一档"；本方法认调用方选定的 `target`（选择器自由选档）。</summary>
    public void BeginAmmoGradeSwap(AmmoData target)
    {
        if (weapon == null || reloading || backpack == null || target == null) return;
        // ⚠️ 「切枪动作」没做完 → **不能起手换级**（同 StartReload：同一个窗口、同一个判据 `FireLocked`）
        if (FireLocked) return;
        if (weapon.ammoKind == AmmoKind.None) return;      // RPG 那类不吃弹药 → 无效
        if (target.ammoKind != weapon.ammoKind)            // 口径对不上（脏数据 / 跨口径）→ 拒绝
        {
            WarnAmmoOnce("该武器不能用这级弹药", new Color(1f, 0.62f, 0.42f));
            return;
        }

        var cur = ResolveAmmo(weapon, magAmmoId);          // 当前这一匣的档（HUD / 退弹都读它）
        if (target == cur)                                 // 选的就是当前档 → 不用换（提示一下，不白耗换弹）
        {
            WarnAmmoOnce("已是" + target.DisplayName, new Color(0.9f, 0.85f, 0.5f));
            return;
        }

        int willLoad;
        if (!backpack.CanSwapMag(weapon, target, magAmmo, cur, out willLoad))
        {
            WarnAmmoOnce("背包放不下退弹，先腾个格子", new Color(1f, 0.62f, 0.42f));
            return;
        }

        _switchTarget = target;
        reloading = true;
        _reloadUntil = Time.time + weapon.reloadTime;   // 复用换弹时长（用户定：换级就是要换弹）
        _markValid = false;
        _recovering = false;
        _undisturbedSince = Time.time;
        _mouseDeviation = Vector2.zero;
        _latAccum = 0f;
        Sfx.ReloadStart(weapon);
    }

    /// <summary>换级完成（**先退弹、再装填**，只在这里做 —— 原子）。
    /// 退弹退给 `cur`（弹匣**原来那一级**）；装填从 `target` 扣。预检已保证放得下，
    /// 但这里仍走 `AddItems` 的返回值尽力收（背包可能在换弹那几秒里被别的东西塞满），
    /// 收不下的**如实提示**而不是静默吞掉。</summary>
    void FinishSwitchAmmo(WeaponData w, AmmoData target)
    {
        if (w == null || target == null) { magAmmo = 0; magAmmoId = null; return; }

        var cur = ResolveAmmo(w, magAmmoId);   // 旧弹退给**它自己那一级**（不是目标那一级）
        int oldAmmo = magAmmo;

        // ⚠️ **顺序必须是"先取新弹、再退旧弹"** —— 与 `CanSwapMag` 的模拟顺序严格一致。
        //    预检的语义是"**取走新弹之后**的背包放不放得下"（用户 2026-09-27 定的），
        //    所以执行也必须先取：满背包 + 目标那格正好被取空时，"取走新弹"腾出的那一格
        //    才是退旧弹的落脚点。
        //    ❌ 反过来（先退后取）的后果：`AddItems` 在满包时返回 0 → **整匣旧弹被静默丢弃**
        //       （2026-09-27 独立审计查出的真 bug，当时实测只验了预检返回值、没验退弹真的回到背包）。
        int willLoad = backpack != null ? Mathf.Min(Mathf.Max(0, w.magSize), backpack.CountAmmo(target)) : 0;
        magAmmo = backpack != null ? backpack.TakeAmmo(target, willLoad) : 0;
        magAmmoId = target;

        // ② 再退旧弹
        int dropped = 0;
        if (oldAmmo > 0 && cur != null)
        {
            int put = backpack != null ? backpack.AddItems(cur, oldAmmo) : 0;
            dropped = oldAmmo - put;
        }

        SaveCurrentSlotAmmo();

        // ③ 反馈（"退弹丢了"最需要玩家知道 → 优先；其次"没装上"；正常才报成功与装填量）
        if (dropped > 0 && cur != null)
            HUD.Announce("背包已满：" + cur.DisplayName + " " + dropped + " 发没能收回", 1.6f, 26, new Color(1f, 0.62f, 0.42f));
        else if (willLoad <= 0)
            HUD.Announce("已切换 " + target.DisplayName + "（备弹不足，没装上）", 1.4f, 26, new Color(0.9f, 0.85f, 0.5f));
        else
            HUD.Announce("已切换 " + target.DisplayName + "（" + willLoad + " 发）", 1.0f, 26, target.pickupColor);
        Sfx.ReloadEnd(w);
    }

    /// <summary>弹药相关的提示：统一走 0.8 秒节流（与"没弹药"共用同一个窗口 → 连按 T 不会刷屏）。</summary>
    void WarnAmmoOnce(string msg, Color color)
    {
        if (Time.time < _nextNoAmmoWarnTime) return;
        _nextNoAmmoWarnTime = Time.time + 0.8f;
        HUD.Announce(msg, 1.0f, 26, color);
    }

    /// 装备武器（从地上捡起时调用）：先收进背包（自动占空快捷槽并切换过去），满了进库存。
    /// 返回 false = 快捷槽和库存都放不下（背包已满，调用方应保留地上的枪并给提示）。
    public bool Equip(WeaponData w, int magAmmo = -1, AmmoData ammoId = null)
    {
        if (backpack == null) return false;
        SaveCurrentSlotAmmo();   // 先把当前槽弹匣写回，避免换槽后原槽免费回满弹
        bool ok = backpack.AddWeapon(w, magAmmo, ammoId);   // magAmmo 负值 = 满弹；ammoId = 那一匣的等级（null → 装填时自动挑）
        // 若因此换到了新槽，刷新手上的镜像
        LoadMirrorFromSlot();
        return ok;
    }

    /// <summary>从背包库存装备一把武器（背包面板点击时调用）：优先装进空槽；没有空槽则替换当前槽，
    /// **被换下的武器下放进库存**（背包内部已处理）。
    /// ⚠️ `displaced` 只在"库存真的放不下"时才非空 —— 这里是**唯一的兜底出口**，不是常规路径：
    ///    既下放又掉落 = 凭空多出一把枪（用户 2026-09-26 报的 bug）。
    /// `stackId` = 物品**身份证**（`ItemStack.def.StackId`）—— **不能用武器名/对象引用**：
    /// 敌人掉落的枪是 `Clone()` 出来的副本，按名字或按引用匹配都会出现"看起来一样却对不上"的静默 bug。</summary>
    public void EquipFromInventory(string stackId)
    {
        if (backpack == null) return;
        SaveCurrentSlotAmmo();   // 先把当前槽弹匣写回
        if (backpack.EquipFromInventory(stackId, out WeaponData displaced, out int displacedAmmo, out AmmoData displacedAmmoId))
        {
            if (displaced != null)
                WeaponPickup.Create(displaced, FindDropPos(), displacedAmmo, displacedAmmoId);   // 兜底：库存放不下才掉地上（连弹匣与等级）
            LoadMirrorFromSlot();
        }
    }

    /// <summary>从背包库存「装备」一个魔法（背包面板点击魔法卡时调用）。
    /// 被替换下来的那张由背包内部下放进库存；**只有库存真的放不下**时才掉到地上
    /// （与武器同一套纪律：既下放又掉落 = 凭空多出一张卡）。
    /// ⚠️ 装备本身不需要在这里做任何事 —— `PlayerMagic` 每帧比对背包槽，会自动重建行为实例。</summary>
    public void EquipMagicFromInventory(string stackId)
    {
        if (backpack == null) return;
        if (backpack.EquipMagicFromInventory(stackId, out MagicData displaced))
        {
            if (displaced != null) MagicPickup.Create(displaced, FindDropPos());
        }
    }

    /// 放弃指定快捷槽的武器（背包面板每格「丢弃」按钮用）掉到地上成为拾取物
    public void DropSlotAt(int i)
    {
        if (backpack == null) return;
        SaveCurrentSlotAmmo();   // 先把当前槽弹匣写回（丢弃的可能不是当前槽，但保险起见统一保存）
        // ⚠️ 必须在 `DropSlot` **之前**记下"是不是手上这把"：`DropSlot` 会把 `currentSlot` 清成 -1
        bool wasCurrent = i == backpack.currentSlot;
        var w = backpack.DropSlot(i, out int ammo, out AmmoData ammoId);
        if (w != null)
        {
            WeaponPickup.Create(w, FindDropPos(), ammo, ammoId);   // 地上的枪保留真实弹匣余量与等级（捡回来不会白满、也不会串级）
            // ⚠️ 只有丢掉的是**手上这把**才刷新镜像 —— `LoadMirrorFromSlot` 会取消换弹，
            //    丢别的槽没必要打断（那是"换枪"语义，不该让玩家白等一次换弹）。
            if (wasCurrent) LoadMirrorFromSlot();
        }
    }

    // ==================== 右键菜单用到的公开动作（2026-10-27） ====================

    /// <summary>使用一个血包（Q 键 / 背包右键菜单共用）：优先用背包里的第一个。</summary>
    public void TryUseMedkit()
    {
        var h = _hp != null ? _hp : GetComponent<Health>();
        if (h == null || !h.alive) return;
        if (h.hp >= h.maxHp)
        {
            HUD.Announce("生命值已满", 1.0f, 26, new Color(0.7f, 0.7f, 0.7f));
            return;
        }
        if (backpack == null || backpack.Medkits <= 0)
        {
            HUD.Announce("没有血包了", 1.0f, 26, new Color(0.9f, 0.7f, 0.4f));
            return;
        }
        var used = backpack.UseMedkit();
        if (used != null) HealBy(used);
    }

    /// <summary>用背包**指定格**的血包（右键菜单「使用」）。</summary>
    public void TryUseMedkitAt(int index)
    {
        var h = _hp != null ? _hp : GetComponent<Health>();
        if (h == null || !h.alive) return;
        if (h.hp >= h.maxHp)
        {
            HUD.Announce("生命值已满", 1.0f, 26, new Color(0.7f, 0.7f, 0.7f));
            return;
        }
        var used = backpack != null ? backpack.UseMedkitAt(index) : null;
        if (used != null) HealBy(used);
    }

    /// <summary>用**箱子指定格**的血包（右键菜单「使用」）。</summary>
    public void UseMedkitFromChest(Chest chest, int index)
    {
        var h = _hp != null ? _hp : GetComponent<Health>();
        if (h == null || !h.alive) return;
        if (h.hp >= h.maxHp)
        {
            HUD.Announce("生命值已满", 1.0f, 26, new Color(0.7f, 0.7f, 0.7f));
            return;
        }
        if (chest == null) return;
        var stack = chest.GetSlot(index);
        var med = stack != null ? stack.def as MedkitData : null;
        if (med == null) return;
        chest.TakeFromSlot(index, 1);
        HealBy(med);
    }

    void HealBy(MedkitData med)
    {
        var h = _hp != null ? _hp : GetComponent<Health>();
        if (h == null || med == null) return;
        h.Heal(med.healAmount);
        DamageText.Spawn(transform.position + Vector3.up * 1.2f, med.healAmount, DamageText.Kind.Heal);
        Sfx.Heal();
        Effects.HealFx(transform.position);   // 治疗光环（GameAssets.healPrefab 留空则不播）
        HUD.Announce($"+{med.healAmount:0} 生命值", 1.0f, 28, new Color(0.4f, 1f, 0.5f));
    }

    /// <summary>丢弃魔法槽（背包面板「丢弃」按钮用）：掉到地上成为一张魔法卡。
    /// 与武器版 <see cref="DropSlotAt"/> 同一套语义 —— 但魔法没有"弹匣镜像"，
    /// 所以不需要 `SaveCurrentSlotAmmo` / `LoadMirrorFromSlot` 那两步（少了两处能出错的地方）。</summary>
    public void DropMagicSlotAt(int i)
    {
        if (backpack == null) return;
        backpack.DropMagicSlot(i, out MagicData dropped);
        if (dropped != null) MagicPickup.Create(dropped, FindDropPos());
    }

    /// <summary>丢弃**背包库存里的一件物品**（背包面板右键用）：掉到地上成为对应的拾取物。
    /// 按物品类型分发到各自的拾取物类 —— 这里是"物品落地"的**唯一分发点**，
    /// 以后加新物品种类（手雷 / 护甲）只需要在下面加一支。
    ///
    /// ⚠️ 顺序：**先判断类型、再取走、最后落地** —— 反过来的话，"认不出来的物品"会先被取走
    ///    然后什么都不生成 = 凭空消失（静默丢东西）。
    /// ⚠️ 弹药**整格丢**（一格 N 发 → 地上就是一包 N 发）：否则"丢 1 发"会在脚边堆一串小包。
    /// ⚠️ 武器要把**真实弹匣与等级**一起带走（`magAmmo` / `ammoId`），否则捡回来会满弹或串级。</summary>
    public void DropItemFromInventory(int index)
    {
        if (backpack == null) return;
        if (index < 0 || index >= backpack.inventory.Count) return;
        var stack = backpack.inventory[index];
        if (stack == null || stack.def == null || stack.IsEmpty) return;

        var def = stack.def;
        var w = def as WeaponData;
        var a = def as AmmoData;
        var m = def as MagicData;
        var t = def as ThrowableData;
        bool isMedkit = def is MedkitData;

        if (w == null && a == null && m == null && t == null && !isMedkit)
        {
            // 认不出来的物品**不取走**（取走就变没了），只给一句明确的话
            HUD.Announce($"「{def.DisplayName}」还不能丢到地上", 1.2f, 24, new Color(1f, 0.6f, 0.4f));
            return;
        }

        int count = Mathf.Max(1, stack.count);          // 弹药整格丢；其它物品一格只有一个
        int ammo = stack.magAmmo;
        AmmoData ammoId = stack.ammoId;
        Vector3 pos = FindDropPos();

        // 先取走（腾格规则由 TakeFromInventory 统一负责）
        backpack.TakeFromInventory(index, w != null ? 1 : count);

        // 再按类型落地（与 `DropSlotAt` / `DropMagicSlotAt` 走同一族拾取物）
        if (w != null) { WeaponPickup.Create(w, pos, ammo, ammoId); return; }
        if (a != null) { AmmoPickup.Create(a, pos, count); return; }
        if (m != null) { MagicPickup.Create(m, pos); return; }
        if (t != null) { ThrowablePickup.Create(t, pos, count); return; }   // 投掷物：可堆叠 → 整格掉（N 颗一包）
        if (isMedkit) { HealthPack.Create(pos, stack.def as MedkitData); return; }   // 血包类型跟着走（C-6）
    }

    /// 把当前手上弹匣写回背包槽（换枪/切槽前调用，防止弹匣状态丢失导致免费回满弹）。
    /// ⚠️ `internal`（2026-10-27）：拖拽移动当前手持枪之前，`ItemMoveService` 也要先写回一次。
    internal void SaveCurrentSlotAmmo()
    {
        if (backpack == null) return;
        if (backpack.currentSlot >= 0 && backpack.currentSlot < backpack.slots.Length)
        {
            backpack.slots[backpack.currentSlot].magAmmo = magAmmo;
            backpack.slots[backpack.currentSlot].ammoId = magAmmoId;   // 等级与余弹一起写回（漏一处就漂移）
        }
    }

    /// <summary>
    /// 播放枪的「后坐动画」（挂在 `Gun` 挂载点上的 **legacy `Animation`** 组件，可选）。
    ///
    /// 为什么挂在 `Gun` 上而不是每个武器模型上：**所有武器模型都挂在同一个 Gun 下面** →
    /// 一个动画对全部武器生效，不用给 8 个模型 prefab 各配一套（用户 2026-09-25 定的做法）。
    ///
    /// ⚠️ 用 `Rewind() + Play()` 而不是 Animator：legacy Animation **没有状态机、没有过渡**，
    ///    能"每一发立即从头重播" → 连续扫射时看起来就是**高频抖动**（正是要的效果）；
    ///    用 Animator 会因为状态机过渡/参数而出现延迟或播不动，除非用 `Play(state, 0, 0f)`。
    /// ⚠️ 它走 scaled time（`Time.timeScale = 0` 时自动冻结），与暂停语义天然一致。
    /// ⚠️ 没挂 Animation 组件时（`_gunAnim == null`）什么都不做 —— 零影响。
    /// 📌 clip 只应驱动 **localPosition.z**：别给 x/y 打关键帧，否则动画会把挂载点摆好的左右/高度拉回 clip 里的值。
    /// </summary>
    void PlayGunKick()
    {
        if (_gunAnim == null) return;
        _gunAnim.Rewind();   // 立即回到 clip 第 0 帧（不是等它播完）—— 连续开火时这就是"高频抖动"的来源
        _gunAnim.Play();
    }

    /// 按当前武器切换手里的枪模型
    /// - 配了「持枪模型 Prefab」→ 用它
    /// - 没配但配了「拾取模型 Prefab」→ 复用它（所以只配一个模型就能同时用于地上和手上）
    /// - 都没配 / 空手 → 手里什么都不显示（不再有方块占位枪）
    /// 注意：gunVisual 是纯挂载点，不要往里放别的东西（这里的逻辑会清空它的所有子物体）
    /// ⚠️ 持弹模式（手里是投掷物）时改画投掷物模型 —— 见本方法开头的分支。
    public void RefreshGunVisual() => UpdateGunVisual();

    /// <summary>「换手」时必做的两件事 —— **唯一实现**（换枪分支与投掷物 `ExitMode` 共用）：
    /// ① 要求"重新按下右键才算瞄准"（换枪 = 重新举枪：右键仍按着也保持腰射）；
    /// ② 起算「切枪动作」的锁火窗（换手 = 手上要动一次，时长 = `GameAssets.switchWindup`）。
    /// ⚠️ 以后任何"手上那把换了"的新路径都调它 —— 别只做一半（只重按右键、或只锁火）。</summary>
    public void BeginHandSwap()
    {
        _aimNeedsRepress = true;
        _fireLockUntil = Time.time + SwitchWindup;
    }

    /// <summary>「切枪动作」是否**还没做完** = 不能开火 / 不能起手换弹 / 不能换级。
    /// ⚠️ 判据只此一处：`HandleFire` / `StartReload` / `BeginAmmoGradeSwap` 都用它 ——
    /// 别在别处再写 `Time.time < _fireLockUntil`（散开了迟早有一处忘改）。</summary>
    bool FireLocked => Time.time < _fireLockUntil;

    /// <summary>「切枪动作」时长（秒，来自资源总表 `GameAssets.switchWindup`；没放资源表时用兜底 0.3）。
    /// 语义 = **从换手那一刻到新枪可以开火**之间的时间（收枪段 + 出枪段都算）；0 = 关闭（退回"切完枪立刻能开火"）。
    /// ⚠️ 以后做了切枪动画：把这个数**填成动画的实际长度**即可 —— 别改这里的逻辑。</summary>
    static float SwitchWindup
    {
        get { var a = GameAssets.I; return a != null ? Mathf.Max(0f, a.switchWindup) : 0.3f; }
    }

    void UpdateGunVisual()
    {
        if (gunVisual == null) return;

        // ⚠️ 换枪这一刻如果后坐动画还在播，`Gun` 正处在"被推后"的位置，而 `UpdateGunTip` 是从
        //    **Gun 的当前位置**反推枪口的（且只在换枪时算一次）→ 不复位的话枪口会**永久**偏出那一帧的后退量。
        //    所以先停动画、把 Gun 拨回基准位置，再做后面的事。（"动画在播时换枪"很少见，但复位只要两行）
        if (_gunAnim != null && _gunAnim.isPlaying)
        {
            _gunAnim.Stop();
            gunVisual.localPosition = _gunBaseLocalPos;
        }

        // 先清掉旧模型：直接清空挂载点，不靠引用追踪，避免残留
        for (int i = gunVisual.childCount - 1; i >= 0; i--)
            DestroyImmediate(gunVisual.GetChild(i).gameObject);

        // ---- 持弹模式：手里拿投掷物（枪收起）----
        if (throwables != null && throwables.Active && throwables.Selected != null)
        {
            var def = throwables.Selected;
            GameObject tgo;
            if (def.pickupModelPrefab != null)
            {
                tgo = Instantiate(def.pickupModelPrefab, gunVisual);
                tgo.name = "ThrowableModel";
            }
            else
            {
                // 没模型 → 代码生成的占位球（颜色 = 卡上的标识色）：新投掷物没有美术也不至于"手里看不见东西"
                tgo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                tgo.name = "ThrowableModel(占位)";
                tgo.transform.SetParent(gunVisual, false);
                tgo.transform.localScale = Vector3.one * 0.18f;
                var r = tgo.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = Effects.Shared(def.pickupColor);
            }
            tgo.transform.localPosition = Vector3.zero;
            tgo.transform.localRotation = Quaternion.identity;
            foreach (var c in tgo.GetComponentsInChildren<Collider>()) DestroyImmediate(c);
            return;   // ⚠️ 不调 UpdateGunTip（枪口是给枪算的，投掷物的出手点另有一套）
        }

        // 装上手里的枪模型（可能没有模型，但武器本身仍能开火）
        Transform model = null;
        if (weapon != null)
        {
            var modelPrefab = weapon.heldModelPrefab != null ? weapon.heldModelPrefab : weapon.pickupModelPrefab;
            if (modelPrefab != null)
            {
                // 挂到挂载点下面（gunVisual 缩放已复位为 1，模型不会被拉变形）
                var go = Instantiate(modelPrefab, gunVisual);
                go.name = "GunModel";
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                // 模型不能带碰撞体：否则会并入玩家刚体、挡住子弹射线
                foreach (var c in go.GetComponentsInChildren<Collider>()) DestroyImmediate(c);
                model = go.transform;
            }
        }

        UpdateGunTip(model);
    }

    /// <summary>
    /// 把枪口点摆到「挂在挂载点上的模型的最前端」。
    /// 这样换任何模型（AK / 火箭筒 / 你导入的任意模型）枪口都自动对齐，不需要手动调数值。
    /// 找不到模型时，退化为按武器的 gunLength 估算。
    /// </summary>
    void UpdateGunTip(Transform model)
    {
        if (gunTip == null || weapon == null) return;

        // 挂载点的实际位置（跟随你在场景里的拖动），用世界坐标换算，不依赖父子层级
        Vector3 anchor = transform.InverseTransformPoint(gunVisual.position);

        // 回退值：按枪身长度估算枪口
        float frontZ = anchor.z + weapon.gunLength + 0.06f;

        if (model != null)
        {
            // 在「模型自身空间」里算最前端。
            // 为什么不用 Renderer.bounds？那是世界坐标轴对齐的包围盒，
            // 模型一转身（玩家会朝鼠标转）这个盒子会被撑大，算出来的枪口会随朝向漂移。
            // 用网格自身的 bounds 做换算，结果与朝向无关。
            float maxLocalZ = float.MinValue;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var lb = mesh.bounds;
                // 网格局部空间 → 模型局部空间
                var toModel = model.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var corner = lb.center + new Vector3(
                        (i & 1) == 0 ? -lb.extents.x : lb.extents.x,
                        (i & 2) == 0 ? -lb.extents.y : lb.extents.y,
                        (i & 4) == 0 ? -lb.extents.z : lb.extents.z);
                    var p = toModel.MultiplyPoint3x4(corner);
                    if (p.z > maxLocalZ) maxLocalZ = p.z;
                }
            }
            // 模型是挂在挂载点下面、旋转与挂载点一致的，所以模型局部 z 就是玩家局部 z 方向
            if (maxLocalZ > float.MinValue) frontZ = anchor.z + maxLocalZ + 0.06f;
        }

        gunTip.position = transform.TransformPoint(new Vector3(anchor.x, anchor.y, frontZ));
    }

    /// 找一个不卡进墙/障碍物的丢枪位置：依次尝试 前/后/左/右
    Vector3 FindDropPos()
    {
        Vector3[] dirs = { transform.forward, -transform.forward, transform.right, -transform.right };
        foreach (var d in dirs)
        {
            Vector3 p = transform.position + d * 1.1f;
            p.y = 0.4f;
            if (!Physics.CheckSphere(p, 0.35f)) return p;
        }
        Vector3 fallback = transform.position + transform.forward * 1.1f;
        fallback.y = 0.4f;
        return fallback;
    }
}
