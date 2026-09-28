using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 背包**库存格**的点击分发器：把"右键点了这一格"转回 <see cref="BackpackPanel"/> 处理（用于丢弃）。
///
/// 为什么需要这个独立组件（而不是在 <see cref="BackpackPanel"/> 里直接处理）：
///   UGUI 的 `IPointerClickHandler` 必须由**格子自己挂的组件**实现，
///   而格子是每次 `Refresh` 重建的（`BackpackPanel` 挂在整个面板的根节点上，不是格子）。
///
/// ⚠️ 为什么走 `EventSystem` 而不是 `Input.GetMouseButtonDown(1)`：
///   背包打开时鼠标被 `VirtualCursor` 接管（系统光标隐藏、由它注入虚拟的 `mousePosition`）——
///   这条链路上**只有 `EventSystem` 知道"光标现在指在哪一格"**。
///   若自己读 `Input`，还得手动做矩形命中判断，反而更容易错（项目里 tooltip 就是手写命中的，
///   那是历史写法，新增交互一律优先用 EventSystem）。
///
/// ⚠️ 左键不归本组件管：库存格的左键 = 装备（武器）/ 使用，由格子上的 `Button` 处理。
/// </summary>
public class BackpackCellClick : MonoBehaviour, IPointerClickHandler
{
    [Tooltip("回传给哪个面板（格子重建时由面板填入）")]
    public BackpackPanel panel;

    [Tooltip("本格在 `backpack.inventory` 里的下标（格子重建时由面板填入）")]
    public int index;

    public void OnPointerClick(PointerEventData e)
    {
        if (e == null) return;
        if (e.button != PointerEventData.InputButton.Right) return;   // 只处理右键
        if (panel != null) panel.RequestDropItem(index);
    }
}
