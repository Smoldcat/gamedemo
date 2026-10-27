using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 物品格子的**拖拽源**（背包库存 / 快捷武器槽 / 箱子格）。
/// 真正的移动规则全在 <see cref="ItemMoveService"/>；本类只负责：
///   · 按下后交给 UGUI 判断是否开始拖（8px 阈值）
///   · 显示一个跟随光标（虚拟光标）的半透明物品图标
///   · 拖拽期间让源格子不挡射线（否则 drop 永远打到自己）
///   · 通知两个面板高亮"可放置"的格子
///
/// ⚠️ **不能在 `OnDrop` 里直接 Move**：Move 会刷新面板、把源格子 Destroy 掉，
///    而 `OnEndDrag` 还没执行 → 图标 / 高亮 / raycast 状态可能残留。
///    所以 `ItemDropTarget` 只把目标记到 <see cref="PendingDrop"/>，统一在 `OnEndDrag` 里执行。
/// </summary>
public class ItemDragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    /// <summary>本格子指向的容器 + 下标（面板每次 Refresh 时重设）。</summary>
    public ItemRef source;

    /// <summary>当前是否正在拖拽（两个面板的 tooltip 会据此隐藏）。</summary>
    public static bool IsDragging { get; private set; }

    /// <summary>正在拖拽的源格子。</summary>
    public static ItemRef DraggingSource { get; private set; }

    /// <summary>松手时命中的目标格（由 <see cref="ItemDropTarget"/> 在 OnDrop 里写入）。</summary>
    public static ItemRef? PendingDrop;

    static Canvas _layer;
    static Image _icon;
    static CanvasGroup _sourceGroup;
    static bool _thresholdApplied;

    void OnEnable()
    {
        // 用户 2026-10-26 定：移动超过 8px 才算拖拽（UGUI 默认 10；全局设一次即可）
        if (_thresholdApplied) return;
        if (EventSystem.current == null) return;
        _thresholdApplied = true;
        EventSystem.current.pixelDragThreshold = 8;
    }

    public void OnBeginDrag(PointerEventData e)
    {
        ItemContextMenu.Close();   // 拖拽开始 → 收起右键菜单（2026-10-27）
        if (!ItemMoveService.CanDrag(source)) return;

        IsDragging = true;
        DraggingSource = source;
        PendingDrop = null;

        EnsureLayer();
        var stack = ItemMoveService.Peek(source);
        if (_icon != null && stack != null && stack.def != null)
        {
            _icon.sprite = ItemVisuals.IconOf(stack.def);
            var c = ItemVisuals.ColorOf(stack.def);
            c.a = 0.85f;
            _icon.color = c;
            _icon.gameObject.SetActive(true);
            UpdateIcon(e.position);
        }

        // 源格子不挡射线：drop 才能命中它下面的目标格
        _sourceGroup = GetComponent<CanvasGroup>();
        if (_sourceGroup == null) _sourceGroup = gameObject.AddComponent<CanvasGroup>();
        _sourceGroup.blocksRaycasts = false;

        if (BackpackPanel.Instance != null) BackpackPanel.Instance.SetDragHighlight(source);
        if (ChestPanel.Instance != null) ChestPanel.Instance.SetDragHighlight(source);
    }

    public void OnDrag(PointerEventData e)
    {
        if (!IsDragging) return;
        UpdateIcon(e.position);
    }

    public void OnEndDrag(PointerEventData e)
    {
        if (!IsDragging) return;
        EndDragVisual();

        // 命中目标才移动；拖到面板外 = 什么都不做（取消）
        var to = PendingDrop;
        PendingDrop = null;
        if (to.HasValue) ItemMoveService.Move(DraggingSource, to.Value);

        // 补上拖拽期间被延迟的面板刷新（搜刮揭示 / 其他数据变化）
        if (BackpackPanel.Instance != null) BackpackPanel.Instance.FlushPendingRefresh();
        if (ChestPanel.Instance != null) ChestPanel.Instance.FlushPendingRefresh();
    }

    void OnDisable()
    {
        // 兜底：源格子被销毁（例如别的路径重建面板）时，UGUI 不会调用 OnEndDrag → 这里必须收掉图标
        if (!IsDragging) return;
        if (DraggingSource.container != source.container || DraggingSource.index != source.index) return;
        EndDragVisual();
        if (BackpackPanel.Instance != null) BackpackPanel.Instance.FlushPendingRefresh();
        if (ChestPanel.Instance != null) ChestPanel.Instance.FlushPendingRefresh();
    }

    /// <summary>收掉拖拽的"视觉与状态"（图标 / 源格射线 / 高亮）；不负责移动数据。</summary>
    void EndDragVisual()
    {
        IsDragging = false;

        if (_icon != null) _icon.gameObject.SetActive(false);
        if (_sourceGroup != null) { _sourceGroup.blocksRaycasts = true; _sourceGroup = null; }

        if (BackpackPanel.Instance != null) BackpackPanel.Instance.ClearDragHighlight();
        if (ChestPanel.Instance != null) ChestPanel.Instance.ClearDragHighlight();
    }

    static void UpdateIcon(Vector2 screenPos)
    {
        if (_layer == null || _icon == null) return;
        var canvasRt = _layer.transform as RectTransform;
        if (canvasRt != null &&
            RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, screenPos, null, out var local))
            _icon.rectTransform.anchoredPosition = local;
    }

    static void EnsureLayer()
    {
        if (_layer != null && _icon != null) return;

        // 350 = 盖住背包 / 箱子（200），但低于虚拟光标（400）
        _layer = UIUtil.NewOverlayCanvas("ItemDragLayer", 350);

        var go = new GameObject("Icon");
        go.transform.SetParent(_layer.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(64f, 64f);
        _icon = go.AddComponent<Image>();
        _icon.raycastTarget = false;      // 图标不能挡射线（否则 drop 永远打到图标）
        _icon.preserveAspect = true;
        go.SetActive(false);
    }

    /// <summary>静态清理（供 <see cref="StaticReset"/> 调用；Canvas 会随场景销毁，这里只清引用）。</summary>
    internal static void ResetStatics()
    {
        _layer = null;
        _icon = null;
        _sourceGroup = null;
        _thresholdApplied = false;
        IsDragging = false;
        PendingDrop = null;
    }
}
