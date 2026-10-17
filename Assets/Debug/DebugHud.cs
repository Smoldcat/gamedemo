#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// **调试常驻条**：屏幕上一直显示的一小块实时数据（FPS / 帧时间 / DrawCall / 三角面 + 血量 / 移速 / 武器 / 进度）。
///
/// 为什么需要它（用户 2026-10-14 定）：详情的 `~` 面板打开时会暂停游戏（为了能拿鼠标点），
/// 于是"实时观察"这件事只能由常驻条承担 —— 掉帧、血量异常、速度不对，往往发生在**你没盯面板的时候**。
///
/// ⚠️ **必须是独立 Overlay Canvas**：`UIState.Push()` 会把**整块 HUD** 隐藏，
///    常驻条若挂在 HUD 上就会"一开面板它自己先消失"（`VirtualCursor.cs:36` 记过这个坑）。
///    排序 210：盖住所有面板（200），但在飘字（300）/ 虚拟光标（400）之下。
///
/// ⚠️ 本文件属于 `Assets/Debug/`：发布前整个文件夹删除，详见 README.md。
/// </summary>
public class DebugHud : MonoBehaviour
{
    const int SortingOrder = 210;
    const float RefreshInterval = 0.25f;    // 文本刷新节流（避免每帧拼字符串产生 GC）
    const float FpsWindow = 1f;             // "最差帧"统计窗口

    [Tooltip("常驻条当前是否显示（详情面板「杂项」页里可切换）。\n⚠️ 2026-10-17 起**默认关**：它常驻在左上角、和游戏内的血量等信息重复，\n死亡结算界面上也会压住卡片 —— 需要实时观察时按 `~` 打开面板再开。")]
    public bool visible = false;

    Canvas _canvas;
    Text _text;
    float _nextRefresh;

    // ---- FPS 统计（实例字段：本类不引入任何静态状态）----
    float _fpsSmooth;        // 自己做的 EMA：**必须用 unscaledDeltaTime** ——
                             // 面板打开时游戏是暂停的（timeScale=0），用 Time.smoothDeltaTime 会恒为 0、
                             // 让人误以为"掉到 0 帧"（2026-10-14 实测）
    float _windowStart;
    int _worstFps;

    // ---- 渲染统计（Unity Profiler 计数器；取不到就显示 "-"，不影响其它功能）----
    ProfilerRecorder _drawCalls;
    ProfilerRecorder _triangles;

    void Awake()
    {
        BuildUI();

        _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        DebugRead.EnsureGcRecorder();   // GC/帧计数器（本文件与 DebugRead 共用一个原生句柄）
    }

    void OnDestroy()
    {
        // ⚠️ ProfilerRecorder 是原生资源，必须成对 Dispose（项目铁律：事件/资源订阅要配对）
        if (_drawCalls.Valid) _drawCalls.Dispose();
        if (_triangles.Valid) _triangles.Dispose();
        DebugRead.DisposeGcRecorder();

        // 运行时创建的 Canvas 会随本组件一起销毁，但 UI 层设置过的那棵树要显式销毁
        if (_canvas != null) Destroy(_canvas.gameObject);
    }

    /// <summary>显隐常驻条（详情面板的开关调它）。</summary>
    public void SetVisible(bool on)
    {
        visible = on;
        if (_canvas != null) _canvas.gameObject.SetActive(on);
    }

    void Update()
    {
        TrackFpsWindow();
        if (!visible) return;

        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + RefreshInterval;
        RefreshText();
    }

    /// <summary>按窗口统计"最差帧"：每满一个窗口（默认 1s）就重置一次。同时维护 FPS 的 EMA。</summary>
    void TrackFpsWindow()
    {
        float dt = Time.unscaledDeltaTime;   // ⚠️ unscaled：暂停（timeScale=0）时仍要反映真实帧率
        if (dt > 0.0001f)
        {
            int fps = Mathf.RoundToInt(1f / dt);
            if (fps < _worstFps || _worstFps <= 0) _worstFps = fps;
            // EMA（0.1 权重 ≈ 平滑但对突变仍有反应；暂停时不会掉到 0）
            _fpsSmooth = _fpsSmooth <= 0f ? fps : Mathf.Lerp(_fpsSmooth, fps, 0.1f);
        }
        if (Time.unscaledTime - _windowStart >= FpsWindow)
        {
            _windowStart = Time.unscaledTime;
            _worstFps = 0;
        }
    }

    void RefreshText()
    {
        if (_text == null) return;

        float ms = Time.unscaledDeltaTime * 1000f;

        string dcs = _drawCalls.Valid ? _drawCalls.LastValue.ToString() : "-";
        string tris = _triangles.Valid ? FormatCount(_triangles.LastValue) : "-";

        _text.text = string.Format(
            "<color=#4E8F60><b>{0:0} FPS</b>  ({1:0.0} ms, 最差 {2})   DC {3}   三角 {4}</color>\n" +
            "{5}\n" +
            "{6}\n" +
            "{7}\n" +
            "<color=#4E8F60>{8}</color>",
            _fpsSmooth, ms, _worstFps > 0 ? _worstFps.ToString() : "--", dcs, tris,
            DebugRead.HpLine(),
            DebugRead.SpeedLine(),
            DebugRead.RunLine(),
            DebugRead.MemLine());
    }

    static string FormatCount(long v)
    {
        if (v >= 1000000) return (v / 1000000f).ToString("0.0") + "M";
        if (v >= 1000) return (v / 1000f).ToString("0.0") + "k";
        return v.ToString();
    }

    void BuildUI()
    {
        _canvas = UIUtil.NewOverlayCanvas("DebugHudCanvas", SortingOrder);
        // ⚠️ 必须挂到本组件（常驻对象）下：否则死亡重开 `LoadScene` 会销毁这块场景根画布，
        //    而组件常驻 → **常驻条永久消失**（2026-10-14 评审发现）。
        _canvas.transform.SetParent(transform, false);
        // ⭐ 2026-10-17：把 `visible` 的初始值**真正作用到画布**。
        //    原来这里没有这一句 —— 因为默认是 `true`，看不出问题；改成默认 `false` 之后，
        //    若不应用，画布会一直留在激活态（常驻条仍然显示、还白占一层 overdraw）。
        _canvas.gameObject.SetActive(visible);

        // 背板（贴在屏幕**左上角**；纯色黑，终端风 —— 不再用游戏的气泡贴图）
        var bg = DebugUI.NewSolid(_canvas.transform, "HudBg", new Vector2(430f, 96f), Vector2.zero, DebugUI.Bg);
        bg.rectTransform.anchorMin = bg.rectTransform.anchorMax = bg.rectTransform.pivot = new Vector2(0f, 1f);
        bg.rectTransform.anchoredPosition = new Vector2(8f, -8f);

        _text = DebugUI.NewText(_canvas.transform, "HudText", new Vector2(414f, 88f), Vector2.zero, 13, DebugUI.Fg, TextAnchor.UpperLeft);
        var trt = _text.rectTransform;
        trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0f, 1f);
        trt.anchoredPosition = new Vector2(16f, -14f);
        _text.lineSpacing = 1.1f;

        Layers.SetRecursively(_canvas.gameObject, UIUtil.UILayer);
    }
}
#endif
