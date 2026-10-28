using System.Collections.Generic;
using UnityEngine;

/// <summary>右键菜单里的一项操作。</summary>
public struct ItemAction
{
    public string label;
    public Color color;
    public bool enabled;
    public System.Action run;
}

/// <summary>
/// 物品右键操作的**唯一实现**（背包 / 快捷槽 / 箱子共用）。
/// 菜单只负责把按钮画出来并调用 `run()`，所有规则收敛在这里。
///
/// 按钮（2026-10-27 用户定）：
///   背包：使用 / 装备 / 卸载子弹 / 拆分 / 丢弃
///   箱子：拿取 / 使用 / 装备 / 卸载子弹 / 拆分（没有丢弃）
///   · 使用 = 血包（满血置灰）；以后扩展其他可用物品
///   · 装备 = 武器/魔法；快捷武器槽 = 切换手持
///   · 卸载子弹 = 退不下的留在弹匣（不提示）
///   · 拆分 = 可堆叠且 ≥2
/// </summary>
public static class ItemActions
{
    static readonly Color Green = new Color(0.55f, 0.9f, 0.6f);
    static readonly Color Orange = new Color(1f, 0.72f, 0.38f);
    static readonly Color Blue = new Color(0.55f, 0.75f, 1f);
    static readonly Color Red = new Color(1f, 0.52f, 0.46f);

    public static List<ItemAction> Build(ItemRef r)
    {
        var list = new List<ItemAction>();
        var stack = ItemMoveService.Peek(r);
        if (stack == null || stack.def == null || stack.IsEmpty) return list;

        var pc = PlayerController.Instance;
        var bp = pc != null ? pc.backpack : null;
        bool inChest = r.container == ItemContainer.ChestSlot;
        bool inWeaponSlot = r.container == ItemContainer.BackpackSlot;
        bool inMagicSlot = r.container == ItemContainer.BackpackMagic;
        var def = stack.def;

        // ---- 拿取（箱子 → 背包）----
        if (inChest)
        {
            bool can = bp != null && bp.FirstFreeInventorySlot() >= 0;
            list.Add(new ItemAction { label = "拿取", color = Green, enabled = can, run = () => Take(r) });
        }

        // ---- 使用（血包；以后扩展其他可用物品）----
        if (def is MedkitData)
        {
            bool full = IsFullHp(pc);
            list.Add(new ItemAction { label = "使用", color = Green, enabled = !full, run = () => Use(r) });
        }

        // ---- 装备（武器 / 魔法）----
        if (def is WeaponData)
        {
            list.Add(new ItemAction
            {
                label = "装备",
                color = Green,
                enabled = bp != null,
                run = () => Equip(r),
            });
        }
        else if (def is MagicData)
        {
            if (!inMagicSlot)   // 已经在魔法槽里就不显示"装备"
                list.Add(new ItemAction { label = "装备", color = Green, enabled = bp != null, run = () => Equip(r) });
        }

        // ---- 卸载子弹（武器 + 有子弹）----
        var w = def as WeaponData;
        if (w != null && w.ammoKind != AmmoKind.None && ReadMag(r, w, bp) > 0)
            list.Add(new ItemAction { label = "卸载子弹", color = Orange, enabled = true, run = () => UnequipAmmo(r) });

        // ---- 拆分（可堆叠且 ≥2；只能拆进同一容器的空位）----
        if (def.canStack && stack.count >= 2 && !inWeaponSlot && !inMagicSlot)
            list.Add(new ItemAction
            {
                label = "拆分",
                color = Blue,
                enabled = HasFreeSlotFor(r, bp),
                run = () => SplitDialog.Open(r),
            });

        // ---- 丢弃（背包；箱子没有）----
        if (!inChest)
            list.Add(new ItemAction { label = "丢弃", color = Red, enabled = true, run = () => Drop(r) });

        return list;
    }

    static int FindEmptyWeaponSlot(PlayerBackpack bp)
    {
        for (int i = 0; i < bp.slots.Length; i++)
            if (bp.slots[i] != null && bp.slots[i].weapon == null) return i;
        return -1;
    }

    // ==================== 动作实现 ====================

    static void Take(ItemRef r)
    {
        // 优先合并进同款未满格，其次放空位（A-10；Move 内部会刷新面板）
        if (!ItemMoveService.TakeIntoBackpack(r)) HUD.AnnounceBackpackFull();
    }

    static void Use(ItemRef r)
    {
        var pc = PlayerController.Instance;
        if (pc == null) return;
        if (r.container == ItemContainer.ChestSlot) pc.UseMedkitFromChest(r.chest, r.index);
        else pc.TryUseMedkitAt(r.index);
        RefreshPanels();
    }

    static void Equip(ItemRef r)
    {
        var pc = PlayerController.Instance;
        var bp = pc != null ? pc.backpack : null;
        if (pc == null || bp == null) return;

        var stack = ItemMoveService.Peek(r);
        if (stack == null) return;

        if (r.container == ItemContainer.BackpackSlot)
        {
            pc.SwitchSlot(r.index);   // 快捷槽 = 切换手持
            RefreshPanels();          // 高亮/当前槽要跟着变
            return;
        }
        if (r.container == ItemContainer.ChestSlot) { EquipFromChest(pc, bp, r, stack); return; }

        // 库存武器 / 魔法：按**引用索引**装（B-6：`StackId` 只会匹配第一把同款，多把同款时会装错）
        if (stack.def is WeaponData)
        {
            int target = FindEmptyWeaponSlot(bp);
            if (target < 0) target = bp.currentSlot >= 0 ? bp.currentSlot : 0;
            if (ItemMoveService.Move(r, ItemRef.Slot(target)))
            {
                bp.currentSlot = target;          // 装备并手持
                pc.LoadMirrorFromSlot();
            }
        }
        else if (stack.def is MagicData)
        {
            ItemMoveService.Move(r, ItemRef.MagicSlot(0));   // 目标空 = 装；有魔法 = 互换回库存
        }
        RefreshPanels();   // ⚠️ 不刷新的话要关掉背包再开才看得到（2026-10-27 用户报的 bug）
    }

    /// <summary>从箱子直接装备：装到空槽 / 替换当前槽；被换下的回背包、满则掉地上。</summary>
    static void EquipFromChest(PlayerController pc, PlayerBackpack bp, ItemRef r, ItemStack stack)
    {
        var w = stack.def as WeaponData;
        var m = stack.def as MagicData;
        if (w == null && m == null) return;

        if (w != null)
        {
            int target = FindEmptySlotOrCurrent(bp);
            if (target < 0) return;

            var old = bp.SlotToStack(target);
            r.chest.SetSlot(r.index, null);          // 从箱子取走
            bp.SetSlotFromStack(target, stack);      // 装到槽（保留弹匣）
            bp.currentSlot = target;                 // 装备后手持
            pc.LoadMirrorFromSlot();

            if (old != null && old.def is WeaponData oldW)
            {
                if (bp.AddWeaponItem(oldW, old.magAmmo, old.ammoId) <= 0)
                    WeaponPickup.Create(oldW, pc.transform.position, old.magAmmo, old.ammoId);
            }
        }
        else
        {
            var oldMagic = bp.CurrentMagic;
            r.chest.SetSlot(r.index, null);
            bp.SetMagicSlotFromStack(0, stack);
            if (oldMagic != null && bp.AddItems(oldMagic, 1) <= 0)
                MagicPickup.Create(oldMagic, pc.transform.position);
        }

        RefreshPanels();
        Sfx.Confirm();
    }

    static void UnequipAmmo(ItemRef r)
    {
        var pc = PlayerController.Instance;
        var bp = pc != null ? pc.backpack : null;
        if (pc == null || bp == null) return;

        var stack = ItemMoveService.Peek(r);
        var w = stack != null ? stack.def as WeaponData : null;
        if (w == null || w.ammoKind == AmmoKind.None) return;

        // 当前手持槽：先把手上的真实余量写回槽
        if (r.container == ItemContainer.BackpackSlot && r.index == bp.currentSlot)
            pc.SaveCurrentSlotAmmo();

        int mag = ReadMag(r, w, bp);
        if (mag <= 0) return;

        var ammo = PlayerBackpack.ResolveDefaultAmmo(w, ReadAmmoId(r, bp));
        if (ammo == null) return;

        int put = bp.AddItems(ammo, mag);   // 背包能放多少放多少
        if (put <= 0) return;

        WriteMag(r, bp, mag - put);        // 退不下的留在弹匣（用户 2026-10-27 定）
        if (r.container == ItemContainer.BackpackSlot && r.index == bp.currentSlot)
            pc.LoadMirrorFromSlot();

        RefreshPanels();
        Sfx.Confirm();
    }

    static void Drop(ItemRef r)
    {
        var pc = PlayerController.Instance;
        if (pc == null) return;
        switch (r.container)
        {
            case ItemContainer.BackpackInventory: pc.DropItemFromInventory(r.index); break;
            case ItemContainer.BackpackSlot: pc.DropSlotAt(r.index); break;
            case ItemContainer.BackpackMagic: pc.DropMagicSlotAt(r.index); break;
            default: return;   // 箱子不显示丢弃
        }
        Sfx.Drop();
        RefreshPanels();   // ⚠️ 丢的是手持枪时，槽位要立刻变空（2026-10-27 用户报的 bug）
    }

    // ==================== 小工具 ====================

    static bool IsFullHp(PlayerController pc)
    {
        if (pc == null) return true;
        var h = pc.GetComponent<Health>();
        return h != null && h.hp >= h.maxHp;
    }

    static bool HasFreeSlotFor(ItemRef r, PlayerBackpack bp)
    {
        if (r.container == ItemContainer.ChestSlot)
        {
            if (r.chest == null) return false;
            for (int i = 0; i < r.chest.SlotCount; i++)
                if (r.chest.CanPlaceAt(i) && r.chest.GetSlot(i) == null) return true;
            return false;
        }
        return bp != null && bp.FirstFreeInventorySlot() >= 0;
    }

    static int FindEmptySlotOrCurrent(PlayerBackpack bp)
    {
        for (int i = 0; i < bp.slots.Length; i++)
            if (bp.slots[i].weapon == null) return i;
        return bp.currentSlot >= 0 ? bp.currentSlot : 0;
    }

    static int ReadMag(ItemRef r, WeaponData w, PlayerBackpack bp)
    {
        if (r.container == ItemContainer.BackpackSlot && bp != null && r.index >= 0 && r.index < bp.slots.Length)
            return PlayerBackpack.AmmoOf(w, bp.slots[r.index].magAmmo);
        var s = ItemMoveService.Peek(r);
        return s != null ? PlayerBackpack.AmmoOf(w, s.magAmmo) : 0;
    }

    static AmmoData ReadAmmoId(ItemRef r, PlayerBackpack bp)
    {
        if (r.container == ItemContainer.BackpackSlot && bp != null && r.index >= 0 && r.index < bp.slots.Length)
            return bp.slots[r.index].ammoId;
        var s = ItemMoveService.Peek(r);
        return s != null ? s.ammoId : null;
    }

    static void WriteMag(ItemRef r, PlayerBackpack bp, int left)
    {
        if (r.container == ItemContainer.BackpackSlot && bp != null && r.index >= 0 && r.index < bp.slots.Length)
        {
            bp.slots[r.index].magAmmo = left;
            return;
        }
        if (r.container == ItemContainer.BackpackInventory && bp != null)
        {
            var s = bp.InventoryAt(r.index);
            if (s != null) s.magAmmo = left;
            return;
        }
        if (r.container == ItemContainer.ChestSlot && r.chest != null)
        {
            var s = r.chest.GetSlot(r.index);
            if (s != null) s.magAmmo = left;
        }
    }

    static void RefreshPanels()
    {
        if (BackpackPanel.Instance != null) BackpackPanel.Instance.RefreshIfOpen();
        if (ChestPanel.Instance != null) ChestPanel.Instance.RefreshIfOpen();
    }
}
