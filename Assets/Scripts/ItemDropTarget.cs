using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 物品格子的**放置目标**（背包库存 / 快捷武器槽 / 箱子格）。
/// 只做一件事：拖拽松手时把"目标是谁"记到 <see cref="ItemDragHandler.PendingDrop"/>，
/// 真正的移动在 `ItemDragHandler.OnEndDrag` 里统一执行（避免 Move 刷新面板把源格子销毁后状态残留）。
/// </summary>
public class ItemDropTarget : MonoBehaviour, IDropHandler
{
    /// <summary>本格子指向的容器 + 下标（面板每次 Refresh 时重设）。</summary>
    public ItemRef target;

    public void OnDrop(PointerEventData eventData)
    {
        if (!ItemDragHandler.IsDragging) return;
        ItemDragHandler.PendingDrop = target;
    }
}
