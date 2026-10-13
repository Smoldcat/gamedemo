using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// **半分辨率烟 · 实验件（可丢弃，2026-10-13）** —— 只为回答两个问题而建：
///   ① 把烟按半分辨率单独渲染，**能省多少 ms**？  ② **伪影长什么样**？
///
/// 它做三件事（都不碰游戏逻辑）：
///   1. 建一台只画 `Layers.Smoke` 的副相机（RT = 屏幕 × `renderScale`，透明底），并让它跟随主相机；
///   2. 挂到主相机上用 `OnRenderImage` 把那张 RT 合成回主画面（着色器 `Hidden/SmokeHalfResComposite`）；
///   3. 提供 `SetHalfRes(bool)` 做 A/B 开关 + `StartPerfTest / StartShotTest` 自动跑测试并写报告。
///
/// ⚠️ **本实验刻意不做**（产品化才补）：**深度感知**（所以烟会穿墙、会盖住战争迷雾暗幕）、
///    与其它 FX 的排序、移动端带宽、HDR。
/// ⚠️ **A 模式零开销**：`halfRes = false` 时整个组件 `enabled = false`（`OnRenderImage` 根本不执行）
///    + 主相机 cullingMask 原样恢复 → 与"没有这个组件"逐位等价，所以 A/B 对比是干净的。
/// ⚠️ 退出 Play 一切自动消失（对象/RT/材质都是运行时创建，不落场景、不落磁盘）。
/// </summary>
[RequireComponent(typeof(Camera))]
public class SmokeHalfResProbe : MonoBehaviour
{
    [Header("实验参数")]
    [Tooltip("烟 RT 相对屏幕的比例：0.5 = 半分辨率（每层只算 1/4 像素）")]
    [Range(0.1f, 1f)] public float renderScale = 0.5f;
    [Tooltip("烟雾效果卡路径（拿它的粒子参数来生成测试用烟）")]
    public string cfgPath = "Assets/Items/Throwables/Effect_烟雾.asset";
    [Tooltip("截图输出目录（相对工程根）")]
    public string shotDir = "临时文件夹";

    [Header("状态（只读）")]
    public bool halfRes = true;
    [TextArea(6, 24)] public string Report = "（未跑）";
    public bool Busy { get; private set; }

    Camera _main, _cam;
    RenderTexture _rt;
    Material _mat;
    int _origMainMask;
    bool _maskSaved;

    static int SmokeBitMask { get { return Layers.Smoke >= 0 ? (1 << Layers.Smoke) : 0; } }

    // ------------------------------------------------------------------ 渲染路径

    void OnEnable() { if (halfRes) Activate(); }
    void OnDisable() { Deactivate(); }

    /// <summary>A/B 开关。true = 半分辨率（烟进副相机 + 合成）；false = 原样（**零额外开销**）。</summary>
    public void SetHalfRes(bool on)
    {
        halfRes = on;
        if (on) { Activate(); enabled = true; }
        else { enabled = false; Deactivate(); }
    }

    void Activate()
    {
        EnsureSetup();
        if (_cam == null) return;
        if (!_maskSaved && _main != null) { _origMainMask = _main.cullingMask; _maskSaved = true; }
        if (_maskSaved) _main.cullingMask = _origMainMask & ~SmokeBitMask;   // 主画面不再画烟
        SyncRT();
        _cam.enabled = true;
    }

    void Deactivate()
    {
        if (_cam != null) _cam.enabled = false;
        if (_maskSaved && _main != null) { _main.cullingMask = _origMainMask; _maskSaved = false; }
    }

    void LateUpdate()
    {
        // 主相机的 FOV（开镜会变）/ 正交尺寸 / 远近裁剪 每帧都可能变 → 副相机跟着同步
        if (halfRes) SyncRT();
    }

    void EnsureSetup()
    {
        if (_main == null) _main = GetComponent<Camera>();
        if (_main == null) { Report = "挂载对象上没有 Camera"; return; }

        if (_cam == null)
        {
            var go = new GameObject("~烟半分辨率相机（实验）");
            go.transform.SetParent(_main.transform, false);   // 跟随主相机 → billboard 朝向与主画面一致
            _cam = go.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);  // 透明底：没烟的地方 alpha = 0
            _cam.cullingMask = SmokeBitMask;
            _cam.depth = _main.depth - 1f;                     // 写在主相机之前（写自己的 RT，不抢屏幕）
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.useOcclusionCulling = false;
            _cam.orthographic = _main.orthographic;
            _cam.nearClipPlane = _main.nearClipPlane;
            _cam.farClipPlane = _main.farClipPlane;
            _cam.enabled = false;
        }

        if (_mat == null)
        {
            var sh = Shader.Find("Hidden/SmokeHalfResComposite");
            if (sh == null) { Report = "找不到着色器 Hidden/SmokeHalfResComposite（Assets/Shaders/烟半分辨率合成.shader）"; return; }
            _mat = new Material(sh);
            _mat.hideFlags = HideFlags.DontSave;
        }
    }

    void SyncRT()
    {
        if (_cam == null || _main == null) return;

        int w = Mathf.Max(1, Mathf.RoundToInt(Screen.width * renderScale));
        int h = Mathf.Max(1, Mathf.RoundToInt(Screen.height * renderScale));
        if (_rt != null && (_rt.width != w || _rt.height != h))
        {
            _cam.targetTexture = null;
            _rt.Release();
            Destroy(_rt);
            _rt = null;
        }
        if (_rt == null)
        {
            // 深度缓冲 0：烟在 RT 里是**单独一层**、材质 ZWrite=Off/ZTest=LEqual，没有别的物体要挡它 → 不需要深度
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _rt.name = "烟半分辨率RT（实验）";
            _rt.hideFlags = HideFlags.DontSave;
            _rt.Create();
        }

        _cam.targetTexture = _rt;
        _cam.cullingMask = SmokeBitMask;
        _cam.orthographic = _main.orthographic;
        _cam.orthographicSize = _main.orthographicSize;
        _cam.fieldOfView = _main.fieldOfView;
        _cam.nearClipPlane = _main.nearClipPlane;
        _cam.farClipPlane = _main.farClipPlane;
    }

    void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        // ⚠️ 任何分支都必须写 dst（否则屏幕会花）。A 模式本该走不到这里（组件已 disable）。
        if (!halfRes || _rt == null || _mat == null) { Graphics.Blit(src, dst); return; }
        // ⚠️ 烟 RT 必须走**独立属性**传进去：`Blit(src, dst, mat)` 会把 `_MainTex` 占为"场景源图"，
        //    着色器若去采样 `_MainTex` 就变成"把画面和自己加了一遍"（实测整屏泛灰）。
        _mat.SetTexture("_SmokeTex", _rt);
        Graphics.Blit(src, dst, _mat);
    }

    // ------------------------------------------------------------------ 测试驱动

    /// <summary>性能 A/B 测试：在开阔地固定生成 <paramref name="clouds"/> 片烟，交替测帧时间。结果写 <see cref="Report"/>。</summary>
    public void StartPerfTest(int clouds, int windows)
    {
        if (!CanStart()) return;
        StartCoroutine(PerfRoutine(clouds, windows));
    }

    /// <summary>伪影截图测试：开阔地 / 贴着障碍物 / 远处暗区，各拍 A、B 两张。</summary>
    public void StartShotTest()
    {
        if (!CanStart()) return;
        StartCoroutine(ShotRoutine());
    }

    bool CanStart()
    {
        if (!Application.isPlaying) { Report = "只能在 Play 模式跑"; return false; }
        if (Busy) { Report = "已经在跑了（等 Report 更新）"; return false; }
        return true;
    }

    IEnumerator PerfRoutine(int clouds, int windows)
    {
        Busy = true;
        int oldVsync = QualitySettings.vSyncCount, oldTarget = Application.targetFrameRate;
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;   // 关垂直同步：帧时间才反映 GPU 负载

        var sb = new StringBuilder();
        sb.Append("== 半分辨率烟 A/B 测试 ==\n")
          .Append("屏幕 ").Append(Screen.width).Append("×").Append(Screen.height)
          .Append("　RT ").Append(Mathf.RoundToInt(Screen.width * renderScale)).Append("×").Append(Mathf.RoundToInt(Screen.height * renderScale))
          .Append("　烟片数 ").Append(clouds).Append("\n");

        yield return PrepareClouds(clouds);
        sb.Append("存活粒子 ").Append(TotalParticles()).Append(" 颗\n");

        var a = new float[windows];
        var b = new float[windows];
        var ga = new float[windows];   // 每轮的 GPU 帧时间（FrameTimingManager 平均）
        var gb = new float[windows];
        for (int i = 0; i < windows; i++)
        {
            yield return Measure(false, 45, a, i, ga);
            yield return Measure(true, 45, b, i, gb);
            sb.Append("第 ").Append(i + 1).Append(" 轮　基线 ").Append(a[i].ToString("F2")).Append(" ms（GPU ")
              .Append(ga[i].ToString("F2")).Append("）　半分辨率 ").Append(b[i].ToString("F2")).Append(" ms（GPU ")
              .Append(gb[i].ToString("F2")).Append("）\n");
        }

        System.Array.Sort(a);
        System.Array.Sort(b);
        System.Array.Sort(ga);
        System.Array.Sort(gb);
        float ma = a[windows / 2], mb = b[windows / 2];
        float d = ma - mb;
        sb.Append("── 帧时间中位：基线 ").Append(ma.ToString("F2")).Append(" ms　半分辨率 ").Append(mb.ToString("F2"))
          .Append(" ms　省 ").Append(d.ToString("F2")).Append(" ms（")
          .Append((d / Mathf.Max(ma, 0.001f) * 100f).ToString("F1")).Append("%）\n");
        sb.Append("── GPU 中位：基线 ").Append(ga[windows / 2].ToString("F2")).Append(" ms　半分辨率 ")
          .Append(gb[windows / 2].ToString("F2")).Append(" ms　省 ")
          .Append((ga[windows / 2] - gb[windows / 2]).ToString("F2")).Append(" ms\n");

        // 同一时刻的 A / B 对照图
        SetHalfRes(false); yield return new WaitForSeconds(0.3f);
        yield return Capture("烟_perf_A_基线.png");
        SetHalfRes(true); yield return new WaitForSeconds(0.3f);
        yield return Capture("烟_perf_B_半分辨率.png");

        QualitySettings.vSyncCount = oldVsync;
        Application.targetFrameRate = oldTarget;
        sb.Append("截图：").Append(ShotDirPath()).Append("\n");
        Report = sb.ToString();
        Busy = false;
    }

    IEnumerator ShotRoutine()
    {
        Busy = true;
        var sb = new StringBuilder();
        sb.Append("== 半分辨率烟 伪影截图（每处 A / B 各一张）==\n");

        yield return PrepareClouds(4);
        yield return ShotPair(sb, "烟_伪影_开阔地4片");

        yield return PrepareObstacleCloud();
        yield return ShotPair(sb, "烟_伪影_贴着障碍物");

        yield return PrepareDarkCloud();
        yield return ShotPair(sb, "烟_伪影_远处暗区");

        sb.Append("截图：").Append(ShotDirPath()).Append("\n");
        Report = sb.ToString();
        Busy = false;
    }

    // ------------------------------------------------------------------ 测试内部

    IEnumerator Measure(bool half, int frames, float[] store, int idx, float[] gpuStore)
    {
        SetHalfRes(half);
        yield return new WaitForSeconds(0.35f);          // 让管线先稳定
        var buf = new float[frames];
        for (int n = 0; n < frames; n++)
        {
            yield return new WaitForEndOfFrame();
            buf[n] = Time.unscaledDeltaTime * 1000f;
        }
        System.Array.Sort(buf);
        store[idx] = buf[frames / 2];                    // 取中位数（抗偶发卡顿）
        gpuStore[idx] = AverageGpuMs(frames);            // ⚠️ 帧时间会被 CPU 地板盖住 → 必须单独看 GPU
    }

    /// <summary>最近若干帧的 GPU 时间平均值（ms；平台/驱动不支持时返回 -1）。
    /// ⚠️ 单次采样噪声很大（同一状态量到过 1.50 / 0.29）→ 只有"多帧平均"才有意义。</summary>
    float AverageGpuMs(int frames)
    {
        var timings = new FrameTiming[Mathf.Clamp(frames, 8, 120)];
        int got = (int)FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);
        double sum = 0; int n = 0;
        for (int i = 0; i < got; i++)
        {
            double g = timings[i].gpuFrameTime;
            if (g > 0.0001) { sum += g; n++; }
        }
        return n > 0 ? (float)(sum / n) : -1f;
    }

    IEnumerator ShotPair(StringBuilder sb, string tag)
    {
        SetHalfRes(false); yield return new WaitForSeconds(0.4f);
        yield return Capture(tag + "_A基线.png");
        SetHalfRes(true); yield return new WaitForSeconds(0.4f);
        yield return Capture(tag + "_B半分辨率.png");
        sb.Append(tag).Append("：A / B 各一张　");
        yield return null;
    }

    IEnumerator Capture(string file)
    {
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(ShotPath(file));
        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame();            // 等它写盘
    }

    /// <summary>清掉现有烟 → 在主相机正前方按"十字形一簇"生成 N 片（和真实投掷的落点分布接近）→ 等粒子到稳态。</summary>
    IEnumerator PrepareClouds(int clouds)
    {
        ClearClouds();
        yield return new WaitForSeconds(1.2f);

        var cfg = LoadCfg();
        if (cfg == null) { Report = "找不到烟雾效果卡：" + cfgPath; Busy = false; yield break; }

        Camera cam = _main != null ? _main : GetComponent<Camera>();
        Vector3 f = cam.transform.forward; f.y = 0f; f.Normalize();
        Vector3 r = cam.transform.right; r.y = 0f; r.Normalize();
        Vector3 center = cam.transform.position + f * 14f;

        var offs = new[] { Vector3.zero, r * 4f, r * -4f, f * 4f, f * -3f };
        for (int i = 0; i < clouds; i++)
        {
            Vector3 p = center + offs[i % offs.Length];
            SmokeVolume.Spawn(p, cfg);
        }
        yield return new WaitForSeconds(6f);             // 粒子寿命 5s → 6s 后基本满员（稳态）
    }

    /// <summary>把一片烟放到"最近的那个障碍物之后"——用来暴露"没有深度感知"的下场（烟会盖住箱子/墙）。</summary>
    IEnumerator PrepareObstacleCloud()
    {
        ClearClouds();
        yield return new WaitForSeconds(1.2f);
        var cfg = LoadCfg();
        if (cfg == null) { Report = "找不到烟雾效果卡：" + cfgPath; Busy = false; yield break; }

        Camera cam = _main != null ? _main : GetComponent<Camera>();
        Vector3 eye = cam.transform.position;
        Vector3 dir = cam.transform.forward; dir.y = 0f; dir.Normalize();

        // 找正前方最近的障碍物（Obstacle 层），把烟放到它后面
        Vector3 place = eye + dir * 16f;
        var cols = Physics.OverlapSphere(eye + dir * 8f, 14f, 1 << Layers.Obstacle);
        float best = float.MaxValue; Vector3 bestPos = place;
        for (int i = 0; i < cols.Length; i++)
        {
            Vector3 c = cols[i].bounds.center;
            Vector3 flat = new Vector3(c.x - eye.x, 0f, c.z - eye.z);
            float along = Vector3.Dot(flat, dir);
            if (along < 3f || along > 25f) continue;                  // 太近/太远都不要
            if (Vector3.Dot(flat.normalized, dir) < 0.75f) continue;  // 必须在正前方
            if (along < best) { best = along; bestPos = eye + dir * (along + cols[i].bounds.extents.magnitude * 0.5f + 1.5f); }
        }
        SmokeVolume.Spawn(bestPos, cfg);
        yield return new WaitForSeconds(6f);
    }

    /// <summary>把一片烟放到远处（视野扇形半径之外 → 战争迷雾的暗幕应该压暗它）——用来暴露"合成在暗幕之后"的下场。</summary>
    IEnumerator PrepareDarkCloud()
    {
        ClearClouds();
        yield return new WaitForSeconds(1.2f);
        var cfg = LoadCfg();
        if (cfg == null) { Report = "找不到烟雾效果卡：" + cfgPath; Busy = false; yield break; }

        Camera cam = _main != null ? _main : GetComponent<Camera>();
        Vector3 f = cam.transform.forward; f.y = 0f; f.Normalize();
        SmokeVolume.Spawn(cam.transform.position + f * 38f, cfg);   // 扇形 25m 之外 → 暗区
        yield return new WaitForSeconds(6f);
    }

    void ClearClouds()
    {
        var alive = new List<SmokeVolume>(SmokeVolume.All);
        for (int i = 0; i < alive.Count; i++)
            if (alive[i] != null) Destroy(alive[i].gameObject);
    }

    int TotalParticles()
    {
        int n = 0;
        var pss = FindObjectsOfType<ParticleSystem>();
        for (int i = 0; i < pss.Length; i++) n += pss[i].particleCount;
        return n;
    }

    SmokeEffectData LoadCfg()
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.LoadAssetAtPath<SmokeEffectData>(cfgPath);
#else
        return null;
#endif
    }

    string ShotDirPath()
    {
        var root = System.IO.Directory.GetParent(Application.dataPath).FullName;
        var dir = System.IO.Path.Combine(root, shotDir);
        if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    string ShotPath(string file) { return System.IO.Path.Combine(ShotDirPath(), file); }
}
