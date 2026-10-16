#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 调试：**效果生效范围指示器**（2026-10-17 新增）。开关打开后，在"效果**真的发生**"时把它的
/// **实际生效范围**画成醒目线框：
///   · **爆炸**（RPG / 手雷 / 自爆兵）→ 订阅 `Combat.OnExploded`，画出来**保留 1 秒**（瞬时效果，得留一会儿才看得见）；
///   · **火焰地带** / **烟云** → 每帧轮询注册表（`FireZone.All` / `SmokeVolume.All`），
///     **存在期间一直保留**、位置跟着走（烟云的半径取 **`BlockRadius`** = 真正挡视线的范围）。
///
/// ⭐ **形状无关**：本层只认「**一串点**」（`DrawLoop`）—— 圆只是"当前所有效果恰好是圆"。
///    以后要方形 / 扇形 / 胶囊，只要调用方把另一串点传进来，**本文件一行都不用改**。
///    （形状的生成放在调用方那层，见 `FillCircle` —— 它是"圆的点集"，本层不关心它为什么是圆。）
///
/// ⚠️ 纯显示：不写任何玩法状态、不改数值、不做任何判定；随 `Assets/Debug/` 一起删（正式包被 `#if` 剔除）。
/// ⚠️ **为什么不复用 `ThrowableTrajectoryView`**（投掷预览也在地面画圈）：那份是**玩家 UI**（预测落点），
///    而本文件属于调试工具、要能**整目录删掉** → 刻意不互相依赖，宁可多这几行绘制。
/// ⚠️ 开销：只在开关打开时才画；每段线一次 `DrawMesh`（**48 段**的圆 = 48 次）—— 调试用途、数量少，可接受。
/// </summary>
public class DebugRangeIndicator : MonoBehaviour
{
    /// <summary>开关（由 `DebugPanel` 的「视觉」页写）。默认关：它是世界覆盖层，会盖住画面。</summary>
    public bool visible = false;

    // ==== 参数 ====
    // ⚠️ 2026-10-17 按用户反馈调过一轮外观（"觉得好难看，但不能为了好看牺牲实用性"）：
    //    ① 段数 32 → 48、且每段两端各延长半个线宽 → **相邻段重叠，圆变成连贯实线**（原来是一圈虚线，那才是"难看"的主因）；
    //    ② 线宽**改成固定值**（原来按半径比例算 → 大圈粗小圈细，看着不统一）；
    //    ③ 颜色微调得更干净一点（仍是品红族、仍然醒目）。
    //    ⭐ **三项都不影响精度**：段的位置（顶点）没动，只是把线画长了一点、画粗一致了一点。
    const int Segments = 48;             // 圆用多少段（够圆又不至于太多 draw call）
    const float HoldTime = 1f;           // 爆炸画出来保留多久（用户 2026-10-17 定：1 秒）
    const float LineWidth = 0.08f;       // 线宽（米，**固定**，不随半径变 → 观感统一）
    const float GroundOffset = 0.02f;    // 贴地高度：在实体平面之上一点点（避免与地面 z-fighting）

    /// <summary>统一颜色（用户定：只一种 —— 打开它的人本来就知道自己在测什么）。
    /// 选品红：游戏内没有任何元素是这个色，最不容易看错。</summary>
    static readonly Color LineColor = new Color(1f, 0.3f, 0.9f, 0.85f);

    /// <summary>一段"保留期点集"（爆炸用）：画到 `until` 时刻为止。</summary>
    struct Held
    {
        public Vector3[] pts;
        public float until;
    }

    readonly List<Held> _held = new List<Held>();
    readonly List<Vector3[]> _pool = new List<Vector3[]>();   // 点集复用池：避免每帧 new（烟/火每帧刷新点集）
    int _poolUsed;

    Mesh _unitBar;        // 水平面内的单位方条（长 1、宽 1、贴地、法线朝上）
    Material _mat;        // 共享材质（Effects.SharedAlpha）

    // ==================== 订阅 ====================

    void OnEnable() { Combat.OnExploded += OnExploded; }

    void OnDisable()
    {
        Combat.OnExploded -= OnExploded;
        _held.Clear();
    }

    /// <summary>爆炸发生（来自 `Combat.Explode` / 自爆兵）：登记一段"保留 1 秒"的点集。
    /// ⚠️ 拿不到相机/开关状态也没关系 —— 真正的绘制在 `LateUpdate` 里按当时状态决定。</summary>
    void OnExploded(Vector3 point, float radius)
    {
        if (!visible || radius <= 0.01f) return;
        // ⚠️ **不能从"每帧复用池"里租**（2026-10-17 修，两个审查代理都独立发现）：`_held` 里的点集要
        //    **跨帧保留 1 秒**，而池在 `LateUpdate` 开头就归零 → 本帧/下帧的火/烟点集会**覆写同一块数组**
        //    → 爆炸圈会跳到火焰/烟云的位置上。爆炸是低频事件，这里直接给**独立数组**（不会产生每帧 GC）。
        var pts = new Vector3[Segments];
        FillCircle(pts, point, radius);
        // `unscaledTime`：面板打开会暂停（timeScale=0），用 `Time.time` 的话爆炸圈永远不过期、会冻在画面上
        _held.Add(new Held { pts = pts, until = Time.unscaledTime + HoldTime });
    }

    // ==================== 每帧 ====================

    void LateUpdate()
    {
        _poolUsed = 0;

        // 死亡结算后一切已停（读数全是过期值）→ 直接收摊，别在结算界面上再盖一层
        if (GameState.GameOver || !visible) { _held.Clear(); return; }

        var cam = CameraCache.Main;
        if (cam == null) { _held.Clear(); return; }

        EnsureAssets();

        // ---- ① 爆炸：清掉过期的，剩下的重画 ----
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            var h = _held[i];
            if (Time.unscaledTime >= h.until) { _held.RemoveAt(i); continue; }
            DrawLoop(h.pts, cam);
        }

        // ---- ② 火焰地带：存续期间一直画（半径 = 每跳伤害的判定半径）----
        var fires = FireZone.All;
        for (int i = 0; i < fires.Count; i++)
        {
            var f = fires[i];
            if (f == null) continue;
            var pts = RentPoints();
            FillCircle(pts, f.transform.position, f.Radius);
            DrawLoop(pts, cam);
        }

        // ---- ③ 烟云：存续期间一直画（半径 = **挡视线**半径，即它真正"隐藏视野"的范围）----
        var smokes = SmokeVolume.All;
        for (int i = 0; i < smokes.Count; i++)
        {
            var s = smokes[i];
            if (s == null) continue;
            var pts = RentPoints();
            FillCircle(pts, s.transform.position, s.BlockRadius);
            DrawLoop(pts, cam);
        }
    }

    // ==================== 绘制 ====================

    /// <summary>⭐ **形状无关入口**：把一串首尾相接的点画成线框。
    /// 本层不关心它为什么是这个形状 —— 圆 / 方 / 扇形都走这里。</summary>
    void DrawLoop(Vector3[] pts, Camera cam)
    {
        if (pts == null || pts.Length < 2) return;

        for (int i = 0; i < pts.Length; i++)
        {
            Vector3 a = pts[i];
            Vector3 b = pts[(i + 1) % pts.Length];        // 首尾相接
            Vector3 d = b - a;
            d.y = 0f;                                     // 压平到水平面（俯视角下一切贴地）
            // ⚠️ 每段**两端各延长半个线宽**（总长 +1 个线宽）→ 相邻段首尾**重叠**，整圈看起来是**连贯实线**，
            //    不再是"一圈虚线"（2026-10-17：那正是用户觉得难看的主因）。
            //    ⭐ 只沿**切线方向**加长，**段的位置（顶点）没动** → 圆的半径 / 精度**不受任何影响**。
            // ⚠️ 守卫要判 `d.magnitude`（不是加过线宽的 len）：相邻点重合时段长为 0，
            //    若靠 len 判会通过，接着 `d / d.magnitude` 就是 0/0 = NaN → 非法矩阵传给 DrawMesh
            if (d.magnitude < 0.001f) continue;
            float len = d.magnitude + LineWidth;

            var mid = new Vector3((a.x + b.x) * 0.5f, GroundPlaneY(), (a.z + b.z) * 0.5f);
            var rot = Quaternion.LookRotation(d.normalized);   // d 已压平 → 这是纯 yaw 旋转（上面已保证 d != 0）
            // ⚠️ 缩放轴必须与 `LookRotation` 对齐的轴一致：它让**局部 +Z** 对齐 `d`，
            //    所以"长度(len)"要给 **Z**、"线宽"给 **X**（2026-10-17 修：原来 `(len,1,LineWidth)` 给反了 →
            //    每段变成"沿半径的短棒"而不是"沿切线的长条"，看起来像放射状刻线、也不是连贯实线）
            var m = Matrix4x4.TRS(mid, rot, new Vector3(LineWidth, 1f, len));
            Graphics.DrawMesh(_unitBar, m, _mat, 0, cam, 0, null, false, false, false);
        }
    }

    static float GroundPlaneY() => GameConst.EntityPlaneY + GroundOffset;

    // ==================== 形状（放在调用方这一层）====================

    /// <summary>把一个**圆**的点集写进 `dst`（长度必须是 `Segments`）。
    /// 📌 形状生成刻意留在这一层：本类只负责"把一串点画出来"，不负责"为什么是圆"。</summary>
    public static void FillCircle(Vector3[] dst, Vector3 center, float radius)
    {
        if (dst == null || dst.Length < Segments || radius <= 0.01f) return;
        float y = GroundPlaneY();
        for (int i = 0; i < Segments; i++)
        {
            float a = i / (float)Segments * Mathf.PI * 2f;
            dst[i] = new Vector3(center.x + Mathf.Cos(a) * radius, y, center.z + Mathf.Sin(a) * radius);
        }
    }

    // ==================== 基础设施 ====================

    /// <summary>点集池：按 `Segments` 固定长度复用，避免每帧为烟/火分配新数组。</summary>
    Vector3[] RentPoints()
    {
        while (_pool.Count <= _poolUsed) _pool.Add(new Vector3[Segments]);
        var pts = _pool[_poolUsed];
        _poolUsed++;
        return pts;
    }

    void EnsureAssets()
    {
        if (_mat == null) _mat = Effects.SharedAlpha(LineColor);

        if (_unitBar != null) return;
        // 水平面内的单位方条：X ∈ [-0.5,0.5]（长）、Z ∈ [-0.5,0.5]（宽）、Y = 0
        // ⚠️ 绕序按"从上方看逆时针"排 → 法线朝 +Y（与 `DebugOverlay.EnsureUnitRing` 同一套做法）：
        //    这样即使 shader 从 `Sprites/Default`（Cull Off）回落到 `Unlit/Color`（Cull Back）也照样可见
        var verts = new Vector3[4]
        {
            new Vector3(-0.5f, 0f, -0.5f),
            new Vector3(-0.5f, 0f,  0.5f),
            new Vector3( 0.5f, 0f,  0.5f),
            new Vector3( 0.5f, 0f, -0.5f),
        };
        var tris = new int[6] { 0, 1, 2, 0, 2, 3 };
        _unitBar = new Mesh { name = "DebugRangeUnitBar" };
        _unitBar.vertices = verts;
        _unitBar.triangles = tris;
        _unitBar.RecalculateBounds();
    }

    void OnDestroy()
    {
        // 谁创建谁销毁（§4 #25）：本类建的 Mesh 自己释放（材质走 Effects 的共享缓存，不归这里）
        if (_unitBar != null) Destroy(_unitBar);
    }
}
#endif
