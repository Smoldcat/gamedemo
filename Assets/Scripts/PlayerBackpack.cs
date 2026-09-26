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

    /// <summary>按口径统计背包里的**总备弹**（跨格子求和）。
    /// **唯一入口**：换弹扣除、HUD 显示都读它 —— 别在别处自己遍历 `inventory` 算弹药。
    /// ⚠️ 将来做「弹药分级」（一级/二级…同口径可互换）时，本方法**自动**把它们加起来（按 `ammoKind` 匹配）；
    ///    但 HUD 到时候要改成**按每张卡分别显示**（用户 2026-09-27 明确），不能继续用这个总和。</summary>
    public int CountAmmo(AmmoKind kind)
    {
        if (kind == AmmoKind.None) return 0;
        int total = 0;
        for (int i = 0; i < inventory.Count; i++)
        {
            var s = inventory[i];
            if (s == null || s.def == null) continue;
            var a = s.def as AmmoData;
            if (a == null || a.ammoKind != kind) continue;
            total += s.count;
        }
        return total;
    }

    /// <summary>按口径扣除备弹（跨格子；返回**实际扣掉**的数量）。
    /// ⚠️ 与 `CountAmmo` 成对 —— 将来做"弹药分级"时，"先扣哪一级"只在这里决定（现在是按库存顺序扣）。</summary>
    public int TakeAmmo(AmmoKind kind, int n)
    {
        if (kind == AmmoKind.None || n <= 0) return 0;
        int left = n;
        // 从后往前扫：`TakeFromInventory` 会 RemoveAt，倒序扫描不会打乱未处理的下标
        for (int i = inventory.Count - 1; i >= 0 && left > 0; i--)
        {
            var s = inventory[i];
            if (s == null || s.def == null) continue;
            var a = s.def as AmmoData;
            if (a == null || a.ammoKind != kind) continue;
            left -= TakeFromInventory(i, left);   // 走唯一"取出"入口（空了自动腾格）
        }
        return n - left;
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

    /// <summary>`ItemStack.magAmmo` 与各处形参 `magAmmo` 的**唯一解析**：**负值 = 满弹**（新枪的统一约定），
    /// 其余夹到 `[0, magSize]`（防手改 YAML 塞脏数据把弹匣撑爆或变负）。
    /// ⚠️ 别在别处再写一遍"-1 算满弹"这个判断 —— 散开了迟早有一处忘改。</summary>
    public static int AmmoOf(WeaponData w, int magAmmo)
    {
        int size = w != null ? Mathf.Max(0, w.magSize) : 0;
        return magAmmo < 0 ? size : Mathf.Clamp(magAmmo, 0, size);
    }

    /// <summary>捡到一把武器：**优先自动装进空快捷槽并成为当前武器**；快捷槽满了则进库存。
    /// `magAmmo` 负值 = 满弹（新枪 / 敌人掉落 / 开局铺的枪都走它）；捡地上用过的枪时传它的真实余量。
    /// <returns>true = 已收下；false = 快捷槽与库存都放不下（背包已满，枪留在原地）</returns></summary>
    public bool AddWeapon(WeaponData w, int magAmmo = -1)
    {
        if (w == null) return false;
        EnsureSlots();

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].weapon == null)
            {
                slots[i].weapon = w;
                slots[i].magAmmo = AmmoOf(w, magAmmo);
                currentSlot = i;
                return true;
            }
        }

        return AddWeaponItem(w, magAmmo) > 0;   // 快捷槽满 → 进库存（武器不可堆叠 → 新占一格）
    }

    /// <summary>把一把武器放进**库存**（连弹匣余量一起写进那一格）—— 库存侧的**唯一入口**，
    /// 替代直接调 `AddItems`。返回收下的数量（0 或 1）；`magAmmo` 负值 = 满弹。</summary>
    public int AddWeaponItem(WeaponData w, int magAmmo = -1)
    {
        if (w == null) return 0;
        int put = AddItems(w, 1);
        if (put <= 0) return 0;

        // 刚加进去的就是末尾那一格（武器不可堆叠 → 每格 1 把，且存的是同一个引用）→ 把弹匣写上去
        for (int i = inventory.Count - 1; i >= 0; i--)
        {
            var s = inventory[i];
            if (s == null || s.def != w) continue;
            s.magAmmo = AmmoOf(w, magAmmo);
            break;
        }
        return put;
    }

    /// <summary>从库存「装备」一把武器：装到空槽；没有空槽则替换当前槽，**被换下的武器下放进库存**。
    /// `displaced` = **只有库存真的放不下时**才非空（调用方负责把它掉到地上）—— 正常情况下为 null，
    /// 因为背包已经把它下放进库存了。⚠️ 调用方**不能**无条件把 `displaced` 掉落（会凭空多一把枪）。
    /// `stackId` = 物品身份证（由 `ItemStack.def.StackId` 得到；**不能**用对象引用 —— 敌人掉落的枪是副本）。</summary>
    public bool EquipFromInventory(string stackId, out WeaponData displaced, out int displacedAmmo)
    {
        displaced = null;
        displacedAmmo = -1;
        EnsureSlots();
        if (string.IsNullOrEmpty(stackId)) return false;

        int idx = -1;
        WeaponData weapon = null;
        int weaponAmmo = -1;                 // 库存那一格记的弹匣余量（-1 = 满弹）
        for (int i = 0; i < inventory.Count; i++)
        {
            var s = inventory[i];
            if (s == null || s.def == null) continue;
            var w = s.def as WeaponData;
            if (w == null) continue;                 // 血包 / 子弹不是武器 → 不能装到快捷槽
            if (w.StackId != stackId) continue;
            idx = i; weapon = w; weaponAmmo = s.magAmmo; break;
        }
        if (idx < 0 || weapon == null) return false;

        // ⚠️ **顺序很重要**：先「从库存取走这把武器」（腾出格子），再决定「被替换下来的那把放哪」——
        //    反过来的话，库存正好满（20/20）时被替换的枪会放不下、**凭空消失**（静默丢装备）。
        var item = inventory[idx];
        item.count -= 1;
        if (item.count <= 0) inventory.RemoveAt(idx);

        int target = -1;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].weapon == null) { target = i; break; }

        if (target < 0)
        {
            // 没有空槽 → 替换当前槽。被换下的枪**下放进库存**（带着它自己的弹匣，刚腾出的那格必然收得下）；
            // 只有真收不下时才用 `displaced` 交给调用方掉到地上。
            // ⚠️ 绝不能"既下放又掉落" —— 那是凭空多出一把枪（用户 2026-09-26 报的 bug）。
            target = currentSlot >= 0 ? currentSlot : 0;
            var old = slots[target].weapon;
            int oldAmmo = slots[target].magAmmo;
            if (old != null && AddWeaponItem(old, oldAmmo) <= 0) { displaced = old; displacedAmmo = oldAmmo; }
        }

        slots[target].weapon = weapon;
        slots[target].magAmmo = AmmoOf(weapon, weaponAmmo);   // ← 用库存里记的余量，不再无条件给满
        currentSlot = target;
        return true;
    }

    /// <summary>从快捷槽丢弃一把武器（掉回地面后由调用方创建 WeaponPickup）。返回被丢弃的数据，空槽返回 null。</summary>
    public WeaponData DropSlot(int i, out int magAmmo)
    {
        magAmmo = -1;
        EnsureSlots();
        if (i < 0 || i >= slots.Length) return null;
        var w = slots[i].weapon;
        if (w == null) return null;
        magAmmo = slots[i].magAmmo;
        slots[i].weapon = null;
        slots[i].magAmmo = -1;
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
