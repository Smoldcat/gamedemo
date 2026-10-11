using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;   // CommandBuffer（写 RT 用）

/// <summary>
/// 玩家视野（战争迷雾的"当前可见"层；挂在 Main Camera 上）：
/// **可见区 = 面前扇形 ∪ 身旁圆形**，其余压暗成半透明黑幕 —— 只影响显示，不碰任何玩法逻辑。
///
/// 每帧五步（每步只依赖**本帧的几何**，不存任何历史状态）：
///   ① 采样：在 `probeHeight` 高度的"检测平面"内，从玩家打一圈水平探针，方向**随朝向一起旋转**
///      （扇形内一档 = fanRadius、扇形外一档 = circleRadius，另加两条精确边界）；
///   ② 命中：撞到物体就记下距离；宽度小于 `minObstacleWidth` 的**细物忽略**（射线穿过去继续打）；
///   ③ 角点：对"被探针撞到的物体"补「4 角 × 2 条」射线，让墙角精确；
///   ④ 多边形：采样按角度排序 → 三角扇；
///   ⑤ 渲染：画进一张低分辨率 RT（用相机 VP，天然与屏幕对齐）→ 暗幕面片采样它 + 3×3 模糊柔化。
///
/// ⚠️ 两条"别再改回去"的约定（实测依据见《游戏设计笔记》视野一节 / 《交接文档》坑清单）：
///   · **不做任何时间平滑**（不限速、不记账、不做形态学抹平）—— 那会让轮廓滞后于几何，
///     观感变成"边缘像波浪一样漂移"，而且各层状态互相牵制、越修越乱；
///   · **治边缘抖动靠"空间抗锯齿"**（第①步的子射线取平均 + 三点平滑），
///     **不是**加密圆周探针（实测 360→720 反而更糟），**也不是**时间平滑。
///
/// ⚠️ 约定：射线显式传 LayerMask + `QueryTriggerInteraction.Ignore`；不新增 static 状态；
///    运行时材质自己 new 并 Destroy；参数全在 Inspector，运行中实时生效。
/// </summary>
[DefaultExecutionOrder(120)]   // 排在 CameraRig(0) / CameraOcclusion(100) / PlayerXRay(110) 之后
public class PlayerVision : MonoBehaviour
{
    [Header("视野形状（可见区 = 扇形 ∪ 圆形）")]
    [Tooltip("扇形总角度（度）：以**玩家朝向**为中心（持枪时朝向 = 武器轴线 = 准星方向）。")]
    [Range(10f, 360f)] public float fanAngle = 90f;
    [Tooltip("扇形半径（米）。")]
    [Range(1f, 60f)] public float fanRadius = 25f;
    [Tooltip("身旁圆形半径（米）：0 = 不要近身圆（只留扇形）。")]
    [Range(0f, 30f)] public float circleRadius = 3f;

    [Header("遮挡（二维检测平面）")]
    [Tooltip("参与遮挡的层。留空（0）时自动取 Layers.Blocker（Obstacle + Ground）。")]
    public LayerMask blockerLayers;
    [Tooltip("检测平面高度（**相对玩家原点**，米）：整套视野只认这一个平面 —— " +
             "平面以上的东西才挡视线，平面以下的一律无视。默认 0.75（平地时 ≈ 绝对高度 1.3m）。")]
    [Range(0f, 3f)] public float probeHeight = 0.75f;
    [Tooltip("**细物宽度**（米，取包围盒 XZ 里较大的一边）：**小于它的物体不遮挡视线**（射线穿过去）。\n" +
             "0 = 一律遮挡（最忠实，但电线杆这类细物会让边缘在移动时闪一下）。默认 0.5。\n" +
             "典型值：电线杆 0.33 / 窗棂 0.37 / 门扇 0.86 / 灌木 2.4 —— 设 0.5 可滤掉杆类，设 1.0 还能滤掉门扇。")]
    [Range(0f, 5f)] public float minObstacleWidth = 0.5f;
    [Tooltip("对「被探针撞到的障碍」补角点射线（让墙角的视野边界干净、不缺口）。")]
    public bool cornerAlign = true;

    [Header("精度")]
    [Tooltip("整圈圆周探针总数：扇形内按角度占比分配、扇形外拿剩下的。越多轮廓越细腻、也越贵。" +
             "360 ≈ 每 1° 一条（推荐）。\n" +
             "⚠️ 注意：单纯加密它**不能**治抖动（实测 360→720 反而更糟：台阶更密但每级高度没变）。" +
             "要治抖动请调下面的「子射线数」。")]
    [Range(48, 720)] public int circleRays = 360;
    [Tooltip("扇形内每条采样方向的【子射线数】（抗锯齿）：每条方向打 N 条子射线，距离取平均 ——\n" +
             "「物体边缘处的可见距离突变」被摊成 1/N 的渐变，N 越大边缘越平滑、越不会抖。\n" +
             "· 1 = 关闭（物体边缘扫过视线时会有明显跳动）\n" +
             "· 3 = 明显改善\n" +
             "· 5 = 推荐（实测每帧跳动 4.04m → 0.84m，帧率不受影响）\n" +
             "· 9 = 更平滑，但扇形内射线数成正比增长、收益递减\n" +
             "⚠️ 代价：扇形内的射线数 = 圆周探针数 × 本值。运行中可实时调整。")]
    [Range(1, 9)] public int subSamples = 5;

    [Header("渲染（边缘柔化）")]
    [Tooltip("RT 分辨率除数（4 = 1/4 屏）—— 相当于模糊刷子的【每根毛有多粗】：\n" +
             "每根毛覆盖 rtScale 个屏幕像素，9 根毛（3×3）合计最多覆盖 3×rtScale 像素。\n" +
             "⭐ 想要更宽的软边 → 本值和「边缘柔和度」一起加（配合表见下面那个参数）。\n" +
             "好处：调大更省性能。⚠️ 别超过 8~16（RT 太小 → 视野形状会糊成一团）。")]
    [Range(1, 16)] public int rtScale = 4;
    [Tooltip("边缘柔和度（屏幕像素）—— 相当于【你希望把边缘刷多宽】。\n" +
             "⚠️ 实际生效宽度 = min(本值, rtScale × 3)：9 根毛合计最多覆盖 3×rtScale 个像素，\n" +
             "   所以本值单独调大、超过 rtScale×3 的部分不起作用（不报错，只是白调）。\n" +
             "⭐ 想真正加宽 → 两个一起加（让本值 = rtScale × 3）：\n" +
             "   · 12 像素 → rtScale=4，本值=12      · 15 像素 → rtScale=5，本值=15\n" +
             "   · 18 像素 → rtScale=6，本值=18      · 24 像素 → rtScale=8，本值=24\n" +
             "⭐ 觉得边缘还有轻微跳动 → 加宽；觉得盲区边界糊得看不清 → 减窄。")]
    [Range(0f, 32f)] public float softnessPx = 12f;

    [Header("暗幕（视野外）")]
    [Tooltip("总开关。关闭时不画任何东西。")]
    public bool enable = true;
    [Tooltip("暗幕颜色。")]
    public Color fogColor = Color.black;
    [Tooltip("暗幕不透明度：0 = 无效果；1 = 全黑（看不到地形轮廓）。")]
    [Range(0f, 1f)] public float fogAlpha = 0.72f;

    // ---------------- 内部常量（刻意不做成参数：参数越多、互相牵制越难排查） ----------------
    const float CornerJitter = 0.35f;      // 角点补线的角度偏移（度）：太小时角点会闪
    const float CornerMinSize = 1.5f;      // XZ 尺寸小于它的物体不补角（细杆/小零件的角只值几个像素）
    const float CornerMatchTol = 0.1f;     // 角点"命中匹配"相对容差：射线必须命中在角点附近才算数
    const float BoundaryGuardDeg = 1f;     // 距扇形边界这么近的角点不要（边界已有两条精确采样）
    const int MaxPierce = 4;               // 细物最多连续穿透几次（防极端场景反复打）
    const int MaxCornerObjects = 64;       // 补角物体数上限（防极端场景射线爆量）
    const float QuadDist = 60f;            // 暗幕面片贴在相机前的距离

    // ---------------- 运行时 ----------------
    Camera _cam;
    Transform _player;
    Mesh _quad;             // 全屏四边形（贴在相机前）
    Material _maskMat;      // 把可见性多边形写进 RT（可见 = 白）
    Material _fogMat;       // 暗幕（采样 RT + 软边）
    CommandBuffer _cb;      // 写 RT 的命令缓冲（绑在 _visionCam 上）
    Camera _visionCam;      // 专用"写 RT"相机（cullingMask=0，只执行 _cb）
    Mesh _visMeshWorld;     // 世界坐标的可见性多边形（贴地 y=0.05）
    RenderTexture _visionRT;

    /// <summary>一个采样方向：角度（度，[0,360)）+ 该方向的可见距离（米，已含长度上限）。</summary>
    struct Sample
    {
        public float ang;
        public float dist;
    }
    sealed class SampleCmp : IComparer<Sample>
    {
        public int Compare(Sample a, Sample b) { return a.ang.CompareTo(b.ang); }
    }

    readonly List<Sample> _samples = new List<Sample>(1024);    // 本帧全部采样方向（圆周 + 边界 + 角点）
    readonly List<float> _fanDist = new List<float>(128);       // 扇形内圆周采样的距离（三点平滑的缓冲）
    readonly List<Collider> _hitCols = new List<Collider>(64);  // 本帧"被探针撞到的障碍"= 角点补线的点名册
    readonly List<Vector3> _cornerBuf = new List<Vector3>(4);
    readonly SampleCmp _sampleCmp = new SampleCmp();
    readonly List<Vector3> _verts = new List<Vector3>();        // 世界坐标顶点（贴地 y=0.05）
    readonly List<int> _tris = new List<int>();

    void Awake()
    {
        _cam = GetComponent<Camera>();
        if (_cam == null) _cam = CameraCache.Main;

        var pc = PlayerController.Instance;
        if (pc == null) pc = FindObjectOfType<PlayerController>();
        _player = pc != null ? pc.transform : null;
        if (_player == null)
            Debug.LogWarning("[PlayerVision] 场景里没有 PlayerController → 视野不会生效。", this);

        if (blockerLayers.value == 0)
        {
            if (Layers.Blocker.value != 0) blockerLayers = Layers.Blocker;
            else Debug.LogWarning("[PlayerVision] 找不到 Blocker 层组合，请在 Inspector 里手动指定「参与遮挡的层」。", this);
        }

        var maskShader = Shader.Find("Town/VisionMask");
        var fogShader = Shader.Find("Town/VisionFog");
        if (maskShader == null || fogShader == null)
            Debug.LogWarning("[PlayerVision] 找不到着色器 Town/VisionMask / Town/VisionFog（是不是 shader 编译失败了？）→ 视野不会生效。", this);
        else
        {
            _maskMat = new Material(maskShader); _maskMat.name = "视野遮罩-RT写入（运行时）";
            _fogMat = new Material(fogShader); _fogMat.name = "视野暗幕-软边（运行时）";
        }

        _visMeshWorld = new Mesh(); _visMeshWorld.name = "视野多边形（运行时）"; _visMeshWorld.MarkDynamic();

        // 专用"写 RT"相机：与主相机同一视角，但不渲染任何场景物体（cullingMask = 0），
        // 只执行绑在它身上的命令缓冲 → 把可见性多边形画进低分辨率 RT。
        // ⚠️ 为什么必须用"专用相机"：团结引擎下 `GL.LoadProjectionMatrix` / `DrawMeshNow(mesh, matrix)`
        //    的矩阵参数都不生效；而相机渲染上下文里的 DrawMesh 必然使用该相机的 VP 矩阵。
        var camGo = new GameObject("视野遮罩相机（运行时）");
        camGo.hideFlags = HideFlags.HideAndDontSave;
        _visionCam = camGo.AddComponent<Camera>();
        _visionCam.CopyFrom(_cam);
        _visionCam.cullingMask = 0;
        _visionCam.clearFlags = CameraClearFlags.SolidColor;
        _visionCam.backgroundColor = Color.black;         // 黑底 = "看不见"
        _visionCam.allowHDR = false;
        _visionCam.allowMSAA = false;
        _visionCam.useOcclusionCulling = false;
        _visionCam.enabled = false;                       // 不自动渲染，由 RenderVisionRT 手动 Render()
        _cb = new CommandBuffer();
        _cb.name = "视野遮罩-写RT（运行时）";
        _visionCam.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _cb);

        _quad = new Mesh();
        _quad.name = "视野暗幕面片（运行时）";
        _quad.vertices = new Vector3[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f)
        };
        _quad.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        _quad.RecalculateBounds();
    }

    void LateUpdate()
    {
        if (!enable || _cam == null || _player == null || _maskMat == null || _fogMat == null) return;

        BuildVisibilityPolygon();   // ①②③④：采样 → 命中 → 角点 → 多边形
        RenderVisionRT();           // ⑤：写进低分辨率 RT（保证 _visionRT 已创建）

        // 暗幕：贴在相机前的全屏面片，采样 RT（3×3 模糊）
        _fogMat.SetColor("_Color", fogColor);
        _fogMat.SetFloat("_Alpha", fogAlpha);
        _fogMat.SetTexture("_VisionRT", _visionRT);
        // 模糊偏移（RT 纹素）限幅在 3 以内 —— 3×3 是稀疏采样，偏移更大时采样点之间会露出"条带"
        _fogMat.SetFloat("_SoftTexels", Mathf.Clamp(softnessPx / Mathf.Max(rtScale, 1), 0f, 3f));
        int rtW = _visionRT.width, rtH = _visionRT.height;
        _fogMat.SetVector("_VisionRTSize", new Vector4(rtW, rtH, 1f / rtW, 1f / rtH));

        float h = 2f * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * QuadDist;
        float w = h * Mathf.Max(_cam.aspect, 0.1f);
        Matrix4x4 m = Matrix4x4.TRS(_cam.transform.position + _cam.transform.forward * QuadDist,
                                    _cam.transform.rotation, new Vector3(w * 1.1f, h * 1.1f, 1f));
        Graphics.DrawMesh(_quad, m, _fogMat, 0, _cam, 0, null, false, false, false);
    }

    /// <summary>角度（度）→ 检测平面内的水平单位方向。</summary>
    static Vector3 DirOf(float deg)
    {
        float rad = deg * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
    }

    // ---------------- ① ② ③ ④：采样 → 命中 → 角点 → 多边形 ----------------
    void BuildVisibilityPolygon()
    {
        Vector3 origin = _player.position;
        Vector3 fwd = _player.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        float baseAngle = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;   // 绕 Y 的朝向角
        float half = fanAngle * 0.5f;
        float probeY = origin.y + probeHeight;                          // 检测平面的绝对高度

        _samples.Clear();
        _hitCols.Clear();

        // ---- ① 圆周采样：方向**跟着朝向旋转** ----
        //  ⚠️ 绝不能用"固定角度网格"（0°/4°/8°…）：朝向微动时扇形边界会扫过网格点，
        //     让该方向的长度上限在"扇形半径 ↔ 圆半径"之间逐帧翻转 → 视野边缘狂抖（已实测定位）。
        int total = Mathf.Max(circleRays, 8);
        bool fullCircle = fanAngle >= 359.9f;
        int nFan, nOut;
        if (fullCircle) { nFan = total; nOut = 0; }
        else
        {
            nFan = Mathf.Clamp(Mathf.RoundToInt(total * (fanAngle / 360f)), 1, total - 1);
            nOut = total - nFan;
        }
        float fanStep = fanAngle / nFan;
        float outStep = nOut > 0 ? (360f - fanAngle) / nOut : 0f;

        // 扇形内：**超采样**（每条方向打 subSamples 条子射线，距离取平均）。
        // ⚠️ 为什么：离散射线扫过"物体边缘"时命中会在"物体表面 ↔ 物体后面"之间突变，
        //    屏幕上就是"边缘发抖"；取平均能把突变摊成 1/subSamples 格宽的渐变。
        //    角点采样与扇形外**不做**超采样：前者要保墙角锐利，后者在近身 3m、几乎无遮挡。
        int sub = Mathf.Max(subSamples, 1);        // 双保险：Inspector 已限 Range(1,9)，这里再兜一次（防除零）
        float subStep = fanStep / sub;
        _fanDist.Clear();
        for (int i = 0; i < nFan; i++)
        {
            float a0 = baseAngle - half + (i + 0.5f) * fanStep;
            float sum = 0f;
            for (int k = 0; k < sub; k++)
            {
                float off = (k - (sub - 1) * 0.5f) * subStep;
                Collider c;
                float d = CastDistance(origin, DirOf(a0 + off), fanRadius, probeY, out c);
                sum += d < 0f ? fanRadius : d;
                if (k == 0 && c != null && !_hitCols.Contains(c)) _hitCols.Add(c);   // 点名册只在首条收集
            }
            _fanDist.Add(sum / sub);
        }

        // 扇形内再做一次**相邻三点平滑**（0.25/0.5/0.25）：
        // 把"边缘处 1 格宽的突变"扩散成"2 格宽的缓坡"（实测这是治抖动的主要功臣）。
        // ⚠️ 有真实扇形的两端**绝不能成环**：`i = 0` 的"左邻居"其实是扇形**另一侧**的边缘样本（相隔 360-fanAngle），
        //    取它等于让两端的可见距离被"对面那一片几何"污染（2026-10-10 实测：静态最大 5.2m ≈ 屏幕 116px，
        //    而且对面一动这边就跟着动，逐帧 1.19~1.90m）——那是**跨屏耦合**，必须切断。
        // ⚠️ 缺的那一侧用**唯一邻居镜像补上**（= 0.5 自己 + 0.5 邻居）：
        //    这样每个样本的"自身权重"恒为 0.5（与扇区内完全一致，是图像处理里的标准边界处理）；
        //    若改成"夹取"（0.75 自己 + 0.25 邻居），端点自身权重会变成 0.75，
        //    实测端点逐帧跳变反而 +50%（慢转 2.39→3.58m、快转 7.21→9.12m、前进 1.27→1.90m）。
        // 代价是扇形内墙角圆掉约 1 格，但**墙角的锐利由角点采样单独负责**（角点不参与平滑）。
        // ⚠️ **例外：整圆（fanAngle≥359.9）没有边界** —— 此时两端在几何上本来就相邻（相隔 1 个 fanStep），
        //    **必须绕圈取邻居**；若仍用"镜像"，正后方那一个方向只做 0.5/0.5、与别处的 0.25/0.5/0.25 不一致
        //    （2026-10-11 修；实测改前该接缝样本 = 镜像值 14.967130，改后 = 绕圈值 15.014748）。
        for (int i = 0; i < nFan; i++)
        {
            float d = _fanDist[i];
            if (nFan >= 3)
            {
                int im, ip;
                if (fullCircle)
                {
                    im = (i - 1 + nFan) % nFan;              // 整圆：两端相邻 → 绕圈取邻居
                    ip = (i + 1) % nFan;
                }
                else
                {
                    im = i > 0 ? i - 1 : i + 1;              // 左端（i=0）没有左邻居 → 镜像用右邻居
                    ip = i < nFan - 1 ? i + 1 : i - 1;       // 右端（i=nFan-1）没有右邻居 → 镜像用左邻居
                }
                d = 0.25f * _fanDist[im] + 0.5f * d + 0.25f * _fanDist[ip];
            }
            Sample sm; sm.ang = Mathf.Repeat(baseAngle - half + (i + 0.5f) * fanStep, 360f); sm.dist = d;
            _samples.Add(sm);
        }

        for (int i = 0; i < nOut; i++)
            AddSample(origin, Mathf.Repeat(baseAngle + half + (i + 0.5f) * outStep, 360f), circleRadius, probeY, true);

        if (!fullCircle)
        {
            // ② 扇形两条**精确边界**：外条取正边界角（= 圆半径）、内条取"边界内侧 0.01°"（= 扇形半径）
            //    → 形成一条干净的"悬崖边"（跳变只跨 0.01°），随朝向平滑旋转、永不翻转。
            //    ⚠️ 2026-10-10（A/B 实验）内条**加子射线平均**：实测它扫过建筑边缘时逐帧跳 11.9m
            //       （屏幕 175px，是全系统最大的单点跳变；慢转 120 帧里 8 次 >1m）。
            //       ⚠️ 这不是"三点邻域平滑"（那条对扇形边界仍然禁止）：没有跨方向的状态，
            //          悬崖仍然只跨 0.01° —— 只是**尖端的半径**更稳（突跳摊成 1/sub）。
            float bL = baseAngle - half, bR = baseAngle + half;
            AddSample(origin, Mathf.Repeat(bL, 360f), circleRadius, probeY, false);
            AddBoundarySampleSub(origin, Mathf.Repeat(bL + 0.01f, 360f), fanRadius, probeY, sub, subStep);
            AddBoundarySampleSub(origin, Mathf.Repeat(bR - 0.01f, 360f), fanRadius, probeY, sub, subStep);
            AddSample(origin, Mathf.Repeat(bR, 360f), circleRadius, probeY, false);
        }

        // ---- ③ 角点补线：只对"被探针撞到的物体"，且只对"够大 + 角确实在轮廓上"的角 ----
        if (cornerAlign)
        {
            int nCol = Mathf.Min(_hitCols.Count, MaxCornerObjects);
            for (int i = 0; i < nCol; i++)
            {
                var c = _hitCols[i];
                if (c == null) continue;
                Bounds b = c.bounds;
                if (b.max.y <= probeY) continue;                                  // 顶面没超过检测平面 → 不挡视线
                if (Mathf.Max(b.size.x, b.size.z) < CornerMinSize) continue;       // 太小 → 它的角只值几个像素
                _cornerBuf.Clear();
                _cornerBuf.Add(new Vector3(b.min.x, 0f, b.min.z));
                _cornerBuf.Add(new Vector3(b.min.x, 0f, b.max.z));
                _cornerBuf.Add(new Vector3(b.max.x, 0f, b.min.z));
                _cornerBuf.Add(new Vector3(b.max.x, 0f, b.max.z));
                for (int k = 0; k < _cornerBuf.Count; k++)
                {
                    Vector3 d = _cornerBuf[k] - new Vector3(origin.x, 0f, origin.z);
                    float cornerDist = d.magnitude;
                    if (cornerDist < 0.01f) continue;
                    float a = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    // 只补"扇形内、且离边界足够远"的角（边界本身已由 4 条精确采样表示）
                    // ⚠️ 整圆时没有边界 → 这个闸门不适用（否则"正后方 1° 内"的角会被无谓排除；
                    //    2026-10-11 实测该判别姿态下有 4 个角点被误排除、最近的一个离正后方仅 0.05°）
                    if (!fullCircle && Mathf.Abs(Mathf.DeltaAngle(a, baseAngle)) > half - Mathf.Max(BoundaryGuardDeg, CornerJitter)) continue;
                    TryAddCornerSample(origin, Mathf.Repeat(a - CornerJitter, 360f), fanRadius, probeY, cornerDist);
                    TryAddCornerSample(origin, Mathf.Repeat(a + CornerJitter, 360f), fanRadius, probeY, cornerDist);
                }
            }
        }

        _samples.Sort(_sampleCmp);

        // ---- ④ 生成顶点（中心 + 各方向边界点）与三角扇 ----
        _verts.Clear();
        Vector3 up = Vector3.up * 0.05f;                 // 略高于地面，避免与地面 z-fighting
        _verts.Add(new Vector3(origin.x, 0.05f, origin.z));
        for (int i = 0; i < _samples.Count; i++)
            _verts.Add(new Vector3(origin.x, 0f, origin.z) + DirOf(_samples[i].ang) * _samples[i].dist + up);

        _tris.Clear();
        int n = _verts.Count - 1;
        for (int i = 0; i < n; i++)
        {
            _tris.Add(0);
            _tris.Add(1 + i);
            _tris.Add(1 + ((i + 1) % n));
        }
    }

    /// <summary>朝一个方向采样：记录命中距离（含细物穿透）；命中时把挡路的物体记入点名册。</summary>
    void AddSample(Vector3 origin, float angle, float maxDist, float probeY, bool collect)
    {
        float maxD = Mathf.Max(maxDist, 0.01f);
        Collider col;
        float d = CastDistance(origin, DirOf(angle), maxD, probeY, out col);
        if (d < 0f) d = maxD;                                     // 没命中 → 该方向的上限
        if (collect && col != null && !_hitCols.Contains(col)) _hitCols.Add(col);
        Sample s; s.ang = angle; s.dist = d;
        _samples.Add(s);
    }

    /// <summary>扇形边界"内条"专用采样：与扇内**同一套**子射线平均（空间抗锯齿），但**不做邻域平滑**。
    /// ⚠️ 为什么单独一个函数：这两条是斜边尖端的收口样本（上限 = 扇形半径），扫过物体边缘时距离会整段突跳
    ///    （实测 11.9m / 屏幕 175px）；取平均把突跳摊成 1/sub，而**悬崖本身仍然只跨 0.01°**。
    /// ⚠️ 与"三点邻域平滑"的区别：这里没有任何跨方向的状态——只是在同一条方向上取 ±(sub-1)/2 个子步的平均。</summary>
    void AddBoundarySampleSub(Vector3 origin, float angle, float maxDist, float probeY, int sub, float subStep)
    {
        float maxD = Mathf.Max(maxDist, 0.01f);
        float sum = 0f;
        for (int k = 0; k < sub; k++)
        {
            float off = (k - (sub - 1) * 0.5f) * subStep;
            Collider c;
            float d = CastDistance(origin, DirOf(angle + off), maxD, probeY, out c);
            sum += d < 0f ? maxD : d;
        }
        Sample s; s.ang = angle; s.dist = sum / sub;
        _samples.Add(s);
    }

    /// <summary>角点专用采样：只有"命中点落在角点附近"才被接受（否则是包围盒里的"空气角"，会戳出锯齿）。
    /// 用相对容差（物体越大，包围盒角与真实轮廓的偏差也越大）。</summary>
    void TryAddCornerSample(Vector3 origin, float angle, float maxD, float probeY, float cornerDist)
    {
        Collider col;
        float d = CastDistance(origin, DirOf(angle), maxD, probeY, out col);
        if (d < 0f) return;                                       // 该方向什么都没撞到 → 角点无轮廓意义
        if (Mathf.Abs(d - cornerDist) > Mathf.Max(0.5f, cornerDist * CornerMatchTol)) return;   // 命中点离角太远
        Sample s; s.ang = angle; s.dist = d;
        _samples.Add(s);
    }

    /// <summary>在检测平面上打一条水平探针。返回可见距离（**负数 = 没命中**），并输出挡路的碰撞体。
    /// **细物穿透**：宽度（包围盒 XZ 较大边）小于 `minObstacleWidth` 的物体不遮挡视线 —— 射线从它后面继续打。
    /// ⚠️ 为什么需要：细物（电线杆 ≈0.33m）在 1° 采样下只在轮廓上戳出 1~2 个点的窄凹陷，
    ///    而它"忽有忽无" → 移动/转视角时闪。忽略它 = 规则与距离无关、处处一致
    ///    （比"事后抹平"稳：那种做法判定跟距离挂钩，会出现"离远没迷雾、近了才有"）。</summary>
    float CastDistance(Vector3 origin, Vector3 dir, float maxDist, float probeY, out Collider hitCol)
    {
        Vector3 o = new Vector3(origin.x, probeY, origin.z);
        float travelled = 0f;
        hitCol = null;
        float remain = maxDist;
        for (int pierce = 0; pierce <= MaxPierce; pierce++)
        {
            RaycastHit hit;
            if (!Physics.Raycast(o, dir, out hit, remain, blockerLayers, QueryTriggerInteraction.Ignore))
                return -1f;                                        // 没命中
            if (minObstacleWidth > 0f)
            {
                Bounds b = hit.collider.bounds;
                if (Mathf.Max(b.size.x, b.size.z) < minObstacleWidth)
                {
                    // 细物 → 从它后面继续打
                    float adv = hit.distance + 0.02f;
                    o += dir * adv;
                    travelled += adv;
                    remain -= adv;
                    if (remain <= 0.02f) return travelled;
                    continue;
                }
            }
            hitCol = hit.collider;
            return travelled + hit.distance;
        }
        return travelled;                                          // 穿透次数用尽
    }

    // ---------------- ⑤ 可见性多边形 → 低分辨率 RT ----------------
    /// <summary>把"可见性多边形"画进一张低分辨率 RT（可见 = 白、其余 = 黑）—— 供暗幕做软边。
    /// ⭐ 必须用**相机自己的 VP 矩阵**渲染（RT = "屏幕的低分辨率版"），
    ///    这样暗幕面片用"屏幕 UV"采样它才能一一对应（曾用"世界俯视正交"→ 错位 + 远处 Clamp 出涂抹块）。
    /// ⚠️ 手动画 RT 时 `GL.LoadProjectionMatrix` / `DrawMeshNow(mesh, matrix)` 的矩阵参数均不生效
    ///    （团结引擎实测）；可靠路径 = **专用相机 + 命令缓冲 DrawMesh**。</summary>
    void RenderVisionRT()
    {
        // ⚠️ 2026-10-12 补：拆除时序的健壮性 —— 曾观测到一次本方法内 `_cb.Clear()` 空引用
        //    （退出 Play 的帧内拆除顺序可能让 LateUpdate 晚于 OnDestroy 的 `_cb = null` 跑一拍）。
        //    命令缓冲 / 专用相机任一缺失 → 直接返回（本帧不画视野图案，暗幕读到上一帧 RT：无害；
        //    ⚠️ 不能判空 `_visionRT` —— 它就是在下面按需创建的）。
        if (_cb == null || _visionCam == null) return;

        // RT 尺寸跟随**相机实际渲染尺寸** ÷ rtScale（用 pixelWidth 而不是 Screen.*：有黑边/缩放时两者会差）
        int scale = Mathf.Max(rtScale, 1);
        int w = Mathf.Max(16, _cam.pixelWidth / scale);
        int h = Mathf.Max(16, _cam.pixelHeight / scale);
        if (_visionRT == null || _visionRT.width != w || _visionRT.height != h)
        {
            if (_visionRT != null) { _visionRT.Release(); Destroy(_visionRT); }
            _visionRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            _visionRT.name = "视野遮罩RT（运行时）";
            _visionRT.filterMode = FilterMode.Bilinear;
            _visionRT.wrapMode = TextureWrapMode.Clamp;
            _visionRT.Create();
        }

        // 同步到主相机此刻的视角（投影 + 位姿都要跟，否则会错位）
        _visionCam.transform.SetPositionAndRotation(_cam.transform.position, _cam.transform.rotation);
        _visionCam.orthographic = _cam.orthographic;
        _visionCam.fieldOfView = _cam.fieldOfView;
        _visionCam.orthographicSize = _cam.orthographicSize;
        _visionCam.aspect = _cam.aspect;
        _visionCam.nearClipPlane = _cam.nearClipPlane;
        _visionCam.farClipPlane = _cam.farClipPlane;

        _visMeshWorld.Clear();
        _visMeshWorld.SetVertices(_verts);
        _visMeshWorld.SetTriangles(_tris, 0);
        _visMeshWorld.RecalculateBounds();

        // 专用相机（黑底由 clearFlags 负责）只执行"画一次白色多边形"
        _cb.Clear();
        _cb.DrawMesh(_visMeshWorld, Matrix4x4.identity, _maskMat, 0, 0);
        _visionCam.targetTexture = _visionRT;
        _visionCam.Render();
        _visionCam.targetTexture = null;
    }

    void OnDestroy()
    {
        if (_maskMat != null) Destroy(_maskMat);
        if (_fogMat != null) Destroy(_fogMat);
        if (_visMeshWorld != null) Destroy(_visMeshWorld);
        if (_cb != null) { _cb.Release(); _cb = null; }
        if (_visionCam != null) { Destroy(_visionCam.gameObject); _visionCam = null; }
        if (_quad != null) Destroy(_quad);
        if (_visionRT != null) { _visionRT.Release(); Destroy(_visionRT); }
    }
}
