using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 「可交互物」的契约：世界里的东西（地上的武器 / 血包 / 箱子…）实现它，就能进入「靠近提示 + 按键交互」这套流程。
///
/// 为什么要有它（背景）：以前地上的武器和血包**各自**每帧扫描、各自显示气泡、各自检测按键 ——
/// 而 HUD 的拾取气泡**全局只有一个**：两种东西并存时就会互相抢（气泡每帧闪、按一次键可能同时触发两件事）。
/// 加了箱子之后是三种，问题必然爆发。
///
/// 现在的做法（行业常见）：**同一时刻只认"离玩家最近的那一个"**（单一交互焦点）——
///   · 只显示它的气泡（说明它是什么、按什么键）
///   · 只响应它的按键
/// → 气泡不闪、不会误触，也不需要"同帧互斥"那种补丁。
///
/// ⚠️ 实现者必须在 `Awake` 里 `InteractFocus.Register(this)`、`OnDestroy` 里 `Unregister(this)`
///    （注册必须配注销，否则场景重载后会留幽灵条目）。
/// </summary>
public interface IInteractable
{
    /// <summary>气泡贴在哪个物体旁边（一般是物体自己的 Transform）。</summary>
    Transform InteractAnchor { get; }

    /// <summary>气泡里显示的名字（例：AK74 / 血包 / 军火箱）。
    /// ⚠️ 返回 null 或空串 = **本次不参与**（例：空箱子且勾了"空了不提示"）→ 焦点会跳过它。</summary>
    string InteractLabel { get; }

    /// <summary>气泡里显示的按键。**目前全项目统一 F**（拾取 / 开箱 / 开商店合并成一键，2026-09-28 起；
    /// E 已让位给魔法释放）。</summary>
    string InteractKey { get; }

    /// <summary>多远之内算"能交互"（米）。每个物体可以不一样（地上的枪 1.6 / 血包 1.2…）。</summary>
    float InteractRange { get; }

    /// <summary>玩家按下按键时执行。
    /// **自己负责**处理"收不下 / 已经空了"这类情况（那时也要给玩家提示，而不是静默什么都不做）。</summary>
    void Interact(PlayerController player);
}

/// <summary>
/// 交互焦点：每帧从所有注册的「可交互物」里选出**离玩家最近的一个**，只提示它、只响应它的按键
/// （为什么这样设计见 <see cref="IInteractable"/> 的注释）。
///
/// 唯一入口：`PlayerController.Update` 每帧调一次 `Tick` —— **别在别处再扫一遍**，
/// 否则又会出现"两个地方各显示一次气泡"的抢焦点问题。
/// </summary>
public static class InteractFocus
{
    static readonly List<IInteractable> _items = new List<IInteractable>();

    /// <summary>当前焦点（供调试 / 需要知道"玩家现在能交互什么"的地方读）。可能为 null。</summary>
    public static IInteractable Current { get; private set; }

    public static void Register(IInteractable it)
    {
        if (it == null || _items.Contains(it)) return;
        _items.Add(it);
    }

    public static void Unregister(IInteractable it)
    {
        _items.Remove(it);
        if (ReferenceEquals(Current, it)) Current = null;
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）：场景重载后注册表里会留死引用，必须清掉。</summary>
    internal static void ResetStatics()
    {
        _items.Clear();
        Current = null;
    }

    /// <summary>每帧：选焦点 → 显示气泡 → 分发按键。由 `PlayerController.Update` 调用（唯一入口）。</summary>
    public static void Tick(PlayerController player)
    {
        Current = null;
        if (player == null) return;
        if (GameState.GameOver || GameState.Paused) return;   // 暂停 / 死亡时不提示、也不响应按键

        // ⚠️ 帧号互斥：同一帧里 F 已被别处消费过（典型是"刚按下 F 关掉了箱子 / 商店"）→ 本次不响应。
        //    否则会出现"关掉的**同一帧**又被焦点重新打开"。见 `GameState.InteractConsumedFrame`。
        if (GameState.InteractConsumedFrame == Time.frameCount) return;

        Vector3 p = player.transform.position;
        IInteractable best = null;
        float bestSq = float.MaxValue;

        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];

            // ⚠️ 接口引用**不能**直接用 `== null` 判断"Unity 对象是否已销毁"：
            //    那走的是普通引用比较，被销毁的 MonoBehaviour 引用并不为 null（经典的静默失效坑）。
            //    必须先转成 UnityEngine.Object，才能用它的重载比较。
            if (it as UnityEngine.Object == null) { _items.RemoveAt(i); i--; continue; }

            string label = it.InteractLabel;
            if (string.IsNullOrEmpty(label)) continue;        // 空箱子勾了"不提示"→ 本次不参与

            var anchor = it.InteractAnchor;
            if (anchor == null) continue;

            float dSq = (anchor.position - p).sqrMagnitude;
            float range = Mathf.Max(0.1f, it.InteractRange);
            if (dSq > range * range) continue;
            if (dSq < bestSq) { bestSq = dSq; best = it; }
        }

        Current = best;
        if (best == null) return;

        // 气泡：调用方每帧续期（0.12s 不续期即自动消失）——和以前"底部提示"的语义一致
        HUD.ShowPickupBubble(best.InteractAnchor, best.InteractLabel, best.InteractKey);

        // 按键：只作用于焦点 → 天然不会"一次按键触发两件事"
        if (!WasKeyPressed(best.InteractKey)) return;
        GameState.InteractConsumedFrame = Time.frameCount;   // 标记本帧的 F 已用掉（面板那边据此让位）
        best.Interact(player);
    }

    /// <summary>把气泡里的按键字母映射成真实按键。
    /// ⭐ **当前统一为 F**（拾取 / 开箱 / 开商店一键，2026-09-28 起；E 已让位给魔法释放）——
    ///    历史遗留的 `"E"` 配置也一律按 F 处理，避免旧场景里的物体"提示 E 却按不动"。
    /// 保留"按键 → 真实键"这一层映射结构：将来若某个交互物需要独立键（例如"长按 F 拆解"），
    /// 在这里加一支分支即可，调用方（<see cref="Tick"/>）不用改。</summary>
    static bool WasKeyPressed(string key)
    {
        return Input.GetKeyDown(KeyCode.F);
    }
}
