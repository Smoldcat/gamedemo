#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 调试工具的**可视化层**（2026-10-16 新增）——三件事，全部**纯显示**：
///   ① **敌人视野扇形**：按每个敌人自己的 `sightRange` / `fov` 画半透明扇面，
///      并沿弧线打一圈 `Layers.Blocker` 射线 → **被墙/箱子挡住的部分会被削短**（一眼看出盲区在哪）；
///   ② **已发现高亮**：看得见玩家的敌人扇形变红 —— 读的是 `EnemyAI.SeesPlayerNow`
///      （**游戏自己那条判定的结果**，不是 `InCombat` 那种把"刚挨过打"也算进来的近似）；
///   ③ **判定圈**：每个敌人脚下画"身体判定半径"（`PlayerController.bodyAimRadius`），
///      并在**屏幕空间**画爆头判定圈（可见头半径 × `headshotGateScale`，公式与开火判定同源）。
///
/// ⚠️ 不写任何玩法状态、不改数值、不需要场景/prefab 改动；随 `Assets/Debug/` 一起删（正式包被 `#if` 剔除）。
/// ⚠️ 开销控制：扇形网格**按敌人缓存并原地更新顶点**（不每帧新建 Mesh）；遮挡射线**节流** 0.12s
///    （与 `EnemyAI` 自己的索敌同频）+ 每扇形 16 段 → 6 个敌人约 800 条/秒，与玩法自身同量级。
/// ⚠️ 扇形半径是节流采样的 → 敌人**快速转身**时扇形轮廓会滞后最多 0.12s（观感问题，不影响判断）。
/// </summary>
public class DebugOverlay : MonoBehaviour
{
    // ---- 开关（由 DebugPanel 的「视觉」页写）----
    // ⚠️ **默认全关**（2026-10-16 用户定）：这三样都是"世界/屏幕覆盖层"，会盖住大片画面 ——
    //    与"常驻条（只读数据、不遮画面 → 默认可开）"不同，它们必须**手动在面板里开**，
    //    否则一进 Play 满屏红青扇形，反而看不清游戏本身。
    public bool showCones = false;      // ① 视野扇形
    public bool highlightSeen = true;   // ② 已发现高亮（只在①开着时才有意义 → 默认保持"开"，开扇形即生效）
    public bool showRanges = false;     // ③ 判定圈（身体半径 + 爆头圈）

    const int ArcSegments = 16;         // 每扇形 16 段
    const float CastInterval = 0.12f;   // 遮挡射线节流（秒）
    const int RingTexSize = 128;        // 屏幕圈贴图尺寸

    static readonly Color ConeIdle = new Color(0.35f, 0.85f, 1f, 0.09f);   // 常态：淡青
    static readonly Color ConeSeen = new Color(1f, 0.32f, 0.26f, 0.13f);   // 已发现：红（多个叠一起会累积，
                                                                           // 所以透明度调低一档：2026-10-16 实测 0.20 时半屏偏红、压屏）
    static readonly Color BodyRing = new Color(1f, 0.75f, 0.25f, 0.9f);    // 身体判定半径
    static readonly Color HeadGate = new Color(1f, 0.4f, 0.35f, 0.95f);    // 爆头判定圈
    static readonly Color HeadVis = new Color(0.6f, 1f, 0.7f, 0.55f);      // 可见头圈（淡）

    /// <summary>一个敌人对应一个扇形：网格缓存 + 顶点数组（原地更新，不反复 new）。</summary>
    sealed class Cone
    {
        public EnemyAI ai;
        public Mesh mesh;
        public Vector3[] verts;      // [0] = 圆心，[1..Seg+1] = 弧点
        public int[] tris;
        public float[] radii;        // 每个弧点的半径（射线结果，节流更新）
        public float nextCast;
    }

    readonly List<Cone> _cones = new List<Cone>();
    readonly List<Image> _ringPool = new List<Image>();   // 屏幕圈对象池
    readonly List<EnemyAI> _snapshot = new List<EnemyAI>(32);

    Mesh _unitRing;        // 单位圆环（半径 1、平躺 XZ 平面）→ 用缩放表示各种半径
    Canvas _canvas;
    RectTransform _canvasRt;
    Sprite _ringSprite;

    // ==================== 每帧 ====================

    void LateUpdate()
    {
        var cam = CameraCache.Main;
        if (cam == null) { HideAll(); return; }

        // ⚠️ 先快照再遍历：敌人死亡会立刻从 `All` 里移除（边遍历边改 = InvalidOperationException）
        _snapshot.Clear();
        var all = EnemyAI.All;
        for (int i = 0; i < all.Count; i++)
            if (all[i] != null) _snapshot.Add(all[i]);

        if (showCones) DrawCones(cam); else HideCones();
        if (showRanges) { DrawBodyRings(cam); DrawHeadRings(cam); } else HideRings();
    }

    // ==================== ① + ② 视野扇形 ====================

    void DrawCones(Camera cam)
    {
        // 清掉已销毁敌人的网格
        for (int i = _cones.Count - 1; i >= 0; i--)
        {
            if (_cones[i].ai == null)
            {
                if (_cones[i].mesh != null) Destroy(_cones[i].mesh);
                _cones.RemoveAt(i);
            }
        }

        for (int i = 0; i < _snapshot.Count; i++)
        {
            var ai = _snapshot[i];
            var h = ai.HealthRef;
            if (h == null || !h.alive) continue;   // 死人不画

            var c = GetOrCreateCone(ai);
            UpdateConeCast(c, ai);                 // 节流射线 → radii
            BuildConeMesh(c, ai);                  // 按当前朝向把顶点铺到世界空间

            var mat = Effects.SharedAlpha(highlightSeen && ai.SeesPlayerNow ? ConeSeen : ConeIdle);
            Graphics.DrawMesh(c.mesh, Matrix4x4.identity, mat, 0, cam, 0, null, false, false, false);
        }
    }

    Cone GetOrCreateCone(EnemyAI ai)
    {
        for (int i = 0; i < _cones.Count; i++)
            if (_cones[i].ai == ai) return _cones[i];

        int n = ArcSegments + 2;
        var c = new Cone
        {
            ai = ai,
            mesh = new Mesh { name = "DebugVisionCone" },
            verts = new Vector3[n],
            tris = new int[ArcSegments * 3],
            radii = new float[ArcSegments + 1],
        };
        for (int s = 0; s < ArcSegments; s++)
        {
            c.tris[s * 3 + 0] = 0;
            c.tris[s * 3 + 1] = s + 1;
            c.tris[s * 3 + 2] = s + 2;
        }
        for (int s = 0; s <= ArcSegments; s++) c.radii[s] = Mathf.Max(0.1f, ai.sightRange);
        c.mesh.vertices = c.verts;
        c.mesh.triangles = c.tris;
        _cones.Add(c);
        return c;
    }

    /// <summary>节流：沿弧线打一圈 `Layers.Blocker` 射线，把每个弧点的半径削到"撞墙距离"。</summary>
    void UpdateConeCast(Cone c, EnemyAI ai)
    {
        if (Time.time < c.nextCast) return;
        c.nextCast = Time.time + CastInterval;

        float half = Mathf.Clamp(ai.fov, 1f, 360f) * 0.5f;
        float range = Mathf.Max(0.1f, ai.sightRange);
        Vector3 origin = OnGround(ai.transform.position);
        Vector3 fwd = FlatForward(ai.transform.forward);

        for (int s = 0; s <= ArcSegments; s++)
        {
            float ang = Mathf.Lerp(-half, half, s / (float)ArcSegments);
            Vector3 dir = Quaternion.Euler(0f, ang, 0f) * fwd;
            float r = range;
            if (Physics.Raycast(origin, dir, out RaycastHit hit, range, Layers.Blocker, QueryTriggerInteraction.Ignore))
                r = Mathf.Max(0.05f, hit.distance);
            c.radii[s] = r;
        }
    }

    /// <summary>把弧点铺到世界空间（圆心 + 各弧点），网格顶点**原地更新**。</summary>
    void BuildConeMesh(Cone c, EnemyAI ai)
    {
        float half = Mathf.Clamp(ai.fov, 1f, 360f) * 0.5f;
        Vector3 origin = OnGround(ai.transform.position);
        Vector3 fwd = FlatForward(ai.transform.forward);

        c.verts[0] = origin;
        for (int s = 0; s <= ArcSegments; s++)
        {
            float ang = Mathf.Lerp(-half, half, s / (float)ArcSegments);
            Vector3 dir = Quaternion.Euler(0f, ang, 0f) * fwd;
            c.verts[s + 1] = origin + dir * c.radii[s];
        }
        c.mesh.vertices = c.verts;
        c.mesh.RecalculateBounds();   // 顶点直接是世界坐标 → 包围盒必须跟着更新，否则会被视锥剔除掉
    }

    void HideCones()
    {
        for (int i = 0; i < _cones.Count; i++)
            if (_cones[i].ai == null && _cones[i].mesh != null) Destroy(_cones[i].mesh);
        _cones.Clear();
    }

    // ==================== ③ 判定圈 ====================

    /// <summary>身体判定半径：世界空间地面上的一圈（判定是"子弹到敌人的距离 ≤ bodyAimRadius"，画圈即它的投影）。</summary>
    void DrawBodyRings(Camera cam)
    {
        var pc = PlayerController.Instance;
        if (pc == null) return;
        float radius = Mathf.Max(0.05f, pc.bodyAimRadius);
        EnsureUnitRing();
        var mat = Effects.SharedAlpha(BodyRing);
        for (int i = 0; i < _snapshot.Count; i++)
        {
            var ai = _snapshot[i];
            var h = ai.HealthRef;
            if (h == null || !h.alive) continue;
            var m = Matrix4x4.TRS(OnGround(ai.transform.position), Quaternion.identity, new Vector3(radius, 1f, radius));
            Graphics.DrawMesh(_unitRing, m, mat, 0, cam, 0, null, false, false, false);
        }
    }

    /// <summary>屏幕空间：每个敌人的「可见头圈（淡）」+「爆头判定圈（亮）」。
    /// 判定圈半径 = 可见头半径 × `headshotGateScale` —— 与 `PlayerController.GetAimState` 同一套公式的复刻
    /// （那边是 private static，没法直接调；这里只读它的 public 字段，**没有改核心**）。</summary>
    void DrawHeadRings(Camera cam)
    {
        var pc = PlayerController.Instance;
        if (pc == null) { HideRings(); return; }
        EnsureCanvas();

        int used = 0;
        for (int i = 0; i < _snapshot.Count; i++)
        {
            var ai = _snapshot[i];
            var h = ai.HealthRef;
            if (h == null || !h.alive || ai.head == null) continue;

            Vector3 headPos = ai.head.WorldPosition;
            Vector3 s0 = cam.WorldToScreenPoint(headPos);
            if (s0.z <= 0f) continue;                                     // 在相机背后
            Vector3 s1 = cam.WorldToScreenPoint(headPos + cam.transform.right * ai.head.radius);
            float visibleR = Vector2.Distance(new Vector2(s0.x, s0.y), new Vector2(s1.x, s1.y));
            if (visibleR < 1f) continue;                                  // 太远/太小，画了也看不见

            SetRing(used++, s0, visibleR, HeadVis);
            float gate = visibleR * Mathf.Max(0f, pc.headshotGateScale);
            if (gate > 0.5f) SetRing(used++, s0, gate, HeadGate);
        }
        for (int i = used; i < _ringPool.Count; i++) _ringPool[i].gameObject.SetActive(false);
    }

    void SetRing(int i, Vector3 screenPos, float pixelRadius, Color col)
    {
        var img = EnsureRing(i);
        img.gameObject.SetActive(true);
        img.color = col;
        var rt = (RectTransform)img.transform;
        float sf = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvasRt, new Vector2(screenPos.x, screenPos.y), null, out var local))
            rt.anchoredPosition = local;
        float d = 2f * pixelRadius / sf;      // UGUI 的尺寸是"画布单位" → 要除以 scaleFactor
        rt.sizeDelta = new Vector2(d, d);
    }

    Image EnsureRing(int i)
    {
        while (_ringPool.Count <= i)
        {
            var go = new GameObject("HeadRing" + _ringPool.Count);
            go.transform.SetParent(_canvasRt, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            var img = go.AddComponent<Image>();
            img.sprite = RingSprite();
            img.raycastTarget = false;
            _ringPool.Add(img);
        }
        return _ringPool[i];
    }

    Sprite RingSprite()
    {
        if (_ringSprite != null) return _ringSprite;
        var tex = UIUtil.RingTexture(RingTexSize, 0.86f, 1f, 0.04f);   // 细线圈（复用 UIUtil 的圆环实现）
        _ringSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        return _ringSprite;
    }

    void HideRings()
    {
        for (int i = 0; i < _ringPool.Count; i++)
            if (_ringPool[i] != null) _ringPool[i].gameObject.SetActive(false);
    }

    void HideAll() { HideCones(); HideRings(); }

    // ==================== 基础设施 ====================

    void EnsureCanvas()
    {
        if (_canvas != null) return;
        _canvas = UIUtil.NewOverlayCanvas("DebugOverlayCanvas", 220);
        // ⚠️ 挂到本组件（常驻对象）下：`NewOverlayCanvas` 建的是场景根节点，死亡重开 LoadScene 会把它销毁
        _canvas.transform.SetParent(transform, false);
        _canvasRt = _canvas.GetComponent<RectTransform>();
        Layers.SetRecursively(_canvas.gameObject, UIUtil.UILayer);
    }

    void EnsureUnitRing()
    {
        if (_unitRing != null) return;
        const int seg = 48;
        const float inner = 0.96f;
        var verts = new Vector3[seg * 2];
        var tris = new int[seg * 6];
        for (int i = 0; i < seg; i++)
        {
            float a = i / (float)seg * Mathf.PI * 2f;
            var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            verts[i * 2] = d * inner;
            verts[i * 2 + 1] = d;
            int n = (i + 1) % seg;
            tris[i * 6 + 0] = i * 2;     tris[i * 6 + 1] = i * 2 + 1; tris[i * 6 + 2] = n * 2;
            tris[i * 6 + 3] = i * 2 + 1; tris[i * 6 + 4] = n * 2 + 1; tris[i * 6 + 5] = n * 2;
        }
        _unitRing = new Mesh { name = "DebugUnitRing" };
        _unitRing.vertices = verts;
        _unitRing.triangles = tris;
        _unitRing.RecalculateBounds();
    }

    /// <summary>压到"实体平面"（与玩家/敌人/拾取物同一高度 —— 扇形和圈都贴在这层地面上画）。</summary>
    static Vector3 OnGround(Vector3 p) => new Vector3(p.x, GameConst.EntityPlaneY, p.z);

    /// <summary>把朝向压平到 XZ 平面（俯视角下扇形必须平躺，否则会插进地板里）。</summary>
    static Vector3 FlatForward(Vector3 fwd)
    {
        fwd.y = 0f;
        return fwd.sqrMagnitude < 0.0001f ? Vector3.forward : fwd.normalized;
    }

    void OnDestroy()
    {
        // 谁创建谁销毁（§4 #25）：本类建的 Mesh 与 Sprite 都成对释放
        for (int i = 0; i < _cones.Count; i++)
            if (_cones[i].mesh != null) Destroy(_cones[i].mesh);
        _cones.Clear();
        if (_unitRing != null) Destroy(_unitRing);
        if (_ringSprite != null) UIUtil.DestroySprite(_ringSprite);   // 成对销毁 Sprite + Texture
        if (_canvas != null) Destroy(_canvas.gameObject);
    }
}
#endif
