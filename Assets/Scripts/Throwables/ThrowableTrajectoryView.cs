using UnityEngine;

/// <summary>
/// 投掷物预览的**渲染器**（只管"怎么画"：弹道点列 / 落点标记圈 / 范围指示圈；自建贴图与材质、自建自销）。
///
/// 为什么单独成类（2026-10-12 重构）：`PlayerThrowable` 原来同时扛"状态机 + 输入 + 弹道模拟 + 渲染"（约 700 行）；
/// 把**渲染**搬到这里，主类只剩"什么时候画、哪一段画白的、哪一段画红的"。**行为一字未变**（纯搬移）。
///
/// ⚠️ 用法：`PlayerThrowable.Awake` 里 `Build()`、`OnDestroy` 里 `Dispose()`；每帧只调下面几个 Draw*。
/// ⚠️ 渲染队列：填充 2985 → 环 2990 → 点 / 落点标记 3000（**后者永远压在最上层**，与贴地高度差无关）。
/// ⚠️ 这里**没有任何状态**（除了自建资源），所有"画什么"的判断都在调用方。
/// </summary>
public class ThrowableTrajectoryView
{
    /// <summary>预览点间距（米）：小点沿弹道摆这么密（观感常量，不做成参数）。</summary>
    const float DotSpacing = 0.35f;
    /// <summary>预览点大小（米）。</summary>
    const float DotSize = 0.085f;
    /// <summary>落点标记（一圈小点）的半径（米）与点数。</summary>
    const float MarkerRadius = 0.42f;
    const int MarkerDots = 12;
    /// <summary>范围圈贴地高度（米）：填充在下、环在上；弹道点靠渲染队列压在它们之上。</summary>
    const float RangeFillY = 0.02f;
    const float RangeRingY = 0.03f;


    /// <summary>点间距（米）——调用方做"跨线段连续摆点"的进位初始化要用它（撞点处补一个红点）。</summary>
    public static float Spacing => DotSpacing;

    Mesh _quad;
    Material _dotMatWhite, _dotMatRed;
    Texture2D _dotTex;
    Texture2D _rangeFillTex;
    Material _rangeRingMat, _rangeFillMat;

    // ⚠️ 2026-10-17：范围圈的**环**改成"按半径生成的固定线宽 mesh"。
    //    原来用的是"环贴图 × 按半径缩放"→ 环带宽 = `半径 × 2 × 8%`，**半径越大环越粗**（用户实测反馈"边缘有点粗"）。
    Texture2D _whiteTex;          // 1×1 纯白（mesh 版环用：形状由 mesh 决定，贴图只需能出 alpha）
    Mesh _ringMesh;
    float _ringMeshRadius = -1f;  // 已生成 mesh 对应的半径（变了就重建）

    /// <summary>范围圈的**环带宽（米，固定）** —— 不管生效半径多大，环始终这么宽。</summary>
    const float RangeRingWidth = 0.06f;
    /// <summary>环用多少段（够圆即可；只在半径变化时重建一次，不是每帧）。</summary>
    const int RangeRingSegments = 64;

    /// <summary>自建资源齐不齐（不齐就什么都不画 —— 调用方不必自己判空）。</summary>
    public bool Ready => _quad != null && _dotMatWhite != null && _dotMatRed != null;

    /// <summary>建"预览用的小资源"（自建自销：在 `Awake` 里调一次）。</summary>
    public void Build()
    {
        _quad = BuildUnitQuad();
        _dotTex = BuildDotTexture();
        _dotMatWhite = BuildDotMaterial("投掷预览-白", Color.white, _dotTex);
        _dotMatRed = BuildDotMaterial("投掷预览-红", new Color(1f, 0.32f, 0.22f, 1f), _dotTex);

        // 范围指示圈：**环**走"按半径生成的固定线宽 mesh"（线宽恒定、不随半径变粗）；
        // **填充**仍是圆盘贴图（它没有"线宽"问题，按半径缩放即可）
        _rangeFillTex = UIUtil.RingTexture(128, 0f, 1f, 0.02f);
        _whiteTex = BuildWhiteTexture();   // 1×1 纯白：`Sprites/Default` 没有贴图会把整块裁掉
        _rangeRingMat = BuildDotMaterial("投掷范围-环", new Color(1f, 0.55f, 0.1f, 0.95f), _whiteTex);
        _rangeFillMat = BuildDotMaterial("投掷范围-填充", new Color(1f, 0.55f, 0.1f, 0.10f), _rangeFillTex);
        _rangeFillMat.renderQueue = 2985;                 // 填充最下
        _rangeRingMat.renderQueue = 2990;                 // 环在其上
        // （弹道点用默认队列 3000 → 永远压在范围圈之上，不受两者高度差影响）
    }

    /// <summary>销毁自建的网格 / 贴图 / 材质（在 `OnDestroy` 里调一次）。</summary>
    public void Dispose()
    {
        if (_quad != null) Object.Destroy(_quad);
        if (_dotTex != null) Object.Destroy(_dotTex);
        if (_dotMatWhite != null) Object.Destroy(_dotMatWhite);
        if (_dotMatRed != null) Object.Destroy(_dotMatRed);
        if (_rangeFillTex != null) Object.Destroy(_rangeFillTex);
        if (_rangeRingMat != null) Object.Destroy(_rangeRingMat);
        if (_rangeFillMat != null) Object.Destroy(_rangeFillMat);
        if (_whiteTex != null) Object.Destroy(_whiteTex);
        if (_ringMesh != null) Object.Destroy(_ringMesh);
    }

    /// <summary>沿一段线段按 <see cref="DotSpacing"/> 摆点（`carry` = 已走过的"到下一个点还差"的进位，跨段连续）。
    /// `red` = 撞点之后那一段（红点）。</summary>
    public void DrawDots(Vector3 a, Vector3 b, Quaternion face, bool red, Camera cam, ref float carry)
    {
        if (cam == null || !Ready) return;
        float len = (b - a).magnitude;
        if (len <= 0.0001f) return;
        Vector3 dir = (b - a) / len;
        Material mat = red ? _dotMatRed : _dotMatWhite;

        float t = 0f;
        while (true)
        {
            float need = DotSpacing - carry;          // 还差多少米走到下一个点
            if (need > len - t) { carry += len - t; return; }
            t += need;
            DrawDot(a + dir * t, face, mat, cam);
            carry = 0f;
            if (t >= len) return;
        }
    }

    /// <summary>落点标记：地面上一圈小点（只在"白线真正到达落点"时画）。</summary>
    public void DrawLandingMarker(Vector3 center, Camera cam)
    {
        if (cam == null || !Ready) return;
        Vector3 c = center; c.y = ThrowableBallistics.LandY + 0.02f;
        Quaternion flat = Quaternion.Euler(90f, 0f, 0f);
        for (int i = 0; i < MarkerDots; i++)
        {
            float a = i * (360f / MarkerDots) * Mathf.Deg2Rad;
            Vector3 p = c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * MarkerRadius;
            var m = Matrix4x4.TRS(p, flat, Vector3.one * (DotSize * 1.1f));
            Graphics.DrawMesh(_quad, m, _dotMatWhite, 0, cam, 0, null, false, false, false);
        }
    }

    /// <summary>范围指示圈：画在**准星落点**的地面上（细环 + 半透明填充）。
    /// ⚠️ 口径（用户 2026-10-12 定）：它只是**尺子** —— 把"生效半径有多大"画给你看；
    ///    **真实杀伤区**（含"隔墙不掉血"、弹跳后实际落点）请结合弹道自行判断。
    ///    "要不要画"由调用方判断（效果卡的「显示范围指示圈」+ `DangerRadius > 0`）。</summary>
    public void DrawRangeIndicator(Vector3 center, float radius, Camera cam)
    {
        if (cam == null || !Ready) return;
        if (radius <= 0.01f) return;

        // 颜色走资源总表（运行中实时可调）：每帧覆盖一次材质颜色（两个专用材质，不涉及合批问题）
        var ga = GameAssets.I;
        if (ga != null)
        {
            _rangeRingMat.color = ga.throwableRangeRingColor;
            _rangeFillMat.color = ga.throwableRangeFillColor;
        }

        Quaternion flat = Quaternion.Euler(90f, 0f, 0f);
        float d = radius * 2f;   // 填充贴图的外缘占比 = 1 → 圆盘半径正好落在 r 上
        var mFill = Matrix4x4.TRS(new Vector3(center.x, RangeFillY, center.z), flat, new Vector3(d, d, 1f));
        Graphics.DrawMesh(_quad, mFill, _rangeFillMat, 0, cam, 0, null, false, false, false);

        // 环：用按半径生成的固定线宽 mesh（⭐ 线宽恒定，不随半径变粗）
        EnsureRingMesh(radius);
        if (_ringMesh != null)
        {
            // ⚠️ 这里必须用 `Quaternion.identity`（**不能**用上面那个 `flat`）：
            //    `_ringMesh` 的顶点本来就建在 **XZ 平面**（y = 0，已经躺平），再套一次"绕 X 转 90°"的 `flat`
            //    会把它**立成一扇拱门**（2026-10-17 实测踩到）；而填充用的 `_quad` 是 **XY 平面**的方片，
            //    才需要 `flat` 去转平 —— 两者坐标系不同，别混用。
            //    mesh 已按真实半径建好 → 这里**缩放 1**（原来环用 `d`，正是"环随半径变粗"的根源）
            var mRing = Matrix4x4.TRS(new Vector3(center.x, RangeRingY, center.z), Quaternion.identity, Vector3.one);
            Graphics.DrawMesh(_ringMesh, mRing, _rangeRingMat, 0, cam, 0, null, false, false, false);
        }
    }

    void DrawDot(Vector3 p, Quaternion face, Material mat, Camera cam)
    {
        var m = Matrix4x4.TRS(p, face, Vector3.one * DotSize);
        Graphics.DrawMesh(_quad, m, mat, 0, cam, 0, null, false, false, false);
    }

    // ==================== 自建的小资源（自建自销） ====================

    static Mesh BuildUnitQuad()
    {
        var mesh = new Mesh();
        mesh.name = "投掷预览点（运行时）";
        mesh.vertices = new Vector3[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f)
        };
        mesh.uv = new Vector2[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
        };
        mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>1×1 纯白贴图。`Sprites/Default` 若没有贴图会把整块裁掉，所以 mesh 版的环也要给它一张全白的。</summary>
    static Texture2D BuildWhiteTexture()
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.name = "投掷范围-白（运行时）";
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        return tex;
    }

    /// <summary>按半径生成"**固定带宽**"的圆环 mesh —— ⭐ 这是"环不随半径变粗"的关键：形状由 mesh 决定，
    /// 外圈精确落在 `radius` 上、内圈在 `radius - RangeRingWidth`，所以无论半径多大，环带都是同样宽。
    /// 📌 只在半径变化时重建（半径是常量 → 实际上每种效果卡只建一次）；绕序与 `BuildUnitQuad` 一致（法线朝 +Y）。</summary>
    void EnsureRingMesh(float radius)
    {
        if (_ringMesh != null && Mathf.Approximately(_ringMeshRadius, radius)) return;

        float outer = radius;
        float inner = Mathf.Max(0f, radius - RangeRingWidth);
        int seg = RangeRingSegments;
        var verts = new Vector3[seg * 2];
        var tris = new int[seg * 6];
        for (int i = 0; i < seg; i++)
        {
            float a = i / (float)seg * Mathf.PI * 2f;
            float cs = Mathf.Cos(a), sn = Mathf.Sin(a);
            verts[i * 2] = new Vector3(cs * inner, 0f, sn * inner);       // 内圈
            verts[i * 2 + 1] = new Vector3(cs * outer, 0f, sn * outer);   // 外圈
            int n = (i + 1) % seg;
            int t = i * 6;
            // ⚠️ 绕序：从 +Y 往下看是**逆时针** → 法线朝 +Y（这样即使 shader 从 `Sprites/Default`（Cull Off）
            //    回落到单面渲染的着色器，从俯视视角仍然可见）。2026-10-17 实测确认过绕序方向。
            tris[t] = i * 2;     tris[t + 1] = n * 2 + 1; tris[t + 2] = n * 2;
            tris[t + 3] = i * 2; tris[t + 4] = i * 2 + 1; tris[t + 5] = n * 2 + 1;
        }

        if (_ringMesh == null)
        {
            _ringMesh = new Mesh();
            _ringMesh.name = "投掷范围环（运行时）";
        }
        _ringMesh.Clear();
        _ringMesh.vertices = verts;
        _ringMesh.triangles = tris;
        _ringMesh.RecalculateBounds();
        _ringMeshRadius = radius;
    }

    static Texture2D BuildDotTexture()
    {
        const int S = 32;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        tex.name = "投掷预览点贴图（运行时）";
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        float r = S * 0.5f;
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
                float a = Mathf.Clamp01(1f - d);
                a *= a;                                  // 更像"点"
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        return tex;
    }

    static Material BuildDotMaterial(string name, Color color, Texture2D dotTex)
    {
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Unlit/Transparent");
        var mat = new Material(shader);
        mat.name = name + "（运行时）";
        mat.mainTexture = dotTex;                        // ⚠️ 光有点贴图还不够：没有它就是一个方块
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        mat.color = color;
        return mat;
    }
}
