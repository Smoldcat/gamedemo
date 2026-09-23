using UnityEngine;

/// <summary>
/// **界面总开关**（"模态界面"的唯一出口）。
///
/// 解决的问题：打开背包 / 箱子时要同时做四件事 —— **暂停游戏 / 隐藏整块 HUD / 隐藏准星 / 显示虚拟光标**。
/// 以前这四件事散在背包面板里，而且是"开就藏、关就放"的简单开关：
///   一旦**同时存在两个面板**（箱子 + 背包），关掉其中一个就会把 HUD 和准星错误地放出来（另一个还开着呢）。
///
/// 做法：**按"当前有几个界面开着"记账**，只在 **0 ↔ 非 0** 的两个瞬间真正执行那四件事
/// → 天然支持"多个面板共存 / 一起关"，而且不关心是谁开的。
///
/// ⚠️ 全项目**只有这里**能改这四件事：面板自己不许再直接调 `GameState.SetPaused` /
///    `HUD.SetContentVisible` / `Crosshair.SetVisible`（否则又会变成"两个地方各管一半"）。
///
/// ⚠️ 约定：**每个界面都必须在玩家死亡时关闭自己**（背包 / 箱子都已如此）——
///    这样"死亡 → 重开"时计数会自然归零；万一漏了，`StaticReset` 里的 `ResetStatics` 也会兜住。
/// </summary>
public static class UIState
{
    static int _openCount;   // 当前打开着的界面数（背包 / 箱子…）
    static bool _applied;    // 上一次"真正执行过"的状态（只用于在 0 ↔ 非 0 的瞬间动手）

    /// <summary>当前是否有界面开着。</summary>
    public static bool AnyOpen => _openCount > 0;

    /// <summary>界面打开时调（每个界面 +1）。</summary>
    public static void Push()
    {
        _openCount++;
        Apply();
    }

    /// <summary>界面关闭时调（每个界面 -1）。</summary>
    public static void Pop()
    {
        _openCount = Mathf.Max(0, _openCount - 1);
        Apply();
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）：清空计数。
    /// ⚠️ 这里**不调用 Apply** —— StaticReset 发生在子系统注册阶段（场景还没加载完），此时
    ///    HUD / Crosshair 都还没就绪；游戏时间由 `GameState.Reset()` 负责恢复。</summary>
    internal static void ResetStatics()
    {
        _openCount = 0;
        _applied = false;
    }

    static void Apply()
    {
        bool open = AnyOpen;
        if (open == _applied) return;   // 只在 0 ↔ 非 0 的瞬间动手（中间态什么都不做）
        _applied = open;

        GameState.SetPaused(open);                     // 统一暂停入口（内部是引用计数）
        HUD.SetContentVisible(!open);                  // 整块 HUD 显隐
        if (Crosshair.InstanceUI != null)              // 准星显隐（它内部同时负责光标模式：锁定/解锁 + 虚拟光标）
            Crosshair.InstanceUI.SetVisible(!open);
    }
}
