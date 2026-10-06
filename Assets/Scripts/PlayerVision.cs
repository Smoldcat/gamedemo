using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 玩家视野（战争迷雾的"当前可见"层；挂在 Main Camera 上）：
/// **可见区 = 面前扇形 ∪ 身旁圆形**，其余压暗成半透明黑幕 —— 只影响显示，不碰任何玩法逻辑。
///
/// 实现（每帧，**二维俯视扫描**）：
///   ① **可见性多边形**：把整个世界对视野来说压成一个"**检测平面**"（`probeHeight` 高度，
///      Inspector 可调）——**平面以上的东西才挡视线**，平面以下一律无视（矮箱、草丛天然不参与）。
///      在这个平面里向四周发射水平探针 → 命中点连成"能看见的区域"轮廓。
///      · **采样方向跟着朝向一起旋转**（扇形内一档、扇形外一档 + 两条精确边界）。
///        ⚠️ 这是防抖的关键：固定角度网格（0°/4°/8°…）在"扇形边界"扫过网格点时，
///        该方向的长度上限会在"扇形半径 ↔ 圆半径"之间逐帧翻转 → 视野边缘疯狂抖动（实测定位）。
///      · **探针点名**：只对"被探针实际撞到的障碍"补「4 角 × 2 条」角点射线 → 墙角边界干净、不缺口；
///        （不再做"半径内全量登记"——那会把 90% 根本不挡视线的物体也算进来。）
///   ② **两段式渲染**（模板缓冲）：
///      · `视野遮罩.shader`：把可见性多边形只写进 **stencil = 1**（不出颜色）；
///      · `视野暗幕.shader`：一个"贴在相机前的全屏四边形"用 `Stencil NotEqual` 只画在**没有标记**的地方
///        → 视野内正常、视野外压暗（`fogAlpha` 可调）。
///
/// ⚠️ 预期说明（不是 bug）：**"相机看得见" ≠ "角色看得见"** —— 俯视相机很高，能看到墙后的地面，
///    但视野机制会照样把那里压暗（因为角色视线被墙挡了）。这正是"视野盲区"的本意。
///
/// ⚠️ 约定：射线显式传 LayerMask + `QueryTriggerInteraction.Ignore`（§4 #24）；不新增 static 状态；
///    运行时材质自己 new 并 Destroy；参数全在 Inspector，运行中实时生效。
/// </summary>
[DefaultExecutionOrder(120)]   // 排在 CameraRig(0) / CameraOcclusion(100) / PlayerXRay(110) 之后
public class PlayerVision : MonoBehaviour
{
    [Header("视野形状（可见区 = 扇形 ∪ 圆形）")]
    [Tooltip("扇形总角度（度）：以**玩家朝向**为中心（持枪时朝向 = 武器轴线 = 准星方向）。")]
    [Range(10f, 360f)] public float fanAngle = 90f;
    [Tooltip("扇形半径（米）。")]
    [Range(1f, 60f)] public float fanRadius = 13f;
    [Tooltip("身旁圆形半径（米）：0 = 不要近身圆（只留扇形）。")]
    [Range(0f, 30f)] public float circleRadius = 4.5f;

    [Header("遮挡（二维检测平面）")]
    [Tooltip("参与遮挡的层。留空（0）时自动取 Layers.Blocker（Obstacle + Ground）。")]
    public LayerMask blockerLayers;
    [Tooltip("检测平面高度（**相对玩家原点**，米）：整套视野只认这一个平面 —— " +
             "平面以上的东西才挡视线，平面以下的一律无视（≈ 二维俯视扫描）。\n" +
             "默认 0.75（玩家站在平地时 ≈ 绝对高度 1.3m，约胸口高）。\n" +
             "调低 → 容易被贴地矮物挡；调高 → 视野更通透（但进门可能被门楣挡）。")]
    [Range(0f, 3f)] public float probeHeight = 0.75f;
    [Tooltip("对「被探针撞到的障碍」的轮廓 4 角补射线（让墙角的视野边界干净、不缺口）。")]
    public bool cornerAlign = true;
    [Tooltip("补角时的角度偏移（度）：太小时角点会闪。")]
    [Range(0.05f, 2f)] public float cornerJitter = 0.35f;
    [Tooltip("扇形边界附近的「角点」丢弃阈值（度）：紧贴边界的角点会让轮廓在边界悬崖处微跳，直接忽略它们。")]
    [Range(0f, 5f)] public float boundaryGuard = 1f;

    [Header("精度 / 性能")]
    [Tooltip("整圈圆周探针总数（扇形内按角度占比分配、扇形外拿剩下的）：越多轮廓越细腻。" +
             "360 ≈ 每 1° 一条（推荐）；90 ≈ 每 4° 一条（省性能，但 8m 外细物可能漏点）。")]
    [Range(48, 720)] public int circleRays = 360;

    [Header("暗幕（视野外）")]
    [Tooltip("总开关。关闭时不画任何东西、也不写模板。")]
    public bool enable = true;
    [Tooltip("暗幕颜色。")]
    public Color fogColor = Color.black;
    [Tooltip("暗幕不透明度：0 = 无效果；1 = 全黑（看不到地形轮廓）。")]
    [Range(0f, 1f)] public float fogAlpha = 0.72f;

    // ---------------- 运行时 ----------------
    Camera _cam;
    Transform _player;
    Mesh _visMesh;          // 可见性多边形（每帧重建）
    Mesh _quad;             // 全屏四边形（贴在相机前）
    Material _maskMat;      // stencil 写入
    Material _fogMat;       // 暗幕
    /// <summary>一个采样方向：角度（度，[0,360)）+ 该方向的可见距离（米，已含长度上限）。</summary>
    struct Sample
    {
        public float ang;
        public float dist;
    }
    /// <summary>按角度排序的比较器（实例字段持有，避免每帧委托分配）。</summary>
    sealed class SampleCmp : IComparer<Sample>
    {
        public int Compare(Sample a, Sample b) { return a.ang.CompareTo(b.ang); }
    }

    readonly List<Sample> _samples = new List<Sample>(1024);    // 本帧全部采样方向（圆周 + 边界 + 角点）
    readonly List<Collider> _hitCols = new List<Collider>(64);  // 本帧"被探针撞到的障碍"= 角点补线的点名册
    readonly SampleCmp _sampleCmp = new SampleCmp();
    readonly List<Vector3> _verts = new List<Vector3>();
    readonly List<int> _tris = new List<int>();
    readonly List<Vector3> _cornerBuf = new List<Vector3>();

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
            _maskMat = new Material(maskShader); _maskMat.name = "视野遮罩（运行时）";
            _fogMat = new Material(fogShader); _fogMat.name = "视野暗幕（运行时）";
        }

        _visMesh = new Mesh();
        _visMesh.name = "视野多边形（运行时）";
        _visMesh.MarkDynamic();

        _quad = new Mesh();
        _quad.name = "视野暗幕面片（运行时）";
        _quad.vertices = new Vector3[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f)
        };
        _quad.uv = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        _quad.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        _quad.RecalculateBounds();
    }

    void LateUpdate()
    {
        if (!enable || _cam == null || _player == null || _maskMat == null || _fogMat == null) return;

        BuildVisibilityMesh();

        // ① 可见性多边形 → 只写 stencil（由 shader 保证不出颜色）
        Graphics.DrawMesh(_visMesh, Matrix4x4.identity, _maskMat, 0, _cam, 0, null, false, false, false);

        // ② 全屏暗幕：贴在相机前方，用 NotEqual 只画"没被标记"的像素
        _fogMat.SetColor("_Color", fogColor);
        _fogMat.SetFloat("_Alpha", fogAlpha);
        float dist = 60f;
        float h = 2f * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * dist;
        float w = h * Mathf.Max(_cam.aspect, 0.1f);
        Matrix4x4 m = Matrix4x4.TRS(_cam.transform.position + _cam.transform.forward * dist,
                                    _cam.transform.rotation, new Vector3(w * 1.1f, h * 1.1f, 1f));
        Graphics.DrawMesh(_quad, m, _fogMat, 0, _cam, 0, null, false, false, false);
    }

    // ---------------- 可见性多边形 ----------------
    void BuildVisibilityMesh()
    {
        Vector3 origin = _player.position;
        Vector3 fwd = _player.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        float baseAngle = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;   // 绕 Y 的朝向角
        float half = fanAngle * 0.5f;

        // ---- ① 圆周采样：**跟着朝向一起旋转**（防抖核心）----
        //  扇形内：baseAngle-half → +half（均分 nFan 条）；扇形外：+half → +360-half（均分 nOut 条）。
        //  ⚠️ 绝不能用"固定角度网格"（0°/4°/8°…）：朝向微动时扇形边界会扫过网格点，
        //     让该方向的长度上限在"扇形半径 ↔ 圆半径"之间逐帧翻转 → 视野边缘狂抖（实测定位）。
        _samples.Clear();
        _hitCols.Clear();
        float probeY = origin.y + probeHeight;      // 检测平面的绝对高度
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
        for (int i = 0; i < nFan; i++)
            AddSample(origin, Mathf.Repeat(baseAngle - half + (i + 0.5f) * fanStep, 360f), fanRadius, probeY, true);
        for (int i = 0; i < nOut; i++)
            AddSample(origin, Mathf.Repeat(baseAngle + half + (i + 0.5f) * outStep, 360f), circleRadius, probeY, true);
        if (!fullCircle)
        {
            // ② 扇形两条**精确边界**：内条（扇形半径）都取"边界**内侧** 0.01°"、外条（圆半径）取正边界角 →
            //    两侧都形成干净的"悬崖边"（跳变只跨 0.01°）；两条角度不同（排序稳定），
            //    且随朝向一起平滑旋转、永不翻转。
            //    ⚠️ 内条必须在"扇形内侧"（da ≤ half）—— 放外侧会与本文件"da ≤ half ? fanRadius : circleRadius"
            //       的归属口径矛盾，并让该侧斜边被放大到"半个采样步长"（审查发现的左右不对称）。
            float bL = baseAngle - half, bR = baseAngle + half;
            AddSample(origin, Mathf.Repeat(bL, 360f), circleRadius, probeY, false);        // 外条：正边界角 = 圆半径
            AddSample(origin, Mathf.Repeat(bL + 0.01f, 360f), fanRadius, probeY, false);   // 内条：边界内侧 0.01° = 扇形半径
            AddSample(origin, Mathf.Repeat(bR - 0.01f, 360f), fanRadius, probeY, false);   // 内条：边界内侧 0.01° = 扇形半径
            AddSample(origin, Mathf.Repeat(bR, 360f), circleRadius, probeY, false);        // 外条：正边界角 = 圆半径
        }

        // ---- ③ 角点对齐：**只对"被探针撞到的障碍"**补角（探针点名）----
        if (cornerAlign)
        {
            int nCol = _hitCols.Count;              // 冻结名单：角点射线本身不再污染名单
            for (int i = 0; i < nCol; i++)
            {
                var c = _hitCols[i];
                if (c == null) continue;
                Bounds b = c.bounds;
                if (b.max.y <= probeY) continue;    // 顶面没超过检测平面 → 不挡视线（动态门槛）
                _cornerBuf.Clear();
                _cornerBuf.Add(new Vector3(b.min.x, 0f, b.min.z));
                _cornerBuf.Add(new Vector3(b.min.x, 0f, b.max.z));
                _cornerBuf.Add(new Vector3(b.max.x, 0f, b.min.z));
                _cornerBuf.Add(new Vector3(b.max.x, 0f, b.max.z));
                for (int k = 0; k < _cornerBuf.Count; k++)
                {
                    Vector3 d = _cornerBuf[k] - new Vector3(origin.x, 0f, origin.z);
                    if (d.sqrMagnitude < 1e-4f) continue;
                    float a = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    float da = Mathf.Abs(Mathf.DeltaAngle(a, baseAngle));
                    // 丢弃"紧贴扇形边界"的角点：它只会让轮廓在悬崖处反复微跳（边界已由 4 条精确采样表示）。
                    // ⚠️ 阈值取 max(boundaryGuard, cornerJitter)：若"抖动半宽"超过"丢弃窗"，
                    //    角点的抖动样本会跨到扇形另一侧却沿用本侧上限 → 轮廓上留下刺 / 凹口（审查发现）。
                    if (Mathf.Abs(da - half) < Mathf.Max(boundaryGuard, cornerJitter)) continue;
                    float maxD = (da <= half) ? fanRadius : circleRadius;
                    AddSample(origin, Mathf.Repeat(a - cornerJitter, 360f), maxD, probeY, false);
                    AddSample(origin, Mathf.Repeat(a + cornerJitter, 360f), maxD, probeY, false);
                }
            }
        }

        _samples.Sort(_sampleCmp);

        // ---- ④ 生成顶点（中心 + 各方向边界点）----
        _verts.Clear();
        Vector3 up = Vector3.up * 0.05f;                 // 略高于地面，避免与地面 z-fighting
        // 扇心（多边形中心）：与边界点同高（0.05m），保证共面
        _verts.Add(new Vector3(origin.x, 0.05f, origin.z));

        for (int i = 0; i < _samples.Count; i++)
        {
            float rad = _samples[i].ang * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            _verts.Add(new Vector3(origin.x, 0f, origin.z) + dir * _samples[i].dist + up);
        }

        // ---- 三角扇 ----
        _tris.Clear();
        int n = _verts.Count - 1;
        for (int i = 0; i < n; i++)
        {
            _tris.Add(0);
            _tris.Add(1 + i);
            _tris.Add(1 + ((i + 1) % n));
        }

        _visMesh.Clear();
        _visMesh.SetVertices(_verts);
        _visMesh.SetTriangles(_tris, 0);
        _visMesh.RecalculateBounds();
    }

    /// <summary>朝一个方向在"检测平面"上打一条水平探针：
    /// 命中 → 记录命中距离，并把撞到的碰撞体记入点名册（仅 `collect` 时）；
    /// 未命中 → 记录该方向的上限（扇形内 = fanRadius，扇形外 = circleRadius）。</summary>
    void AddSample(Vector3 origin, float angle, float maxDist, float probeY, bool collect)
    {
        if (maxDist <= 0.01f) maxDist = 0.01f;
        float rad = angle * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        Vector3 o = new Vector3(origin.x, probeY, origin.z);
        float d = maxDist;
        RaycastHit hit;
        if (Physics.Raycast(o, dir, out hit, maxDist, blockerLayers, QueryTriggerInteraction.Ignore))
        {
            d = hit.distance;
            if (collect)
            {
                var col = hit.collider;
                if (col != null && !_hitCols.Contains(col)) _hitCols.Add(col);
            }
        }
        Sample s; s.ang = angle; s.dist = d;
        _samples.Add(s);
    }

    void OnDestroy()
    {
        if (_maskMat != null) Destroy(_maskMat);
        if (_fogMat != null) Destroy(_fogMat);
        if (_visMesh != null) Destroy(_visMesh);
        if (_quad != null) Destroy(_quad);
    }
}
