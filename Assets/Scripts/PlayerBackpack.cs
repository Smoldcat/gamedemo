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
    /// <summary>一个**快捷装备槽**：武器 + 该槽独立的弹匣余量 + 弹匣里装的是哪张弹药卡（等级）+ 魔法卡。
    /// ⚠️ 两类槽共用同一个结构，是为了**代码同构**（装备 / 丢弃 / 以后的多槽扩容，写法完全一样），
    ///    而不是"一个槽能同时装枪和魔法" —— 用哪个字段由**槽的归属**决定：
    ///    `slots` 数组只写 <see cref="weapon"/>，`magics` 数组只写 <see cref="magic"/>。</summary>
    [System.Serializable]
    public class Slot
    {
        public WeaponData weapon;
        public int magAmmo;
        /// <summary>弹匣里装的弹药卡（`null` = 未知 → 装填时按"该口径有货的最低等级"自动选）。
        /// 决定了**开火伤害倍率**、**按 T 换级时旧弹退给谁**、**HUD 显示哪一级的备弹**。</summary>
        public AmmoData ammoId;
        /// <summary>魔法槽里装的魔法卡（`slots` 数组不用本字段）。</summary>
        public MagicData magic;
    }

    [Header("快捷武器槽（只放武器）")]
    public Slot[] slots = new Slot[3];
    public int currentSlot;   // 当前使用中的槽位 0..2（-1 = 空手）

    [Header("魔法槽（只放魔法）")]
    [Tooltip("⭐ 做成**数组**是刻意的（用户 2026-09-28 定）：将来会同时装备多个魔法，" +
             "现在只开 1 格（magics[0] = HUD 上的 E 格）—— 以后开多格**只改这里的数字**，代码已全部按数组写。\n" +
             "⚠️ 装备 / 丢弃与武器完全同构：装备 = 从库存移进槽，丢弃 = 从槽掉到地上。")]
    public Slot[] magics = new Slot[1];

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

        // 魔法槽：**不强制长度**（以后加格只改字段默认值就行，不用来这里改数字），
        // 但要保证每个元素都已创建 —— 编辑器里字段为 null 或旧存档缺字段时靠这里兜住。
        if (magics == null) magics = new Slot[1];
        for (int i = 0; i < magics.Length; i++)
            if (magics[i] == null) magics[i] = new Slot();
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

    /// <summary>库存里**有物品**的格数。
    /// ⚠️ 不能用 `inventory.Count` 当"用了几格"：拖拽会把空位留在中间（固定槽位语义），
    ///    拖到第 40 格后 `Count` 会变成 40+，但实际可能只有 2 件东西。</summary>
    public int InventoryItemCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < inventory.Count; i++)
            {
                var s = inventory[i];
                if (s != null && s.def != null && !s.IsEmpty) n++;
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
        return HasFreeInventorySlot();
    }

    /// <summary>库存还有没有空位（含 `null` 空位；拖拽会把格子留在原位，所以不能只看 `Count`）。</summary>
    public bool HasFreeInventorySlot()
    {
        for (int i = 0; i < inventory.Count; i++)
        {
            var s = inventory[i];
            if (s == null || s.def == null || s.IsEmpty) return true;
        }
        return inventory.Count < Capacity;
    }

    /// <summary>第一个可用的库存空位下标；没有则返回 -1（必要时把列表扩到 Capacity）。</summary>
    public int FirstFreeInventorySlot()
    {
        for (int i = 0; i < inventory.Count; i++)
        {
            var s = inventory[i];
            if (s == null || s.def == null || s.IsEmpty) return i;
        }
        if (inventory.Count < Capacity)
        {
            inventory.Add(null);
            return inventory.Count - 1;
        }
        return -1;
    }

    void EnsureInventorySize(int size)
    {
        while (inventory.Count < size) inventory.Add(null);
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

        // ② 还有剩 → 一格一格地开新格（优先用拖拽留下的 null 空位，再追加到末尾）
        while (left > 0)
        {
            int idx = FirstFreeInventorySlot();
            if (idx < 0) break;   // 库存满了
            var s = new ItemStack();
            s.def = def;
            s.count = 0;
            left = s.Add(left);
            if (s.count > 0) inventory[idx] = s;
            else break;   // 兜底：一格都收不下（正常不会发生）
        }

        return count - left;
    }

    /// <summary>从库存第 i 格取走 n 个（商店卖出 / 以后的消耗品用）；返回**实际取走**的数量。
    /// 这一格空了就把格腾出来（**背包是紧凑列表：后面的格子会前移**）。
    /// ⚠️ 箱子的 `Chest.TakeFromSlot` 2026-10-26 起改成**固定槽位、拿走后不前移**（搜刮需要"物品待在原格"）
    ///    —— 两者语义**不同**，别再当成同一套。
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

    // ==================== 按索引读写（拖拽用；2026-10-27） ====================

    /// <summary>库存第 i 格（越界 / 空位 → null）。⚠️ 返回的是**引用**，只读；写入请走 <see cref="SetInventoryAt"/>。</summary>
    public ItemStack InventoryAt(int i) => (i >= 0 && i < inventory.Count) ? inventory[i] : null;

    /// <summary>把库存第 i 格替换成 s（null / 空 = 清空成空位）。
    /// ⚠️ **不 `RemoveAt`**：拖拽要保证"物品待在原格"，索引不能因为别的格子被拿走而整体前移。</summary>
    public void SetInventoryAt(int i, ItemStack s)
    {
        if (i < 0 || i >= Capacity) return;
        EnsureInventorySize(i + 1);
        // 武器进库存也补齐弹匣等级（null = 按口径一级）→ 下游永远不用判空
        if (s != null && !s.IsEmpty && s.def is WeaponData w)
            s.ammoId = ResolveDefaultAmmo(w, s.ammoId);
        inventory[i] = (s != null && !s.IsEmpty) ? s : null;
    }

    /// <summary>把快捷武器槽第 i 格转成 `ItemStack` **副本**（空槽 → null）。弹匣余量 / 等级一起带出。</summary>
    public ItemStack SlotToStack(int i)
    {
        EnsureSlots();
        if (i < 0 || i >= slots.Length) return null;
        var slot = slots[i];
        if (slot == null || slot.weapon == null) return null;
        return new ItemStack
        {
            def = slot.weapon,
            count = 1,
            magAmmo = slot.magAmmo,
            ammoId = slot.ammoId,
        };
    }

    /// <summary>把 `ItemStack` 写回快捷武器槽（只接受武器；s 为空 = 清空该槽）。返回是否成功。</summary>
    public bool SetSlotFromStack(int i, ItemStack s)
    {
        EnsureSlots();
        if (i < 0 || i >= slots.Length) return false;
        if (s == null || s.IsEmpty) { ClearSlot(i); return true; }

        var w = s.def as WeaponData;
        if (w == null) return false;
        slots[i].weapon = w;
        slots[i].magAmmo = AmmoOf(w, s.magAmmo);
        slots[i].ammoId = ResolveDefaultAmmo(w, s.ammoId);
        return true;
    }

    /// <summary>这个物品能不能放进快捷武器槽（只接受武器）。</summary>
    public static bool CanEquipInSlot(ItemData def) => def is WeaponData;

    /// <summary>魔法槽第 i 格转成 `ItemStack` 副本（空 → null）。</summary>
    public ItemStack MagicSlotToStack(int i)
    {
        EnsureSlots();
        if (i < 0 || i >= magics.Length) return null;
        var slot = magics[i];
        if (slot == null || slot.magic == null) return null;
        return new ItemStack { def = slot.magic, count = 1 };
    }

    /// <summary>把 `ItemStack` 写回魔法槽（只接受魔法；空 → 清空该槽）。返回是否成功。</summary>
    public bool SetMagicSlotFromStack(int i, ItemStack s)
    {
        EnsureSlots();
        if (i < 0 || i >= magics.Length) return false;
        if (s == null || s.IsEmpty) { magics[i].magic = null; return true; }
        var m = s.def as MagicData;
        if (m == null) return false;
        magics[i].magic = m;
        return true;
    }

    /// <summary>这个物品能不能放进魔法槽（只接受魔法）。</summary>
    public static bool CanEquipInMagicSlot(ItemData def) => def is MagicData;

    /// <summary>清空一个快捷槽（武器 + 弹匣 + 等级）。</summary>
    public void ClearSlot(int i)
    {
        EnsureSlots();
        if (i < 0 || i >= slots.Length) return;
        slots[i].weapon = null;
        slots[i].magAmmo = -1;
        slots[i].ammoId = null;
    }

    // ⚠️ 2026-09-27 **已删除**按口径的 `CountAmmo(AmmoKind)` / `TakeAmmo(AmmoKind, int)`：
    //    分级系统上线后所有调用点都改走下面**按卡**的那对，它们成了死代码。
    //    为什么是删掉而不是留着 —— 这两个名字更短、直觉上"更对"，下一个人顺手用一次就会把
    //    一级 + 二级加在一起 → "HUD 显示有 90 发却换不上弹" / "换完等级悄悄变了"这类静默 bug。
    //    真需要"这个口径总共还有多少发"（不分等级）时，请显式遍历 `ItemDatabase` 自己求和。
    //    （2026-09-27 独立审计建议。）

    // ==================== 弹药分级（一级 / 二级…同口径可互换；用户 2026-09-27 定） ====================
    //
    // 三个概念别混：
    //   · **口径**（`AmmoKind`）= 哪把枪能用它（武器只认这个，永不用等级做匹配）
    //   · **等级**（`AmmoData.tier`）= 一级 / 二级…（决定切换顺序、展示、军火箱权重）
    //   · **弹匣装的是哪张卡**（`Slot.ammoId` / `ItemStack.ammoId`）= 这一匣子弹的**实例状态**
    //     → 伤害倍率、按 T 换级时旧弹退给谁，全看它
    //
    // ⚠️ 换弹 / HUD / 切换预检都必须用**按卡**的 `CountAmmo(AmmoData)`，**不能**用按口径的总和
    //    （总和会把一级二级加在一起 —— 玩家看到"有 90 发"却换不上，或者换完等级悄悄变了）。

    /// <summary>统计**某一张**弹药卡的数量（跨格求和）。分级系统的关键查询 —— 见上面那段说明。
    /// ⚠️ 实现收敛在通用的 <see cref="CountItems"/>（按卡统计的规则**只有一份**）。</summary>
    public int CountAmmo(AmmoData def) => CountItems(def);

    /// <summary>统计**任意一张物品卡**的数量（跨格求和，按「身份证」`SameKindAs` 匹配）。
    /// 弹药（分级）与投掷物（G 槽数量 = 派生值）等都走它 —— **按卡统计的唯一实现**。</summary>
    public int CountItems(ItemData def)
    {
        if (def == null) return 0;
        int total = 0;
        for (int i = 0; i < inventory.Count; i++)
        {
            var s = inventory[i];
            if (s == null || s.def == null) continue;
            if (!s.def.SameKindAs(def)) continue;
            total += s.count;
        }
        return total;
    }

    /// <summary>扣除**某一张**弹药卡（跨格；返回**实际扣掉**的数量）。与 `CountAmmo(AmmoData)` 成对。
    /// ⚠️ 实现收敛在通用的 <see cref="TakeItems"/>。</summary>
    public int TakeAmmo(AmmoData def, int n) => TakeItems(def, n);

    /// <summary>扣除**任意一张物品卡** N 个（跨格、空了自动腾格；返回**实际扣掉**的数量）——
    /// 与 <see cref="CountItems"/> 成对，是通用的「按卡扣除」**唯一实现**
    /// （投掷物的"扔一颗"也走它；别在别处直接改 `inventory`）。</summary>
    public int TakeItems(ItemData def, int n)
    {
        if (def == null || n <= 0) return 0;
        int left = n;
        for (int i = inventory.Count - 1; i >= 0 && left > 0; i--)
        {
            var s = inventory[i];
            if (s == null || s.def == null) continue;
            if (!s.def.SameKindAs(def)) continue;
            left -= TakeFromInventory(i, left);
        }
        return n - left;
    }

    /// <summary>该口径下、**背包里还有货**的等级最低的那张卡（都没有则 null）。用于"空弹匣该装哪一级"。</summary>
    public AmmoData LowestOwnedAmmo(AmmoKind kind)
    {
        if (kind == AmmoKind.None) return null;
        var db = ItemDatabase.I;
        if (db == null || db.all == null) return null;

        AmmoData best = null;
        for (int i = 0; i < db.all.Count; i++)
        {
            var a = db.all[i] as AmmoData;
            if (a == null || a.ammoKind != kind || a.tier <= 0) continue;
            if (CountAmmo(a) <= 0) continue;
            if (best == null || a.tier < best.tier) best = a;
        }
        return best;
    }

    /// <summary>按 T 换级的**纯模拟预检**（用户 2026-09-27 定的语义：判定基准是"取走新弹之后"的背包状态）。
    /// 例：弹匣 30 发一级、背包正好 30 发二级 → 取走 30 发后那格空了 → 退下的 30 发一级**正好占这个空位**（可换 ✓）；
    ///     若背包是 35 发二级 → 取走 30 后还剩 5 发仍占着那格 → 退下的 30 发一级没地方放（不可换 ✗）。
    /// 为什么不用现成的 `CanAdd`：它看"当前"背包，会把上面第一种情况误判成放不下。
    /// `willLoad` 输出**将要装多少发**（= min(弹匣容量, 目标卡库存)）。</summary>
    public bool CanSwapMag(WeaponData w, AmmoData to, int unloadCount, AmmoData from, out int willLoad)
    {
        willLoad = 0;
        if (w == null || to == null) return false;

        willLoad = Mathf.Min(Mathf.Max(0, w.magSize), CountAmmo(to));
        if (willLoad <= 0) return false;                        // 目标等级一颗都没有 → 不能切
        if (unloadCount <= 0 || from == null) return true;      // 空弹匣 / 等级未知 → 没有退弹要安置
        return CanTakeThenAdd(inventory, to, willLoad, from, unloadCount);
    }

    /// <summary>「在库存**副本**上试算」的纯逻辑（**绝不碰真实数据**）：先从副本里扣掉 `takeCount` 个 `takeDef`
    /// （空了整格移除 = 腾出格子），再看 `giveCount` 个 `giveDef` 放不放得下。
    /// 叠加规则与 `AddItems` / `TakeFromInventory` 完全一致（合并同类格 → 逐格开新格）。</summary>
    bool CanTakeThenAdd(List<ItemStack> src, ItemData takeDef, int takeCount, ItemData giveDef, int giveCount)
    {
        if (src == null || takeDef == null || giveDef == null || giveCount <= 0) return false;

        // ① 复制（浅拷贝每格，深到 count 一级 —— 后面只改副本）
        var inv = new List<ItemStack>(src.Count);
        for (int i = 0; i < src.Count; i++)
        {
            var s = src[i];
            inv.Add(s == null ? null : new ItemStack { def = s.def, count = s.count });
        }

        // ② 扣（倒序扫，空了就移除 = 腾格；与 `TakeAmmo` 同一套规则）
        int left = takeCount;
        for (int i = inv.Count - 1; i >= 0 && left > 0; i--)
        {
            var s = inv[i];
            if (s == null || s.def == null || !s.def.SameKindAs(takeDef)) continue;
            int take = Mathf.Min(left, s.count);
            s.count -= take;
            left -= take;
            if (s.count <= 0) inv.RemoveAt(i);
        }

        // ③ 看"退回来的那批"放不放得下（先并进同类格，再开新格）
        int put = 0;
        if (giveDef.canStack)
            for (int i = 0; i < inv.Count && put < giveCount; i++)
            {
                var s = inv[i];
                if (s == null || s.def == null || !s.def.CanMergeWith(giveDef)) continue;
                put += Mathf.Min(giveCount - put, Mathf.Max(0, s.MaxStack - s.count));
            }
        while (put < giveCount && inv.Count < Capacity) put += Mathf.Max(1, giveDef.MaxStack);
        return put >= giveCount;
    }

    /// <summary>能不能再收下这把武器：有空快捷槽 → 能；否则看库存还有没有空格（武器不可堆叠 → 一把占一格）。</summary>
    public bool CanAddWeapon(WeaponData w)
    {
        if (w == null) return false;
        EnsureSlots();
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].weapon == null) return true;
        return HasFreeInventorySlot();
    }

    /// <summary>`ItemStack.magAmmo` 与各处形参 `magAmmo` 的**唯一解析**：**负值 = 满弹**（新枪的统一约定），
    /// 其余夹到 `[0, magSize]`（防手改 YAML 塞脏数据把弹匣撑爆或变负）。
    /// ⚠️ 别在别处再写一遍"-1 算满弹"这个判断 —— 散开了迟早有一处忘改。</summary>
    public static int AmmoOf(WeaponData w, int magAmmo)
    {
        int size = w != null ? Mathf.Max(0, w.magSize) : 0;
        return magAmmo < 0 ? size : Mathf.Clamp(magAmmo, 0, size);
    }

    /// <summary>武器弹匣等级的**兜底**：`ammoId == null` → 按口径取一级弹药卡（`GameAssets.defaultAmmoByKind`）。
    /// ⚠️ 用户 2026-10-27 定：`ammoId` **永远不为 null**（`None` 口径的 RPG 除外）——
    ///    以后做"随机弹药等级 / 数量"时，所有下游都不用再判空。</summary>
    public static AmmoData ResolveDefaultAmmo(WeaponData w, AmmoData ammoId)
    {
        if (ammoId != null || w == null) return ammoId;
        return GameAssets.I != null ? GameAssets.I.DefaultAmmo(w.ammoKind) : null;
    }

    /// <summary>捡到一把武器：**优先自动装进空快捷槽并成为当前武器**；快捷槽满了则进库存。
    /// `magAmmo` 负值 = 满弹（新枪 / 敌人掉落 / 开局铺的枪都走它）；捡地上用过的枪时传它的真实余量。
    /// <returns>true = 已收下；false = 快捷槽与库存都放不下（背包已满，枪留在原地）</returns></summary>
    public bool AddWeapon(WeaponData w, int magAmmo = -1, AmmoData ammoId = null)
    {
        if (w == null) return false;
        EnsureSlots();

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].weapon == null)
            {
                slots[i].weapon = w;
                slots[i].magAmmo = AmmoOf(w, magAmmo);
                slots[i].ammoId = ResolveDefaultAmmo(w, ammoId);
                currentSlot = i;
                return true;
            }
        }

        return AddWeaponItem(w, magAmmo, ammoId) > 0;   // 快捷槽满 → 进库存（武器不可堆叠 → 新占一格）
    }

    /// <summary>把一把武器放进**库存**（连弹匣余量 + 弹匣等级一起写进那一格）—— 库存侧的**唯一入口**，
    /// 替代直接调 `AddItems`。返回收下的数量（0 或 1）；`magAmmo` 负值 = 满弹。</summary>
    public int AddWeaponItem(WeaponData w, int magAmmo = -1, AmmoData ammoId = null)
    {
        if (w == null) return 0;

        // ⚠️ 必须**先定格、再建 ItemStack**（2026-10-27 审查 C-2）：
        //    旧写法是 `AddItems` 后再从末尾倒序找 "def == w" 的格子写弹匣 ——
        //    当库存里有空洞 / 有第二把同款枪时，会写到**另一把旧枪**身上：
        //    新入库的枪白送满弹，旧枪的余量与等级被静默改写。
        int idx = FirstFreeInventorySlot();
        if (idx < 0) return 0;

        SetInventoryAt(idx, new ItemStack
        {
            def = w,
            count = 1,
            magAmmo = AmmoOf(w, magAmmo),
            ammoId = ResolveDefaultAmmo(w, ammoId),
        });
        return 1;
    }

    /// <summary>从库存「装备」一把武器：装到空槽；没有空槽则替换当前槽，**被换下的武器下放进库存**。
    /// `displaced` = **只有库存真的放不下时**才非空（调用方负责把它掉到地上）—— 正常情况下为 null，
    /// 因为背包已经把它下放进库存了。⚠️ 调用方**不能**无条件把 `displaced` 掉落（会凭空多一把枪）。
    /// `stackId` = 物品身份证（由 `ItemStack.def.StackId` 得到；**不能**用对象引用 —— 敌人掉落的枪是副本）。</summary>
    public bool EquipFromInventory(string stackId, out WeaponData displaced, out int displacedAmmo,
                                   out AmmoData displacedAmmoId)
    {
        displaced = null;
        displacedAmmo = -1;
        displacedAmmoId = null;
        EnsureSlots();
        if (string.IsNullOrEmpty(stackId)) return false;

        int idx = -1;
        WeaponData weapon = null;
        int weaponAmmo = -1;                 // 库存那一格记的弹匣余量（-1 = 满弹）
        AmmoData weaponAmmoId = null;        // 库存那一格记的弹匣等级（null = 未知）
        for (int i = 0; i < inventory.Count; i++)
        {
            var s = inventory[i];
            if (s == null || s.def == null) continue;
            var w = s.def as WeaponData;
            if (w == null) continue;                 // 血包 / 子弹不是武器 → 不能装到快捷槽
            if (w.StackId != stackId) continue;
            idx = i; weapon = w; weaponAmmo = s.magAmmo; weaponAmmoId = s.ammoId; break;
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
            AmmoData oldAmmoId = slots[target].ammoId;
            if (old != null && AddWeaponItem(old, oldAmmo, oldAmmoId) <= 0)
            {
                displaced = old; displacedAmmo = oldAmmo; displacedAmmoId = oldAmmoId;
            }
        }

        slots[target].weapon = weapon;
        slots[target].magAmmo = AmmoOf(weapon, weaponAmmo);   // ← 用库存里记的余量，不再无条件给满
        slots[target].ammoId = weaponAmmoId;                  // ← 弹匣等级一起带过去
        currentSlot = target;
        return true;
    }

    /// <summary>从快捷槽丢弃一把武器（掉回地面后由调用方创建 WeaponPickup）。返回被丢弃的数据，空槽返回 null。</summary>
    public WeaponData DropSlot(int i, out int magAmmo, out AmmoData ammoId)
    {
        magAmmo = -1;
        ammoId = null;
        EnsureSlots();
        if (i < 0 || i >= slots.Length) return null;
        var w = slots[i].weapon;
        if (w == null) return null;
        magAmmo = slots[i].magAmmo;
        ammoId = slots[i].ammoId;
        slots[i].weapon = null;
        slots[i].magAmmo = -1;
        slots[i].ammoId = null;
        if (currentSlot == i) currentSlot = -1;   // 手上武器被丢掉 → 空手
        return w;
    }

    // ==================== 魔法槽（与武器槽完全同构；用户 2026-09-28 定） ====================

    /// <summary>当前装备的魔法卡（空槽为 null）。
    /// ⭐ **"装备了什么魔法"的唯一权威** —— <see cref="PlayerMagic"/> 每帧比对它来决定是否重建行为实例。
    /// 为什么权威放这里而不是放 `PlayerMagic`：这样**所有**改动路径（装备 / 丢弃 / 商店卖出 / 以后新增的路径）
    /// 都只需要改背包一处，不会出现"魔法还挂着但卡已经没了"的幽灵状态。</summary>
    public MagicData CurrentMagic
    {
        get
        {
            EnsureSlots();
            return magics.Length > 0 && magics[0] != null ? magics[0].magic : null;
        }
    }

    /// <summary>从库存「装备」一个魔法：装进**空魔法槽**；没有空槽则替换第 0 槽，
    /// **被换下的魔法下放进库存**；库存也放不下时通过 `displaced` 交给调用方掉到地上。
    ///
    /// ⚠️ 与武器版 `EquipFromInventory` 同一套纪律（那三条是踩过坑总结出来的）：
    ///   ① `stackId` 用**物品身份证**（`ItemStack.def.StackId`），不用对象引用
    ///   ② **先**从库存取走（腾出格子），**再**安置被替换下来的那张 —— 顺序反了会在库存满时静默丢卡
    ///   ③ 绝不"既下放又掉落"（那会凭空多出一张卡）</summary>
    public bool EquipMagicFromInventory(string stackId, out MagicData displaced)
    {
        displaced = null;
        EnsureSlots();
        if (string.IsNullOrEmpty(stackId) || magics.Length == 0) return false;

        int idx = -1;
        MagicData magicDef = null;
        for (int i = 0; i < inventory.Count; i++)
        {
            var s = inventory[i];
            if (s == null || s.def == null) continue;
            var m = s.def as MagicData;
            if (m == null) continue;                     // 武器 / 子弹 / 血包 → 不能装进魔法槽
            if (m.StackId != stackId) continue;
            idx = i; magicDef = m; break;
        }
        if (idx < 0 || magicDef == null) return false;

        // ① 先从库存取走（腾出格子）
        var item = inventory[idx];
        item.count -= 1;
        if (item.count <= 0) inventory.RemoveAt(idx);

        // ② 找空魔法槽；没有就替换第 0 槽（第一期只有一格）
        int target = -1;
        for (int i = 0; i < magics.Length; i++)
            if (magics[i].magic == null) { target = i; break; }

        if (target < 0)
        {
            target = 0;
            var old = magics[target].magic;
            // 魔法卡不可堆叠 → 下放就是占一格；放不下才交给调用方掉落
            if (old != null && AddItems(old, 1) <= 0) displaced = old;
        }

        magics[target].magic = magicDef;
        return true;
    }

    /// <summary>从魔法槽丢弃（掉到地上后由调用方创建 `MagicPickup`）。空槽返回 null。
    /// 与武器版 `DropSlot` 同一套语义。</summary>
    public MagicData DropMagicSlot(int i, out MagicData magicDef)
    {
        magicDef = null;
        EnsureSlots();
        if (i < 0 || i >= magics.Length) return null;
        magicDef = magics[i].magic;
        if (magicDef == null) return null;
        magics[i].magic = null;
        return magicDef;
    }

    /// <summary>整理库存：按显示名排序（背包面板右上角「整理」按钮调用）。</summary>
    public void SortInventory()
    {
        inventory.Sort((a, b) =>
        {
            // 拖拽留下的空位（null）统一排到最后，不能让它们插在物品中间
            bool ea = a == null || a.def == null || a.IsEmpty;
            bool eb = b == null || b.def == null || b.IsEmpty;
            if (ea && eb) return 0;
            if (ea) return 1;
            if (eb) return -1;
            return string.CompareOrdinal(a.DisplayName, b.DisplayName);
        });
    }

    /// <summary>用掉**指定格**的血包（右键菜单「使用」）；不是血包 / 越界 → null。</summary>
    public MedkitData UseMedkitAt(int index)
    {
        if (index < 0 || index >= inventory.Count) return null;
        var s = inventory[index];
        if (s == null || s.def == null) return null;
        var m = s.def as MedkitData;
        if (m == null || s.IsEmpty) return null;

        s.count -= 1;
        if (s.count <= 0) inventory.RemoveAt(index);
        return m;
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
