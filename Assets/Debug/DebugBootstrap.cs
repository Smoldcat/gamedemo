#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;

/// <summary>
/// 调试工具的**自举入口**：运行时自动创建调试对象，**不需要往任何场景 / prefab 挂组件**
/// （这是"零场景改动"约束的关键 —— 发布前删掉 `Assets/Debug/` 整个文件夹即可，场景与 prefab 一个字节都不用碰）。
///
/// 生命周期：
///   · `RuntimeInitializeOnLoadMethod(AfterSceneLoad)` 在**首次场景加载后**执行一次；
///   · 对象 `DontDestroyOnLoad` → 死亡后 `LoadScene` 重开一局时，它跟着存活
///     （而 `PlayerController` 是每局重建的，所以取数据必须**每次现取**——`DebugRead` 正是这么做的）；
///   · 本类**没有静态字段**，所以"重开一局"不需要任何清理，也就不用挂 `StaticReset`。
///
/// ⚠️ 本文件属于 `Assets/Debug/`：发布前整个文件夹删除，详见 README.md。
/// </summary>
public static class DebugBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        // 幂等守卫：理论上只会跑一次；万一被重复触发（关闭 Domain Reload 等），不要出现两个调试对象
        if (Object.FindObjectOfType<DebugHud>() != null) return;

        var go = new GameObject("[调试工具]");
        Object.DontDestroyOnLoad(go);

        var hud = go.AddComponent<DebugHud>();
        var panel = go.AddComponent<DebugPanel>();
        panel.hud = hud;        // 详情面板里的"常驻条开关"要用它（显式注入，避免静态单例）

        // 就绪日志：既是给用户的提示，也是"按键没反应"时最快的诊断锚点
        //（没看到这条 = 调试工具没建起来；看到了但按键没日志 = 输入没到游戏窗口/被输入法吞了）
        Debug.Log("[调试工具] 已就绪：按 ~ 或 F1 开关调试面板（左上角是常驻实时条）");
    }
}
#endif
