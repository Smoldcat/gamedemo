using UnityEngine;

/// <summary>物品所在的容器类型（拖拽用）。</summary>
public enum ItemContainer
{
    BackpackInventory,   // 背包库存格
    BackpackSlot,        // 背包快捷武器槽
    ChestSlot,           // 箱子格子
}

/// <summary>一个物品格子的引用（容器 + 下标；箱子还要带实例）。</summary>
public struct ItemRef
{
    public ItemContainer container;
    public int index;
    public Chest chest;   // 仅 ChestSlot 使用

    public static ItemRef Inventory(int i) => new ItemRef { container = ItemContainer.BackpackInventory, index = i };
    public static ItemRef Slot(int i) => new ItemRef { container = ItemContainer.BackpackSlot, index = i };
    public static ItemRef ChestSlot(Chest c, int i) => new ItemRef { container = ItemContainer.ChestSlot, index = i, chest = c };
}

/// <summary>
/// 物品拖拽的**数据层唯一实现**：移动 / 互换 / 合并 / 校验。
/// UI 只负责"拖到哪个格子"，所有规则收敛在这里（背包 / 箱子两个面板不要各写一套）。
///
/// 规则（用户 2026-10-26 定）：
///   · 背包库存 ↔ 库存：空位放入 / 有物品互换 / 同款可堆叠合并
///   · 快捷武器槽 ↔ 快捷槽 / 库存：空位放入 / 有物品互换（只接受武器）
///   · 箱子格 ↔ 箱子格：空位放入 / 有物品互换；**未揭示的格子不能放、也不能拖走**
///   · 背包 ↔ 箱子：目标格有物品 → 拒绝（跨容器互换容易歧义）
///   · 拖到面板外 → 取消（不会丢东西）
///
/// ⚠️ **弹匣随枪走**：`ItemStack` / `Slot` 都带 `magAmmo` + `ammoId`，移动时一起搬运；
///    拖当前手持枪之前还要先 `SaveCurrentSlotAmmo()`（镜像里的余量写回槽）。
/// </summary>
public static class ItemMoveService
{
    static PlayerBackpack Backpack => PlayerController.Instance != null ? PlayerController.Instance.backpack : null;

    /// <summary>这个格子能不能被拖（有物品 / 箱子已揭示）。</summary>
    public static bool CanDrag(ItemRef r)
    {
        var s = Peek(r);
        return s != null && !s.IsEmpty && s.def != null;
    }

    /// <summary>读格子里的物品（库存 / 箱子返回**引用**；快捷槽返回副本；无效/空 → null）。</summary>
    public static ItemStack Peek(ItemRef r)
    {
        var bp = Backpack;
        switch (r.container)
        {
            case ItemContainer.BackpackInventory:
                return bp != null ? bp.InventoryAt(r.index) : null;
            case ItemContainer.BackpackSlot:
                return bp != null ? bp.SlotToStack(r.index) : null;
            case ItemContainer.ChestSlot:
                if (r.chest == null || !r.chest.CanPlaceAt(r.index)) return null;   // 未揭示 → 不能拖
                return r.chest.GetSlot(r.index);
            default:
                return null;
        }
    }

    /// <summary>目标格能不能接受源物品（只校验，不改数据）。</summary>
    public static bool CanDrop(ItemRef from, ItemRef to)
    {
        var src = Peek(from);
        if (src == null || src.IsEmpty) return false;
        if (from.container == to.container && from.index == to.index) return false;   // 拖回自己

        var bp = Backpack;
        switch (to.container)
        {
            case ItemContainer.BackpackInventory:
                if (bp == null || to.index < 0 || to.index >= bp.Capacity) return false;
                break;
            case ItemContainer.BackpackSlot:
                if (bp == null || to.index < 0 || to.index >= bp.slots.Length) return false;
                if (!PlayerBackpack.CanEquipInSlot(src.def)) return false;   // 只有武器能进快捷槽
                break;
            case ItemContainer.ChestSlot:
                if (to.chest == null || !to.chest.CanPlaceAt(to.index)) return false;   // 未揭示不能放
                break;
            default:
                return false;
        }

        var dst = GetAt(to);
        bool dstEmpty = dst == null || dst.IsEmpty;

        // 跨容器（背包 ↔ 箱子）：目标有物品 → 拒绝
        if (IsBackpack(from.container) != IsBackpack(to.container) && !dstEmpty) return false;

        // ⚠️ 互换时**双向**都要能住进对方的容器：快捷槽只收武器 → 快捷槽里的枪不能和库存里的血包 / 子弹互换。
        //    否则写回槽会失败（返回 false 没人看），而库存那格已经被枪覆盖 → 血包凭空消失。
        if (!dstEmpty)
        {
            if (from.container == ItemContainer.BackpackSlot
                && (dst.def == null || !PlayerBackpack.CanEquipInSlot(dst.def))) return false;
            if (to.container == ItemContainer.BackpackSlot
                && (src.def == null || !PlayerBackpack.CanEquipInSlot(src.def))) return false;
        }

        return true;   // 空格 / 同款合并 / 互换 都在 Move 里按实际情况处理
    }

    /// <summary>执行一次拖拽（返回是否真的移动了）。</summary>
    public static bool Move(ItemRef from, ItemRef to)
    {
        var bp = Backpack;
        if (bp == null) return false;
        if (!CanDrop(from, to)) return false;

        var src = Peek(from);
        if (src == null || src.IsEmpty) return false;

        var pc = PlayerController.Instance;
        bool fromIsCurrent = from.container == ItemContainer.BackpackSlot && from.index == bp.currentSlot;

        // 拖的是当前手持那把 → 先把镜像里的弹匣写回槽（否则拖走的余量是旧的）
        if (fromIsCurrent && pc != null) pc.SaveCurrentSlotAmmo();

        var dst = GetAt(to);
        bool dstEmpty = dst == null || dst.IsEmpty;

        if (dstEmpty)
        {
            SetAt(to, Clone(src));
            SetAt(from, null);
        }
        else if (src.def != null && src.def.canStack && dst.def != null && dst.def.CanMergeWith(src.def))
        {
            // 合并：目标格是引用（库存 / 箱子）→ 就地加；剩下的留在源格
            int left = dst.Add(src.count);
            if (left <= 0) SetAt(from, null);
            else src.count = left;
        }
        else
        {
            // 互换：先把两边都复制出来，避免写一边时覆盖另一边
            var a = Clone(src);
            var b = Clone(dst);
            SetAt(from, b);
            SetAt(to, a);
        }

        // 快捷槽变化 → 同步镜像；当前手持被拖空 → 自动切下一个有武器的槽（都空则空手）
        bool slotTouched = from.container == ItemContainer.BackpackSlot || to.container == ItemContainer.BackpackSlot;
        if (slotTouched)
        {
            if (from.container == ItemContainer.BackpackSlot
                && bp.slots[from.index].weapon == null
                && (fromIsCurrent || bp.currentSlot == from.index))
            {
                bp.currentSlot = FindNextWeaponSlot(bp, from.index);
            }
            if (pc != null) pc.LoadMirrorFromSlot();
        }

        // 刷新两个面板（背包 / 箱子可能同时开着）
        if (BackpackPanel.Instance != null) BackpackPanel.Instance.RefreshIfOpen();
        if (ChestPanel.Instance != null) ChestPanel.Instance.RefreshIfOpen();

        Sfx.Confirm();
        return true;
    }

    static bool IsBackpack(ItemContainer c) => c == ItemContainer.BackpackInventory || c == ItemContainer.BackpackSlot;

    static ItemStack GetAt(ItemRef r)
    {
        switch (r.container)
        {
            case ItemContainer.BackpackInventory: return Backpack != null ? Backpack.InventoryAt(r.index) : null;
            case ItemContainer.BackpackSlot: return Backpack != null ? Backpack.SlotToStack(r.index) : null;
            case ItemContainer.ChestSlot: return r.chest != null ? r.chest.GetSlot(r.index) : null;
            default: return null;
        }
    }

    static void SetAt(ItemRef r, ItemStack s)
    {
        var bp = Backpack;
        switch (r.container)
        {
            case ItemContainer.BackpackInventory: bp?.SetInventoryAt(r.index, s); break;
            case ItemContainer.BackpackSlot: bp?.SetSlotFromStack(r.index, s); break;
            case ItemContainer.ChestSlot: r.chest?.SetSlot(r.index, s); break;
        }
    }

    static int FindNextWeaponSlot(PlayerBackpack bp, int from)
    {
        // ⚠️ 不包含自己（k 从 1 到 Length-1）：否则"只有这一把枪"时会返回 from（一个已经空了的槽），
        //    而不是 -1（空手）—— 状态不干净，下一次切换会指到空槽。
        for (int k = 1; k < bp.slots.Length; k++)
        {
            int i = (from + k) % bp.slots.Length;
            if (bp.slots[i] != null && bp.slots[i].weapon != null) return i;
        }
        return -1;
    }

    static ItemStack Clone(ItemStack s)
    {
        if (s == null) return null;
        return new ItemStack
        {
            def = s.def,
            count = s.count,
            magAmmo = s.magAmmo,
            ammoId = s.ammoId,
        };
    }
}
