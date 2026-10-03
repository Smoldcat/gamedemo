using UnityEngine;

/// <summary>
/// 俯视角摄像机：以固定构图偏移跟随玩家，玩家始终位于画面中部；持枪时朝准星方向轻微前探；
/// 按住右键瞄准（ADS）时把 FOV 收窄一点（举枪的注视感，全局统一一个收窄量），并**抬高朝准星前探的上限**
/// （`aimLeadExtra`，抵消"收窄把远处目标往外推"的副作用；只在原本已探满上限之后生效 —— 详见该字段的 Tooltip）。
/// 挂在 Main Camera 上，相机的初始位置和旋转就是「构图偏移」（在场景里调好即可）。
/// </summary>
public class CameraRig : MonoBehaviour
{
    public static CameraRig Instance;

    [Header("跟谁")]
    [Tooltip("要跟随的目标，一般是场景里的 Player")]
    public Transform target;

    [Header("构图偏移（相机相对玩家的位置）")]
    [Tooltip("相机相对玩家的**固定偏移**（世界轴向、米）= 相机应当出现的位置。\n" +
             "  · 默认 (-1.7034, 16, -8.7559) = 相机在玩家「西 1.7m、上 16m、南 8.76m」—— 水平距离 8.9m、俯角约 61°。\n" +
             "  · ⚠️ **运行时只认这个字段**（2026-10-03 起）：在场景里把 Main Camera 拖到哪儿都**不影响游戏机位**，\n" +
             "    玩家被拖走也照样跟随 —— 想调构图就改这三个数（z 越接近 0 镜头越近；y 越大越接近正俯视）。\n" +
             "  · 旧行为是「把场景里相机的位置当偏移」→ 为了看效果随手拖一下相机就会把游戏镜头搞偏（事故记录见 §4 #40）。")]
    public Vector3 offset = new Vector3(-1.7034214f, 16f, -8.755903f);
    [Tooltip("进入 Play 时直接把相机落到「玩家 + 偏移」的位置（不滑行）。\n" +
             "  · 开（默认）= 开场就在正确机位，没有「镜头从别处飞过来」的那一段；\n" +
             "  · 关 = 保持旧行为：从相机在场景里的当前位置平滑滑到目标（约 0.2~0.5 秒）。")]
    public bool snapOnStart = true;

    [Header("相机参数")]
    [Tooltip("相机跟随目标的平滑速度（每秒向目标位置逼近的比例）：越大跟得越紧、越小越「拖」。\n" +
             "  · 8（默认）≈ 0.1~0.2 秒收敛；\n" +
             "  · 它**同样作用于「朝准星前探」那段位移** —— 调小会把前探也一起变拖。")]
    public float followLerp = 8f;
    [Tooltip("【朝准星前探】比例：镜头跟随点 = 玩家位置 + 「玩家→瞄准点」方向 × 本比例（封顶值见下一条）。\n" +
             "  · 0 = 完全不前探（相机严格跟着玩家）；0.18 = 默认值。\n" +
             "  · 效果 = 你瞄哪边、镜头就往那边让一点，**那个方向能多看到一截地面**；\n" +
             "    它不是变焦：FOV / 相机高度 / 俯角 / 后距全都不变，只是水平平移。\n" +
             "  · ⚠️ **只有持枪时生效**（空手不探）；实测（稳态）：准星在屏幕正中时只探 0.38m、屏幕偏上 80% 处 1.60m、贴屏幕边缘达到上限 2.5m。")]
    [Range(0f, 0.5f)] public float aimLeadRatio = 0.18f;
    [Tooltip("【朝准星前探】的最大距离（米）：前探量 = min(瞄准点距玩家 × 前探比例, 本值)。\n" +
             "  · 2.5（默认）：瞄准点超过约 13.9 米（= 2.5 ÷ 0.18）之后前探量就封顶了。\n" +
             "  · 调大 = 能往远处探得更狠（代价：画面里的玩家会被推得更靠边）。\n" +
             "  · ⚠️ 注意是「先按比例缩放、再夹长度」，方向不变；实测贴屏幕顶部/左边缘时恰好 2.500m。")]
    public float maxAimLead = 2.5f;
    [Tooltip("【瞄准收窄视角（度）】按住右键瞄准时，相机 FOV 在基准上**减去**本值（0 = 关闭 → 逐位等于改动前）。\n" +
             "  · 7（默认）：45° → 38°，画面上放大约 1.20×、屏幕里的地面纵深少约 20%、鼠标的世界灵敏度同步变「沉」17% —— 就是举枪时的注视感。\n" +
             "  · 单位是**竖直**视角（Unity 的 FOV 本身是竖直的）：16:9 下收窄 7° ≈ 水平 73° → 63°。\n" +
             "    （⚠️ 这里曾经写【水平 79°】，算错了 —— 竖直 45° 在 16:9 下约等于水平 72.7°；2026-09-29 修正）\n" +
             "  · 节奏跟着 ADS 过渡走（WeaponData 的 hipToAimTime / aimToHipTime，与散布过渡**共用同一条时钟**）→ 不额外加参数，且与「散布圈在收缩」天然同步。\n" +
             "  · 基准 = 你**开镜前那一刻**相机上的 FOV（不写死）→ 在 Play 里直接调相机的 FOV 也会被跟着当基准，不用重进 Play；本值本身每帧读，运行中调即时生效。\n" +
             "  · ⚠️ 空手 / 开背包 / 暂停 / 死亡时 ADS 进度会归零 → FOV 自动回到基准（本组件被关掉或销毁时也会还原，不会把相机留在收窄状态）。\n" +
             "  · ⚠️ 收窄值大于基准 FOV 时会被运行时兜底夹到 1°（Unity 的 FOV 必须 > 0）。")]
    [Range(0f, 30f)] public float aimFovNarrow = 7f;
    [Tooltip("【瞄准前探增量（米）】按住右键瞄准时，把「朝准星前探」的**上限**抬高这么多（0 = 关闭 → 逐位等于改动前）。\n" +
             "  · 语义（关键）：**只在原本已经探满上限之后才生效** —— 准星近时前探根本没到上限 → **一点都不会多探**（逐位等于改动前）；\n" +
             "    瞄得越远、越接近新上限（10→13m 之间逐渐多探，约 13m 以外拿满本值）。所以近距离不会出现无谓的推镜。\n" +
             "  · 为什么需要它：收窄 FOV = 「绕屏幕中心放大」→ **远处的**目标会被往外推（实测收窄 2°：6m +8px / 12m +14px / 20m +21px）→ 就是「瞄着敌人一点右键、敌人往上滑」。外推量随距离递增，所以只在远处补。\n" +
             "  · 参考值：0.6（默认）；觉得 12m 左右补得不够就提到 1.0（上限随之抬到 maxAimLead + 1.0，20m 处能完整补 1.0m）。\n" +
             "  · 代价：每多探 1 米 ≈ 画面里的玩家下移 6% 屏高（1080p 约 66px）→ 建议 ≤ 1.5。\n" +
             "  · ⚠️ 本值作用在「上限」上，既有前探（aimLeadRatio / maxAimLead）一行都不改 → 不瞄准（或本值 = 0）时逐位等于改动前。\n" +
             "  · ⚠️ **节奏与变焦严格一致**：它乘 ADS 进度，并且**绕开相机跟随平滑**直接加在相机位置上。\n" +
             "    为什么必须绕开：FOV 是直接写进相机的（零平滑），而相机位置要走 followLerp 的一阶平滑 —— 若这段增量也跟着平滑走，实测会变成「变焦 0.3s 就停住、镜头还要往前滑到 0.9s 以上」的两段感（变焦之后多出 0.15m 位移）。\n" +
             "    现在它与变焦**同时到位、同时退回**（变焦之后不再有多余位移）。\n" +
             "  · ⚠️ 后坐把准星顶远时前探（含本增量）会跟着变大 → 压枪时镜头前后「呼吸」略多一点（既有现象，不是新增的问题）。")]
    [Range(0f, 3f)] public float aimLeadExtra = 0.6f;

    Quaternion _baseRot;
    Vector3 _offset;      // 相机相对跟随点的固定偏移（高度 + 后距）
    Vector3 _basePos;     // 未叠加震动时的相机位置
    Vector3 _shake;
    float _shakeAmount;

    // ---- ADS 收窄视角（瞄准变焦）----
    Camera _cam;          // 同一物体上的相机（本组件就挂在 Main Camera 上）
    float _fovBase;       // 本次「举枪」开始前的 FOV；开镜那一刻抓一次，收窄时以它为基准
    bool _fovEngaged;     // 是否处于「被本组件改写 FOV」的状态（决定要不要继续接管、以及何时放手）
    float _extraP;        // 「瞄准前探增量」自己的进度副本：跟随 ADS 进度、但阶跃会被铺开（见 Update 里的说明）

    void Awake()
    {
        // 单例防护：出现重复实例（如场景重载的极端时序）时丢弃后出现的那个
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        _basePos = transform.position;
        _baseRot = transform.rotation;
        // 构图偏移 = **组件上的字段**（2026-10-03 起）：不再读「场景里相机的位置」，
        // 这样在编辑器里拖动相机 / 玩家都不会影响游戏里的机位（旧行为的事故见 §4 #40）。
        _offset = offset;

        // 容错：没手动拖 target 时自动找玩家
        if (target == null)
        {
            var pc = FindObjectOfType<PlayerController>();
            if (pc != null) target = pc.transform;
        }

        // 开场就位：直接落到「跟随点 XZ + 偏移」，避免镜头从场景里的位置滑过来
        if (snapOnStart && target != null)
        {
            _basePos = new Vector3(target.position.x, 0f, target.position.z) + _offset;
            transform.position = _basePos;
        }

        // ADS 收窄视角：本组件必须挂在带 Camera 的物体上（Main Camera）。找不到就明确告警，不要静默失效
        _cam = GetComponent<Camera>();
#if UNITY_EDITOR
        if (_cam == null && aimFovNarrow > 0f)
            Debug.LogWarning("[CameraRig] 「瞄准收窄视角」需要挂在带 Camera 的物体（Main Camera）上，当前物体上没有 Camera → 这项不会生效。", this);
#endif
    }

    public static void Shake(float amount)
    {
        if (Instance == null) return;
        Instance._shakeAmount = Mathf.Min(Instance._shakeAmount + amount, 0.5f);
    }

    void LateUpdate()
    {
        // 锁定俯视角角度
        transform.rotation = _baseRot;

        var pc = PlayerController.Instance;

        // ADS 收窄视角：必须在 target 早退**之前**做 —— 否则 target 一旦为空，FOV 就再也回不到基准
        UpdateAimFov(pc);

        if (target == null) { transform.position = _basePos; return; }

        // 跟随点：玩家位置；持枪时朝准星方向前探一点（还原俯视射击手感）
        Vector3 t = target.position;
        Vector3 extraDir = Vector3.zero;   // ADS 前探增量的方向（玩家→准星落点）
        float extraOver = 0f;              // 超出 maxAimLead 的米数（本增量要补的部分）
        if (pc != null && pc.weapon != null)
        {
            Vector3 lead = pc.AimPoint - target.position;
            lead.y = 0f;
            t += Vector3.ClampMagnitude(lead * aimLeadRatio, maxAimLead);   // 既有前探：一行未改

            // ---- ADS 前探增量（2026-09-23；第 4 版定稿"绕开相机平滑"，第 5 版加"进度副本"防瞬跳）----
            // 目的：收窄 FOV = 「绕屏幕中心放大」→ **远处的**目标会被往外推（实测收窄 2°：6m +8px / 12m +14px / 20m +21px）
            //       → "瞄着敌人一点右键、敌人往上滑"。把镜头再往前补一点，把目标拉回视野。
            // ⚠️ 语义一：**只在原本已探满上限之后才补**（未封顶值 ≤ maxAimLead 时一点都不补 → 近距离逐位等于改动前）
            //    → 不会"在哪儿瞄准都多推一下"、也不会在近处白白把玩家往下压。
            // ⚠️ 语义二（关键）：它乘 ADS 进度、并且**绕开相机跟随平滑**（加在 `_basePos` 之后）。为什么必须绕开：
            //    FOV 是**直接写进相机**的（零平滑），而相机位置要走 followLerp 的一阶平滑（τ≈1/6s）+ 前探自身的反馈环；
            //    实测（60fps 逐帧喂进度）：**变焦第 18 帧（0.30s）就停住，镜头却一路爬到 50+ 帧（>0.9s）**、
            //    变焦之后还多出 0.15~0.19m 位移 → 玩家读成"先变焦、后前探"两段动作。
            //    放到平滑之后、只跟进度走 ⇒ 与 FOV **严格同时到位**（第 18 帧），之后不再有多余位移 ✅。
            // ⚠️ 也别改成去调 aimLeadRatio：前探是反馈环（AimPoint 由当前相机位置反投影），改比例 = 改环的增益、非线性难预期。
            if (aimLeadExtra > 0f && lead.sqrMagnitude > 0.0001f)
            {
                extraOver = Mathf.Min(lead.magnitude * aimLeadRatio - maxAimLead, aimLeadExtra);
                if (extraOver > 0f) extraDir = lead.normalized;
            }
        }

        // ADS 前探增量：用「自己的进度副本」`_extraP` 驱动（2026-09-23 第 5 版，修"进度瞬间归零时镜头单帧瞬跳"）。
        // 速度取**该武器 ADS 过渡里更快的那条**（进 1/hipToAimTime、出 1/aimToHipTime）：
        //  · 正常进 / 出（0.3s / 0.1s）：副本**跟得上** → 与 FOV 严格同步（用户要的"同时到位"，实测尾巴 **+0.000m**）；
        //  · 进度被**一帧归零**（换枪 / 捡枪 / 开背包 / 暂停 / 死亡 —— 都是既有行为）：副本按 ~10/s 收回，
        //    把"单帧瞬移最多 aimLeadExtra 米"摊成 ~0.1s 的匀速滑回（实测：0.550m / 0.099s，单次位移 ≤0.022m）。
        //  · ⚠️ 别改成"固定 4m/s 限速"：`aimLeadExtra` 调大后（如 1.8）松手回落会被拉长到 0.45s，反而制造出
        //    "松开右键后镜头还在飘"的两段感（实测过）。取"更快的那条 ADS 速率"才能既跟上正常进出、又铺开阶跃。
        bool armed = (pc != null && pc.weapon != null);
        float pNow = armed ? Mathf.Clamp01(pc.AdsProgress01) : 0f;
        float follow = armed
            ? Mathf.Max(1f / Mathf.Max(pc.weapon.hipToAimTime, 0.05f), 1f / Mathf.Max(pc.weapon.aimToHipTime, 0.05f))
            : 10f;
        _extraP = Mathf.MoveTowards(_extraP, pNow, follow * Time.deltaTime);
        Vector3 extraLead = extraDir * (extraOver * _extraP);

        // 核心：相机位置 = 跟随点XZ + 固定构图偏移（高度、后距保持不变，玩家固定出现在画面中部）
        Vector3 desired = new Vector3(t.x, 0f, t.z) + _offset;
        _basePos = Vector3.Lerp(_basePos, desired, followLerp * Time.deltaTime);

        // 屏幕震动（沿屏幕平面方向抖动，不污染基准位置）
        if (_shakeAmount > 0.001f)
        {
            Vector2 r = Random.insideUnitCircle;
            _shake = (transform.right * r.x + transform.up * r.y) * _shakeAmount * 0.35f;
            _shakeAmount = Mathf.MoveTowards(_shakeAmount, 0f, Time.deltaTime * 1.2f);
        }
        else _shake = Vector3.zero;

        transform.position = _basePos + _shake + extraLead;   // 不走相机平滑：由 _extraP 与 FOV 同步（正常进出严格同步）
    }

    /// <summary>
    /// ADS 收窄视角：按住右键瞄准时 `FOV = 基准 − 收窄值 × ADS 过渡进度`，松开后回到基准。
    ///
    /// 设计要点（都别随手改）：
    ///  · **进度只用 `PlayerController.AdsProgress01`** —— 与散布过渡**共用同一条时钟**
    ///    （`WeaponData.hipToAimTime` 进 / `aimToHipTime` 出）→ 不新增时间参数，且「画面在推近」与「散布圈在收缩」天然同步；
    ///    ⚠️ 禁止在这里另起一份过渡（那就出现第二条时钟了）。
    ///  · **基准取「开镜前那一刻」的相机 FOV**（不写死、也不在 Awake 抓）：这样在 Play 里直接调相机的 FOV 也会被当成新基准，
    ///    不必重进 Play；开镜期间不动手，收窄值与基准的关系就永远自洽。
    ///  · **没参与时一个字节都不碰相机**：不瞄准且不在接管状态 → 直接返回（保留「在 Play 里手动调 FOV」的操作空间，
    ///    也让「收窄值 = 0」的关闭状态对相机零触碰）。
    ///  · **回到 0 之后还要再写一帧基准**（`_fovEngaged = p > 0f` 的用法）：保证松开右键 / 暂停 / 死亡 / 空手时都能还原；
    ///    之后彻底放手。
    ///  · `Mathf.Max(1f, …)` 兜底：收窄值大于基准时不会算出 0 或负数（Unity 的 FOV 必须 > 0）。
    /// ⚠️ 放在 LateUpdate：读到的是**本帧**的过渡进度（Update 里刚推进过）；代价是 FOV 的写入比本帧 Update 里的
    ///    屏幕投影晚一帧 —— 与「相机位置本身也在 LateUpdate 写入」是同一类滞后，量级 &lt;1°、只在过渡的 0.3 秒内存在。
    /// </summary>
    void UpdateAimFov(PlayerController pc)
    {
        if (_cam == null) return;

        float p = (aimFovNarrow > 0f && pc != null) ? Mathf.Clamp01(pc.AdsProgress01) : 0f;

        if (!_fovEngaged)
        {
            if (p <= 0f) return;                 // 完全没参与 → 不碰相机
            _fovBase = _cam.fieldOfView;         // 开镜前那一刻的 FOV = 本次收窄的基准
            _fovEngaged = true;
        }

        float fov = Mathf.Max(1f, _fovBase - aimFovNarrow * p);
        if (!Mathf.Approximately(_cam.fieldOfView, fov)) _cam.fieldOfView = fov;   // 值没变就不写
        _fovEngaged = p > 0f;
    }

    // 组件被关掉 / 物体被销毁时，别把相机留在收窄状态（与 TimedEffect.OnDisable 还原灯光强度同一套约定）
    void OnDisable()
    {
        if (_fovEngaged && _cam != null) _cam.fieldOfView = _fovBase;
        _fovEngaged = false;
        _extraP = 0f;
    }

    void OnValidate()
    {
        if (aimFovNarrow < 0f) aimFovNarrow = 0f;   // 负数 = 反向变宽，不是本参数语义（想变宽就改场景里相机的 FOV）
        if (aimLeadExtra < 0f) aimLeadExtra = 0f;   // 负数 = 往后缩，不是本参数语义
        if (offset.y < 0.5f) offset.y = 0.5f;       // 相机至少要离地 0.5m，否则会钻到地面以下
    }
}
