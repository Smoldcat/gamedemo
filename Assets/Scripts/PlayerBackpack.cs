using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 玩家背包（方案 B）：3 个快捷武器槽 + 背包库存（**武器与血包共用这 Capacity 格**）。
/// 叠加规则：同类武器在一格里叠加计数；**血包不叠加，一个血包独占一格**（有几个一目了然）。
/// 纯数据 + 增删逻辑，挂载在 Player 物体上；视图名（手上枪模型、HUD）由 PlayerController 驱动。
/// 命中规则里用在刀刃上：敌人掉落的枪是 Clone 副本（不同引用实例），所以库存去重按 weaponName 而不是引用。
/// </summary>
public class PlayerBackpack : MonoBehaviour
{
    /// <summary>背包物品类型：武器 / 血包（两者都是**占格物品**；血包一格一个，不叠加）</summary>
    public enum ItemKind { Weapon, Medkit }

    /// <summary>血包在库存里的显示名（背包格子 / 排序都用它）</summary>
    public const string MedkitItemName = "血包";

    /// <summary>一个快捷武器槽：武器 + 该槽独立的弹匣余量</summary>
    [System.Serializable]
    public class Slot
    {
        public WeaponData weapon;
        public int magAmmo;
    }

    /// <summary>背包库存里的一格物品：武器（带 WeaponData）或血包（消耗品）。
    /// 武器同类叠加计数（count = 叠了几把）；**血包不叠加，count 恒为 1**（有几个血包就是几格）。</summary>
    [System.Serializable]
    public class InvItem
    {
        public ItemKind kind = ItemKind.Weapon;
        public string weaponName;   // 武器名；血包格为 null
        public WeaponData weapon;   // 武器数据；血包格为 null（用到它的地方一律先看 IsMedkit）
        public int count;           // 武器 = 叠了几把；血包恒为 1

        /// <summary>是不是血包格</summary>
        public bool IsMedkit => kind == ItemKind.Medkit;

        /// <summary>显示名（血包格 = "血包"，武器格 = 武器名）</summary>
        public string DisplayName => IsMedkit ? MedkitItemName : weaponName;
    }

    [Header("快捷武器槽")]
    public Slot[] slots = new Slot[3];
    public int currentSlot;   // 当前使用中的槽位 0..2（-1 表示空手）

    [Header("背包库存（武器与血包共用这些格子）")]
    public List<InvItem> inventory = new List<InvItem>();

    [Header("开局自带")]
    [Tooltip("开局自带的血包数量（每局开始由 Awake 放进库存；一个血包占一格）")]
    public int startMedkits = 2;

    void Awake()
    {
        EnsureSlots();
        currentSlot = -1;
        if (startMedkits > 0) AddMedkit(startMedkits);   // 开局自带血包（一个占一格）
    }

    /// <summary>确保快捷槽数组元素已创建（编辑器里新挂组件时字段为 null；也防 AddComponent 顺序导致 Awake 前被调用）</summary>
    void EnsureSlots()
    {
        if (slots == null) slots = new Slot[3];
        if (slots.Length != 3) System.Array.Resize(ref slots, 3);
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] == null) slots[i] = new Slot();
    }

    /// <summary>是否有空快捷槽（拾取提示判断「进槽 or 进库存」用）</summary>
    public bool HasEmptySlot()
    {
        EnsureSlots();
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].weapon == null) return true;
        return false;
    }

    /// <summary>库存最多能放几「格」（同类物品在同一格里叠加 → 等于「最多能装几种物品」）。
    /// 读 `GameAssets.backpackInventorySlots`（默认 20）；资源表缺失或配 ≤0 时回落 20。</summary>
    public int Capacity
    {
        get
        {
            var a = GameAssets.I;
            int cap = a != null ? a.backpackInventorySlots : 20;
            return cap > 0 ? cap : 20;
        }
    }

    /// <summary>背包里的血包总数（**派生值**：血包是占格物品，不再单独存计数）。
    /// 一个血包独占一格 → 数一数有几格血包就是几个。</summary>
    public int Medkits
    {
        get
        {
            int n = 0;
            for (int i = 0; i < inventory.Count; i++)
                if (inventory[i] != null && inventory[i].kind == ItemKind.Medkit) n++;
            return n;
        }
    }

    /// <summary>能不能再收下这把武器（拾取前判断用，不实际加入）。规则与 <see cref="AddWeapon"/> 完全一致：
    /// 有空快捷槽 → 能；库存已有同类 → 能（叠加）；库存还有空格 → 能；否则不能（背包已满）。</summary>
    public bool CanAddWeapon(WeaponData w)
    {
        if (w == null) return false;
        EnsureSlots();
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].weapon == null) return true;
        for (int i = 0; i < inventory.Count; i++)
            if (!inventory[i].IsMedkit && inventory[i].weaponName == w.weaponName) return true;
        return inventory.Count < Capacity;
    }

    /// <summary>捡到一把武器：优先自动装进空快捷槽并成为当前武器；满了则进库存（同类叠加、一格一种）。</summary>
    /// <returns>true = 已收下；false = 快捷槽与库存都放不下（背包已满，枪留在原地）</returns>
    public bool AddWeapon(WeaponData w)
    {
        if (w == null) return false;
        EnsureSlots();

        // 优先自动装备：找一个空快捷槽,并设为当前槽
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].weapon == null)
            {
                slots[i].weapon = w;
                slots[i].magAmmo = w.magSize;
                currentSlot = i;
                return true;
            }
        }

        // 快捷槽已满 → 进库存（按名字叠加；血包格不参与武器去重）
        for (int i = 0; i < inventory.Count; i++)
            if (!inventory[i].IsMedkit && inventory[i].weaponName == w.weaponName)
            {
                inventory[i].count += 1;
                return true;
            }

        // 库存还有空格 → 新占一格（血包格也占格，所以这里跟血包抢格子）
        if (inventory.Count < Capacity)
        {
            inventory.Add(new InvItem { kind = ItemKind.Weapon, weaponName = w.weaponName, weapon = w, count = 1 });
            return true;
        }

        // 快捷槽满 + 库存满且无同类 → 收不下（背包已满）
        return false;
    }

    /// <summary>从背包库存「装备」一把武器：装到指定的空槽，若没空槽则替换当前槽，原武器下放进库存。</summary>
    public bool EquipFromInventory(string weaponName, out WeaponData displaced)
    {
        displaced = null;
        EnsureSlots();
        // 找到了库存项（血包格不是武器，不参与）
        int idx = -1;
        for (int i = 0; i < inventory.Count; i++)
            if (!inventory[i].IsMedkit && inventory[i].weaponName == weaponName) { idx = i; break; }
        if (idx < 0) return false;

        var item = inventory[idx];
        // 优先空槽
        int target = -1;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].weapon == null) { target = i; break; }
        if (target >= 0)
        {
            slots[target].weapon = item.weapon;
            slots[target].magAmmo = item.weapon.magSize;
        }
        else
        {
            // 全满 → 替换当前槽,原武器进库存
            target = currentSlot >= 0 ? currentSlot : 0;
            displaced = slots[target].weapon;
            slots[target].weapon = item.weapon;
            slots[target].magAmmo = item.weapon.magSize;
            AddToInventory(displaced);
        }

        item.count -= 1;
        if (item.count <= 0) inventory.RemoveAt(idx);
        currentSlot = target;
        return true;
    }

    void AddToInventory(WeaponData w)
    {
        if (w == null) return;
        for (int i = 0; i < inventory.Count; i++)
            if (!inventory[i].IsMedkit && inventory[i].weaponName == w.weaponName) { inventory[i].count += 1; return; }
        inventory.Add(new InvItem { kind = ItemKind.Weapon, weaponName = w.weaponName, weapon = w, count = 1 });
    }

    /// <summary>从快捷槽丢弃一把武器（掉回地面后由调用方创建 WeaponPickup）。返回被丢弃的数据，空槽返回 null。</summary>
    public WeaponData DropSlot(int i)
    {
        EnsureSlots();
        if (i < 0 || i >= slots.Length) return null;
        var w = slots[i].weapon;
        if (w == null) return null;
        slots[i].weapon = null;
        slots[i].magAmmo = 0;
        if (currentSlot == i) currentSlot = -1;   // 手上武器被丢掉 → 空手
        return w;
    }

    /// <summary>整理库存：按显示名排序（背包面板右上角「整理」按钮调用）。</summary>
    public void SortInventory()
    {
        inventory.Sort((a, b) =>
        {
            if (a == null || b == null) return 0;
            return string.CompareOrdinal(a.DisplayName, b.DisplayName);
        });
    }

    /// <summary>使用一个血包（未满血才可,由调用方判断）,成功则移除那一格并返回 true（用掉一格、空出一格）。</summary>
    public bool UseMedkit()
    {
        for (int i = 0; i < inventory.Count; i++)
            if (inventory[i] != null && inventory[i].kind == ItemKind.Medkit)
            {
                inventory.RemoveAt(i);   // 一个血包占一格：用掉就把这一格腾出来
                return true;
            }
        return false;
    }

    /// <summary>把血包收进背包（血包拾取调用）：**一个血包占一格**（不叠加），有几个血包就占几格。
    /// 返回 false = 一个都没装下（背包已满）—— 调用方应让药箱留在原地并提示。
    /// n 大于剩余空格时只装得下几个算几个（拾取时 n = 1，不会出现这种情况）。</summary>
    public bool AddMedkit(int n = 1)
    {
        if (n <= 0) return true;
        int room = Mathf.Max(Capacity - inventory.Count, 0);   // 还剩几个空格
        int can = Mathf.Min(n, room);                          // 一格装一个血包
        for (int i = 0; i < can; i++)
            inventory.Add(new InvItem { kind = ItemKind.Medkit, count = 1 });   // weaponName/weapon 均为 null
        return can > 0;
    }
}
