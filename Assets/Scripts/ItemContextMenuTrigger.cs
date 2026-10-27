using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 物品格子的**右键菜单触发器**（背包库存 / 快捷武器槽 / 魔法槽 / 箱子格都挂这个）。
/// 左键不归它管（左键 = 拖拽 / 箱子拿取 / 无操作）。
/// </summary>
public class ItemContextMenuTrigger : MonoBehaviour, IPointerClickHandler
{
    /// <summary>本格子指向的容器 + 下标（面板每次 Refresh 时重设）。</summary>
    public ItemRef target;

    public void OnPointerClick(PointerEventData e)
    {
        if (e == null || e.button != PointerEventData.InputButton.Right) return;   // 只处理右键
        ItemContextMenu.Open(target, e.position);
    }
}
