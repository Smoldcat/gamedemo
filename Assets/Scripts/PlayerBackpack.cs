using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 玩家背包：3 个快捷武器槽 + 库存（**固定 Capacity 格，各类物品共用**）。
///
/// 2026-09-24 起库存改成**通用物品格**（<see cref="ItemStack"/> = 物品定义 + 数量）：
///   · 武器 / 血包 / 以后的子弹，走的是**同一套**"收下 / 移除 / 排序"逻辑 → **加新物品种类不用改本文件**
///   · 叠加规则 = **「该物品允许堆叠」且「身份证相同」**（`ItemData.canStack` + `ItemData.StackId`）
///   · ⚠️ 武器默认**不可堆叠**（一件占一格）—— 用户 2026-09-24 定：将来加配件后
///     "两把同款枪"可能完全不是一回事，叠加会把它们混成一格
///   · ⚠️ 血包也**不可堆叠**（一格一个，有几个一目了然，用户 2026-09-22 定）
///
/// ⚠️ **快捷武器槽（slots）仍然只放武器**（`WeaponData`）：它就是"手上的枪"，按 1/2/3 切；
///    子弹 / 血包放进去没有意义。将来要加"装备槽"（头盔 / 防弹衣…）时，那是**另一组带类型限制的槽**，
///    不是把这里改成通用 —— 所以本文件不需要为那件事提前抽象（改起来很便宜）。
///
/// 纯数据 + 增删逻辑，挂在 Player 物体上；视图（枪模型 / HUD / 面板）由其它组件驱动。
/// 库存去重/叠加按 `StackId` 而不是对象引用 —— 敌人掉落的枪是 `Clone()` 出来的副本，地址和资源不一样。
/// </summary>
public class PlayerBackpack : MonoBehaviour
{
    /// <summary>一个快捷武器槽：武器 + 该槽独立的弹匣余量</summary>
    [System.Serializable]
    public class Slot
    {
        public WeaponData weapon;
        public int magAmmo;
    }

    [Header("快捷武器槽（只放武器）")]
    public Slot[] slots = new Slot[3];
    public int currentSlot;   // 当前使用中的槽位 0..2（-1 = 空手）

    [Header("背包库存（通用物品格）")]
    public List<ItemStack> inventory = new List<ItemStack>();

    [Header("开局自带")]
    [Tooltip("开局自带的血包数量（每局开始由 Awake 放进库存；一个血包占一格）")]
    public int startMedkits = 2;

    void Awake()
    {
        EnsureSlots();
        currentSlot = -1;

        if (startMedkits > 0)
        {
            var medkit = MedkitDef();
            if (medkit != null) AddItems(medkit, startMedkits);
            else Debug.LogWarning("[PlayerBackpack] 找不到「血包定义卡」→ 开局自带血包没有生效。" +
                                  "请检查 Assets/Items/Consumables/Medkit.asset 是否存在，并跑一次菜单 Tools/游戏/重建物品清单。");
        }
    }

    /// <summary>从物品清单里取"血包"这张定义卡（世界里那些只认类型、不认具体哪张卡的地方用它）。</summary>
    public static MedkitData MedkitDef() => ItemDatabase.FirstOfType<MedkitData>();

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

    /// <summary>背包里的血包总数（**派生值**：数一数有几格血包）。
    /// 血包不可堆叠 → 每格恒为 1，所以直接按格计数。</summary>
    public int Medkits
    {
        get
        {
            int n = 0;
            for (int i = 0; i < inventory.Count; i++)
            {
                var s = inventory[i];
                if (s == null || s.def == null) continue;
                if (s.def is MedkitData) n += Mathf.Max(1, s.count);
            }
            return n;
        }
    }

    // ==================== 通用物品（库存） ====================

    /// <summary>能不能再收下任意一种物品（至少 1 个）。规则与 <see cref="AddItems"/> 一致。</summary>
    public bool CanAdd(ItemData def)
    {
        if (def == null) return false;
        EnsureSlots();

        // 可堆叠：已有同类格且没满 → 能
        if (def.canStack)
            for (int i = 0; i < inventory.Count; i++)
            {
                var s = inventory[i];
                if (s == null || s.def == null) continue;
                if (s.def.CanMergeWith(def) && s.count < s.MaxStack) return true;
            }
        // 还有空格 → 能
        return inventory.Count < Capacity;
    }

    /// <summary>
    /// 尽量收下某样物品；返回**真正收下的数量**（0 = 一点都收不下）。
    /// 这是库存的**唯一入口**（武器 / 血包 / 以后的子弹都走它），所以"能不能堆叠"只需要在物品定义卡上配一次。
    /// </summary>
    public int AddItems(ItemData def, int count)
    {
        if (def == null || count <= 0) return 0;
        EnsureSlots();
        int left = count;

        // ① 可堆叠：先塞进已有的同类格（可能跨多格）
        if (def.canStack)
            for (int i = 0; i < inventory.Count && left > 0; i++)
            {
                var s = inventory[i];
                if (s == null || s.def == null) continue;
                if (!s.def.CanMergeWith(def)) continue;
                left = s.Add(left);
            }

        // ② 还有剩 → 一格一格地开新格（不可堆叠的物品每格只收 1 个）
        while (left > 0 && inventory.Count < Capacity)
        {
            var s = new ItemStack();
            s.def = def;
            s.count = 0;
            left = s.Add(left);
            if (s.count > 0) inventory.Add(s);
            else break;   // 兜底：一格都收不下（正常不会发生）
        }

        return count - left;
    }

    /// <summary>从库存第 i 格取走 n 个（商店卖出 / 以后的消耗品用）；返回**实际取走**的数量。
    /// 这一格空了就把格腾出来（与箱子取物 `Chest.TakeFromSlot` 同一套语义）。
    /// ⚠️ 这是库存的"取出"**唯一入口**（与 `AddItems` 对称）—— 别在别处直接改 `inventory`，
    ///    否则"空了要腾格"这类规则会散落在各处。</summary>
    public int TakeFromInventory(int i, int n)
    {
        if (n <= 0 || i < 0 || i >= inventory.Count) return 0;
        var s = inventory[i];
        if (s == null || s.def == null) { inventory.RemoveAt(i); return 0; }

        int take = s.Take(n);
        if (s.IsEmpty) { s.Clear(); inventory.RemoveAt(i); }
        return take;
    }

    /// <summary>能不能再收下这把武器：有空快捷槽 → 能；否则看库存还有没有空格（武器不可堆叠 → 一把占一格）。</summary>
    public bool CanAddWeapon(WeaponData w)
    {
        if (w == null) return false;
        EnsureSlots();
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].weapon == null) return true;
        return inventory.Count < Capacity;
    }

    /// <summary>捡到一把武器：**优先自动装进空快捷槽并成为当前武器**；快捷槽满了则进库存。
    /// <returns>true = 已收下；false = 快捷槽与库存都放不下（背包已满，枪留在原地）</returns></summary>
    public bool AddWeapon(WeaponData w)
    {
        if (w == null) return false;
        EnsureSlots();

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

        return AddItems(w, 1) > 0;   // 快捷槽满 → 进库存（武器不可堆叠 → 新占一格）
    }

    /// <summary>从库存「装备」一把武器：装到空槽，全满则替换当前槽，原武器下放进库存。
    /// `stackId` = 物品身份证（由 `ItemStack.def.StackId` 得到；**不能**用对象引用 —— 敌人掉落的枪是副本）。</summary>
    public bool EquipFromInventory(string stackId, out WeaponData displaced)
    {
        displaced = null;
        EnsureSlots();
        if (string.IsNullOrEmpty(stackId)) return false;

        int idx = -1;
        WeaponData weapon = null;
        for (int i = 0; i < inventory.Count; i++)
        {
            var s = inventory[i];
            if (s == null || s.def == null) continue;
            var w = s.def as WeaponData;
            if (w == null) continue;                 // 血包 / 子弹不是武器 → 不能装到快捷槽
            if (w.StackId != stackId) continue;
            idx = i; weapon = w; break;
        }
        if (idx < 0 || weapon == null) return false;

        int target = -1;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].weapon == null) { target = i; break; }

        if (target >= 0)
        {
            slots[target].weapon = weapon;
            slots[target].magAmmo = weapon.magSize;
        }
        else
        {
            target = currentSlot >= 0 ? currentSlot : 0;
            displaced = slots[target].weapon;
            slots[target].weapon = weapon;
            slots[target].magAmmo = weapon.magSize;
            if (displaced != null) AddItems(displaced, 1);   // 原武器下放进库存（走通用流程）
        }

        var item = inventory[idx];
        item.count -= 1;
        if (item.count <= 0) inventory.RemoveAt(idx);
        currentSlot = target;
        return true;
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

    /// <summary>使用一个血包（未满血才可，由调用方判断）。
    /// 成功 → 用掉一格并返回它的**定义卡**（调用方从卡里读回血量，不再写死 50）；没有血包返回 null。</summary>
    public MedkitData UseMedkit()
    {
        for (int i = 0; i < inventory.Count; i++)
        {
            var s = inventory[i];
            if (s == null || s.def == null) continue;
            var m = s.def as MedkitData;
            if (m == null) continue;

            s.count -= 1;
            if (s.count <= 0) inventory.RemoveAt(i);   // 一格一个：用掉就把这一格腾出来
            return m;
        }
        return null;
    }
}
