using System;
using UnityEngine;

/// <summary>
/// 玩家侧**魔法管理器**：装备同步 / 冷却 / 释放入口 / 命中转发 / 事件广播。
///
/// 为什么单独一层（不塞进 <see cref="PlayerController"/>）：
///   · `PlayerController` 已经 1400+ 行、7 类职责 —— 再加魔法只会继续长胖
///   · "公共流程"（装备 / 卸下 / CD / 按键入口）与"某个魔法怎么工作"
///     （<see cref="MagicBehaviour"/> 子类）必须分开
///   · HUD / 音效 / 以后任何系统想关心魔法，**订阅下面的事件**即可，不用认识本类内部
///
/// ⭐ 状态归属（**只有一个权威**，别搞双份 —— 项目里"弹匣等级漂移"的教训就是双份状态）：
///   · **背包是权威**：当前装备的是哪张卡，读 `backpack.magics[0].def`
///     （与武器完全同构：装备 = 从库存移进槽，丢弃 = 从槽掉到地上）
///   · 本类只缓存"由那张卡创建出来的**行为实例** + 冷却剩余"——纯运行时态，不进背包
///
/// ⚠️ 挂在 **Player** 物体上，由 `PlayerController` 运行时自动创建（不需要往场景里预挂）。
/// ⚠️ 静态单例 → **必须**登记到 `StaticReset`（否则重开一局会残留上一局的冷却/装备）。
/// </summary>
public class PlayerMagic : MonoBehaviour
{
    public static PlayerMagic Instance;

    // ==================== 事件（外部只订阅这些，不要去读内部字段） ====================

    /// <summary>装备变化（卸下时传 null）。HUD 据此换图标 / 角标。</summary>
    public event Action<MagicData> OnEquipChanged;
    /// <summary>成功释放。</summary>
    public event Action<MagicData> OnCast;
    /// <summary>冷却开始（HUD 开始画遮罩）。</summary>
    public event Action<MagicData> OnCooldownStart;
    /// <summary>冷却结束（HUD 撤掉遮罩 / 图标恢复）。</summary>
    public event Action<MagicData> OnCooldownEnd;
    /// <summary>条件类魔法触发（例：印记斩杀生效）。参数 = 魔法卡 + 被作用的目标。</summary>
    public event Action<MagicData, Health> OnExecute;

    // ==================== 运行时状态 ====================

    PlayerBackpack _backpack;
    MagicData _equipped;         // 当前生效的卡（背包槽里那份的镜像，**每帧核对**）
    MagicBehaviour _behaviour;   // 由卡反射创建的行为实例
    float _cdRemaining;

    /// <summary>**每张卡各自的冷却剩余**（秒）。键 = 卡资产。
    /// ⚠️ 为什么需要它（2026-09-29 外部审查发现的漏洞）：
    ///    `_cdRemaining` 只表示"当前装备那张卡"的冷却；换卡时若把它直接清零，
    ///    那么"卸下 → 装回**同一张卡**"就能白嫖掉冷却（实测两次施放只隔 2 帧、场上出现 10 颗球）。
    ///    现在换卡时先存旧卡的剩余、再取新卡的剩余 —— 换**别的**卡仍然立刻可用（旧行为不变），
    ///    但换**回同一张**会接着走原来的冷却。
    /// ⚠️ 本表是**实例字段**（随 PlayerMagic 对象销毁而消失），不需要登记 StaticReset
    ///    —— 与该处"只清单例、不碰实例字段"的约定一致。</summary>
    readonly System.Collections.Generic.Dictionary<MagicData, float> _cdByCard
        = new System.Collections.Generic.Dictionary<MagicData, float>();

    /// <summary>当前装备的魔法卡（null = 空槽）。</summary>
    public MagicData Equipped => _equipped;

    /// <summary>冷却剩余（秒）。0 = 就绪。</summary>
    public float CooldownRemaining => _cdRemaining;

    /// <summary>冷却进度 0~1（1 = 刚放完；0 = 就绪）—— HUD 画扇形/竖向遮罩用。
    /// 未装备或该魔法无 CD 时恒为 0。</summary>
    public float CooldownRatio
    {
        get
        {
            if (_equipped == null || _equipped.cooldown <= 0f) return 0f;
            return Mathf.Clamp01(_cdRemaining / _equipped.cooldown);
        }
    }

    /// <summary>当前行为实例（可能为 null：没装备，或卡上的行为类名解析失败）。</summary>
    public MagicBehaviour Behaviour => _behaviour;

    /// <summary>现在能不能放（冷却就绪 + 魔法自己的条件）。HUD 可用它决定图标灰不灰。
    /// ⚠️ 被动魔法恒为 false（被动没有"放"这个动作）—— 它的就绪状态看 <see cref="PassiveReady"/>。</summary>
    public bool CanCastNow
        => _equipped != null && !_equipped.isPassive
           && _cdRemaining <= 0f
           && _behaviour != null && _behaviour.CanCast();

    /// <summary>**被动魔法**是否就绪（不在冷却中）。主动走 <see cref="CanCastNow"/>，被动走这里。
    ///
    /// ⭐ 用户 2026-09-29 定的设计：被动也能有冷却，冷却期间**完全不生效**。
    ///    · 卡上 `cooldown` 填 **0** → 恒为 true = **时刻生效**（这就是"一个字段覆盖两种行为"）
    ///    · 填大于 0 → 生效一次后进冷却，冷却走完才恢复
    ///
    /// ⚠️ 为什么必须放在本类（"技能层"）而不是挂在敌人身上的 <see cref="MagicMark"/>：
    ///    `MagicMark` 是**每个被标记的敌人一份**，冷却写在那里就变成"每个敌人各算各的 CD"
    ///    → 3 个敌人都低于阈值时会被**一次连斩 3 个**，那不是技能冷却。
    ///    只有放在这里（全场一份）才能保证"斩一次 → 其他敌人身上的印记也得等"。</summary>
    public bool PassiveReady => _cdRemaining <= 0f;

    /// <summary>[DEBUG-ONLY] 调试面板用：**清空当前魔法的冷却**（并广播 `OnCooldownEnd` 让 HUD 撤遮罩）。
    /// ⚠️ 必须**同时清每卡冷却表**（`_cdByCard`）：否则"切走再切回来"会把旧冷却读回来（见 `_cdByCard` 的注释）。
    /// 中性语义：把冷却视为已走完。发布前清理见 `Assets/Debug/README.md` 登记表。</summary>
    public void ClearCooldown()
    {
        if (_equipped != null) _cdByCard[_equipped] = 0f;   // 每卡账本也要清
        if (_cdRemaining <= 0f) { _cdRemaining = 0f; return; }
        _cdRemaining = 0f;
        OnCooldownEnd?.Invoke(_equipped);                   // HUD 撤遮罩（与 Tick 里自然走完的分支一致）
    }

    // ==================== 生命周期 ====================

    void Awake()
    {
        // 单例防护：出现重复实例（如场景重载的极端时序）时丢弃后出现的那个
        // ⚠️ 2026-10-10 补：本类曾是 10 个单例里**唯一**漏了这道防护的（其余 9 个都有，
        //    见 PlayerController / Crosshair / BackpackPanel 的同款注释与写法）。
        if (Instance != null && Instance != this)
        {
            Destroy(this);   // ✅ 只销毁重复组件，不能销毁整个 Player（B-2；其他单例都是 Destroy(this)）
            return;
        }
        Instance = this;
        _backpack = GetComponent<PlayerBackpack>();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        DestroyBehaviour();
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）：清单例 ——
    /// ⚠️ 这里**不能**去动 <see cref="Instance"/> 指向对象里的字段（那是场景对象，会被正常销毁）。</summary>
    internal static void ResetStatics() => Instance = null;

    /// <summary>
    /// 每帧由 <see cref="PlayerController"/> 驱动（⚠️ 暂停 / 死亡时它不会调到这里 —— 与武器一致）。
    /// 职责：① 核对背包槽有没有换魔法 ② 推进冷却 ③ 驱动行为自己的 Tick。
    /// ⚠️ 冷却用 `Time.deltaTime`：暂停时 `timeScale = 0` → 冷却自动冻结，不需要额外判暂停。
    /// </summary>
    public void Tick(float dt)
    {
        SyncFromBackpack();

        if (_cdRemaining > 0f)
        {
            _cdRemaining -= dt;
            if (_cdRemaining <= 0f)
            {
                _cdRemaining = 0f;
                OnCooldownEnd?.Invoke(_equipped);   // HUD 撤遮罩
            }
        }

        if (_behaviour != null) _behaviour.Tick(dt);
    }

    /// <summary>
    /// **装备同步的唯一入口**：拿背包槽里的卡和当前生效的卡比对，不一致就重建行为。
    /// 为什么"每帧核对"而不是"让背包通知我"：一次引用比较的成本可以忽略，但换来了
    /// **所有**改动路径（装备 / 丢弃 / 商店卖出 / 以后可能的其它路径）都自动生效 ——
    /// 不会出现"某条路径忘了通知 → 魔法还挂着但卡已经没了"。
    /// </summary>
    void SyncFromBackpack()
    {
        MagicData want = null;
        if (_backpack != null && _backpack.magics != null && _backpack.magics.Length > 0)
        {
            var s = _backpack.magics[0];
            if (s != null) want = s.magic;
        }
        if (ReferenceEquals(want, _equipped)) return;   // 没变（绝大多数帧都走这里）

        // ⭐ 换卡时的冷却处理（2026-09-29 修）：
        //    先把"当前卡的剩余"存回表，再把"新卡的剩余"取出来 ——
        //    ⚠️ 以前是直接 `_cdRemaining = 0f`，导致"卸下 → 装回同一张卡"能白嫖掉冷却
        //       （外部审查实测：两次施放只隔 2 帧）。
        //    换到**另一张卡**时读到的是它自己的剩余（通常是 0 = 立刻可用），
        //    所以"换了魔法就能用"这条旧行为不变；变的只是"换回同一张不再重置"。
        if (_equipped != null) _cdByCard[_equipped] = _cdRemaining;

        DestroyBehaviour();          // 旧行为必须**先**卸下（成对清理），再建新的
        _equipped = want;
        _cdRemaining = (want != null && _cdByCard.TryGetValue(want, out float cd)) ? cd : 0f;

        if (want != null) CreateBehaviour(want);

        OnEquipChanged?.Invoke(_equipped);
    }

    void CreateBehaviour(MagicData def)
    {
        var t = def.BehaviourType;
        if (t == null || !typeof(MagicBehaviour).IsAssignableFrom(t))
        {
            // ⚠️ 不静默失败：卡上填错类名 / 忘了填程序集名时，必须让作者一眼看出来
            Debug.LogError($"[PlayerMagic] 魔法卡「{def.DisplayName}」的 behaviorTypeName 解析失败：" +
                           $"\"{def.behaviourTypeName}\"（应为 \"类名, 程序集名\"，且必须继承 MagicBehaviour）。" +
                           $"该魔法将不可用。", def);
            return;
        }

        _behaviour = (MagicBehaviour)gameObject.AddComponent(t);
        _behaviour.Init(def, this);
        _behaviour.OnEquip();
    }

    void DestroyBehaviour()
    {
        if (_behaviour == null) return;
        // ⚠️ 顺序：先让子类自己撤掉副作用（挂出去的标记 / 订阅 / 生成的物体），再销毁组件
        _behaviour.OnUnequip();
        Destroy(_behaviour);
        _behaviour = null;
    }

    // ==================== 对外入口 ====================

    /// <summary>玩家按下魔法键（E）时由 <see cref="PlayerController"/> 调用。
    ///
    /// ⚠️ 提示策略（这里曾经写成"所有失败路径都会提示"，与代码不符，2026-09-29 修正）：
    ///    **会**提示（说清原因，与武器换弹的提示风格一致）：
    ///      · 冷却中 → 报剩余秒数
    ///      · 魔法自己拒绝（<see cref="MagicBehaviour.CanCast"/> + <see cref="MagicBehaviour.DenyCast"/>）
    ///    **静默返回**（这是有意的，不是漏写）：
    ///      · 空槽（没装魔法）—— 正常状态，按 E 就刷屏反而烦
    ///      · 被动魔法 —— 它没有"释放"这回事，装备即生效
    ///      · 行为实例为 null —— 装备时 <see cref="CreateBehaviour"/> 已经报过一条 LogError 了</summary>
    public void TryCast()
    {
        if (_equipped == null) return;                       // 空槽：什么都不做（不提示，避免"没装备"也刷屏）
        if (_equipped.isPassive) return;                     // 被动魔法没有"释放"这回事
        if (_cdRemaining > 0f)
        {
            HUD.Announce($"冷却中  {_cdRemaining:0.0}s", 1.0f, 26, new Color(0.9f, 0.7f, 0.4f));
            return;
        }
        if (_behaviour == null) return;
        if (!_behaviour.CanCast()) return;                   // 魔法自己会通过 DenyCast 说明原因

        _behaviour.Cast();
        OnCast?.Invoke(_equipped);

        if (_equipped.cooldown > 0f)
        {
            _cdRemaining = _equipped.cooldown;
            OnCooldownStart?.Invoke(_equipped);
        }
    }

    /// <summary>玩家命中敌人时由 <see cref="Combat"/> 转发（直击与爆炸两条路径都会调）。
    /// ⚠️ 这是**每次命中**都会走的路径 → 第一行必须尽快退出（没装被动魔法时一次比较就返回）。</summary>
    public void NotifyHitEnemy(Health enemy, bool headshot)
    {
        if (enemy == null || _behaviour == null || _equipped == null || !_equipped.isPassive) return;
        _behaviour.OnPlayerHitEnemy(enemy, headshot);
    }

    /// <summary>⭐ 被动魔法**成功生效后**由它自己调用（例：<see cref="MagicMark"/> 斩杀成功）→ 开始转冷却。
    ///
    /// ⚠️ 必须是"**成功生效**之后"才调，不能"检测到条件就调"——
    ///    否则会出现"没斩到（被别人抢了人头 / 目标已死）却白转一圈冷却"。
    /// ⚠️ 卡上 `cooldown` 填 0 时**直接返回、不进冷却** → 就是"时刻生效"（用户要的默认行为）。
    ///
    /// ⚠️ **必须判 `isPassive`**（2026-09-29 全面审查补的边界）：
    ///    印记触发的时刻，`_equipped` 理论上应该是"挂印记的那张被动卡"，但**同一帧换卡**时
    ///    `SyncFromBackpack` 可能已经把 `_equipped` 换成别的卡了 —— 若不判就会给**主动卡**
    ///    误设冷却（玩家会看到"刚换上的魔法自己进冷却"）。判一下 `isPassive` 即可挡住。
    ///
    /// ⚠️ **HUD 需要改（这里曾经写错，已修正）**：我原以为"<see cref="CooldownRatio"/> 不判被动
    ///    → HUD 一行不用改"。实际只对了一半：`HUD.UpdateHotbar` 里**键位文字**那个分支
    ///    把"被动"判在"冷却"之前 → 被动恒显示「被」、看不到秒数。
    ///    已在 `HUD.cs` 把冷却判断提前（现在被动冷却中会显示剩余秒数，与主动一致）。</summary>
    public void NotifyPassiveTriggered()
    {
        // ⚠️ 只认【被动卡】：防"同帧换卡"时给主动卡误设冷却（见上方注释）
        if (_equipped == null || !_equipped.isPassive || _equipped.cooldown <= 0f) return;
        _cdRemaining = _equipped.cooldown;
        OnCooldownStart?.Invoke(_equipped);
    }

    /// <summary>供魔法子类上报"我触发了一个效果"（例：印记斩杀）→ 本类广播给外部（HUD / 音效 / 统计）。
    /// ⚠️ 为什么绕一层：让魔法子类**不需要认识 HUD**，保持"逻辑不碰 UI"的解耦纪律。</summary>
    public void ReportExecute(Health target) => OnExecute?.Invoke(_equipped, target);

    /// <summary>让魔法子类也能读到玩家的背包（例：以后要做"消耗弹药放技能"）。
    /// 第一期没人用，但留着一个**明确**的取用口，比以后让子类自己 `GetComponent` 好。</summary>
    public PlayerBackpack Backpack => _backpack;
}
