using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 挂在敌人身上的**印记**（魔法② 的核心）：倒计时 + 条件判定 + 触发效果。
///
/// 为什么要有这个独立组件（而不是把逻辑写进 <see cref="ConditionMagic"/>）：
///   · 魔法活在**玩家**身上，而"等条件满足"要跟着**敌人** —— 两者生命周期不同
///   · 同一个魔法可以同时给多个敌人挂印记，每个敌人各自独立倒计时
///
/// 判定时机（⭐ 两条腿，各有分工）：
///   · **主判定**：订阅 <see cref="Health.OnDamaged"/> —— 伤害结算之后立刻判定，最准也最省
///   · **兜底轮询**：每 `checkInterval` 秒查一次，防止"没受伤但血量因别的原因降低"漏网
///
/// ⚠️ **成对纪律**：<see cref="OnDamaged"/> 的订阅必须在销毁 / 卸下魔法时退订，
///    否则敌人身上会留下指向已销毁组件的死回调。
/// ⚠️ 触发后走 `Health.TakeDamage`（而不是直接把 hp 设成 0）—— 这样伤害飘字、击杀播报、
///    武器掉落**全都自动获得**；用"当前血 + 1"保证无论多少血都刚好击杀。
/// </summary>
[DisallowMultipleComponent]
public class MagicMark : MonoBehaviour
{
    /// <summary>场上所有印记（供"卸下魔法时全部撤掉"用）。
    /// ⚠️ 静态注册表 → **必须**登记到 `StaticReset`。</summary>
    static readonly List<MagicMark> All = new List<MagicMark>();

    ConditionMagicData _d;
    PlayerMagic _owner;      // 谁挂的（卸载时按它清理）
    Health _hp;
    float _remain;           // 剩余标记时长
    float _checkTimer;       // 兜底检查倒计时
    GameObject _indicator;   // 头顶图标（独立物体，每帧跟随 —— 不挂成子物体是为了躲开敌人的体型缩放）
    bool _subscribed;
    bool _done;              // 已触发 / 已超时 → 本组件即将销毁

    void Awake()
    {
        All.Add(this);
        _hp = GetComponent<Health>();
    }

    void OnDestroy()
    {
        All.Remove(this);
        Unsubscribe();
        if (_indicator != null) Destroy(_indicator);
    }

    /// <summary>静态清理（供 `StaticReset`）：清注册表。
    /// ⚠️ 只清列表，不去 Destroy 那些组件 —— 它们随场景对象一起被销毁。</summary>
    internal static void ResetStatics() => All.Clear();

    // ==================== 挂上 / 刷新 ====================

    /// <summary>挂上印记；**重复调用 = 刷新持续时长**（用户 2026-09-28 定）。</summary>
    public void Attach(ConditionMagicData d, PlayerMagic owner)
    {
        if (d == null || _done) return;
        _d = d;
        _owner = owner;
        _remain = d.markDuration;
        _checkTimer = 0f;                 // 立刻做一次兜底检查
        if (_hp == null) _hp = GetComponent<Health>();
        EnsureIndicator();
        Subscribe();
    }

    /// <summary>把场上（由某个魔法挂的）印记全部撤掉 —— 卸下魔法时调用（成对清理）。</summary>
    public static void RemoveAllFrom(PlayerMagic owner)
    {
        for (int i = All.Count - 1; i >= 0; i--)
        {
            var m = All[i];
            if (m == null) continue;
            if (owner != null && m._owner != owner) continue;
            m.Cleanup();
        }
    }

    // ==================== 每帧 ====================

    void Update()
    {
        if (_done) return;
        if (_d == null || _hp == null) { Cleanup(); return; }
        if (!_hp.alive) { Cleanup(); return; }                  // 目标已经死了 → 印记没意义

        _remain -= Time.deltaTime;
        if (_remain <= 0f) { Cleanup(); return; }                // 超时消散

        FollowIndicator();                                      // 头顶图标跟随

        _checkTimer -= Time.deltaTime;
        if (_checkTimer <= 0f)
        {
            _checkTimer = _d.checkInterval;                     // 兜底轮询
            if (CheckCondition(_hp)) Trigger();
        }
    }

    /// <summary>伤害结算后立刻判定（主路径）。</summary>
    void OnDamagedHandler(float dmg, Vector3 fromDir)
    {
        if (_done || _d == null || _hp == null) return;
        if (CheckCondition(_hp)) Trigger();
    }

    void Subscribe()
    {
        if (_subscribed || _hp == null) return;
        _hp.OnDamaged += OnDamagedHandler;
        _subscribed = true;
    }

    void Unsubscribe()
    {
        if (!_subscribed || _hp == null) return;
        _hp.OnDamaged -= OnDamagedHandler;
        _subscribed = false;
    }

    // ==================== ⭐ 两个留缝方法 ====================

    /// <summary>⭐ **留缝**：条件判定集中在这里。
    /// 第一期 = 当前血量 ≤ 最大血量 × 阈值。
    /// 以后要加【受击 N 次 / 被爆头 / 挂满 X 秒 / 距玩家 X 米内】，
    /// **只改本方法** + 在 <see cref="ConditionTrigger"/> 里加枚举值。</summary>
    bool CheckCondition(Health h)
    {
        if (h == null || h.maxHp <= 0f) return false;
        switch (_d.conditionType)
        {
            case ConditionTrigger.HpBelowPercent:
            default:
                return (h.hp / h.maxHp) <= _d.executeThreshold;
        }
    }

    /// <summary>⭐ **留缝**：条件满足后干什么集中在这里。
    /// 第一期 = 天降雷柱 + 直接击杀。
    /// 以后要加【范围爆炸 / 给玩家回血 / 加 buff / 掉金币】，
    /// **只改本方法** + 在 <see cref="ConditionEffect"/> 里加枚举值；
    /// 表现（雷柱 / 闪光 / 震屏 / 音效 / 提示）是共用的，不必每个效果重写一遍。</summary>
    void ApplyEffect(Health h)
    {
        if (h == null) return;

        PlayBolt(h.transform.position);                              // 表现：雷柱落下
        if (_d.shakeAmount > 0f) CameraRig.Shake(_d.shakeAmount);     // 震屏
        Effects.Decal(h.transform.position, new Color(0.12f, 0.12f, 0.15f));   // 地面焦痕

        // ⭐ 触发闪光（强度 = 卡上的 flashAmount；0 = 不闪）
        //    ⚠️ 这个字段以前**没有任何代码读取**（2026-09-29 外部审查发现：Tooltip 写着"触发时的闪光强度"，
        //       但改它完全没效果）。现在真正接上了——位置抬到胸口高度，避免闪光被地面遮住。
        if (_d.flashAmount > 0f)
            Effects.Impact(h.transform.position + Vector3.up * 1.0f,
                           _d.boltColor, 1.5f + _d.flashAmount * 3f);

        if (_d.triggerSound != null) Sfx.Play(_d.triggerSound);

        switch (_d.effectType)
        {
            case ConditionEffect.ExecuteKill:
            default:
                // ⭐ 用"当前血 + 1"而不是直接把 hp 设为 0：
                //    前者会走完整的伤害流程（飘字 / 击杀播报 / 掉落 / 死亡变色），后者是"静默死"。
                //    同时也保证了「无论敌人有多少血都必定击杀」（不是按百分比扣血）。
                h.TakeDamage(h.hp + 1f, Vector3.up);
                break;
        }

        if (!string.IsNullOrEmpty(_d.triggerMessage))
            HUD.Announce(_d.triggerMessage, 1.0f, 30, _d.boltColor);

        if (_d.endSound != null) Sfx.Play(_d.endSound);

        // ⭐ 广播（HUD / 音效 / 以后的成就统计都能订阅，而不需要认识本类）
        if (_owner != null) _owner.ReportExecute(h);
    }

    /// <summary>条件满足 → 结算效果。
    ///
    /// ⭐ **冷却门**（用户 2026-09-29 定）：技能在冷却中时**不触发**，但**印记继续挂着**（`_done` 保持 false）。
    ///    为什么"继续挂着"而不是直接撤掉：冷却一好，<see cref="Update"/> 的兜底轮询
    ///    （或目标再受一次伤）就会重新判到它 → **不用玩家再补一刀**，
    ///    这就是"冷却开始前已挂的印记"的价值。
    ///
    /// ⚠️ 冷却加在 <see cref="PlayerMagic.PassiveReady"/>（**技能层**）而不是本组件上 ——
    ///    本组件是**每个敌人一份**，写在这里会导致"3 个低血敌人被一次连斩 3 个"。
    /// ⚠️ <see cref="PlayerMagic.NotifyPassiveTriggered"/> 必须放在 <see cref="ApplyEffect"/> **之后**：
    ///    要"真正斩成功了"才进冷却（不然没斩到却白转一圈）。</summary>
    void Trigger()
    {
        if (_done) return;
        if (_owner != null && !_owner.PassiveReady) return;   // ⭐ 技能冷却中 → 这次不触发

        _done = true;
        ApplyEffect(_hp);
        if (_owner != null) _owner.NotifyPassiveTriggered();  // ⭐ 生效成功 → 技能开始转冷却
        Cleanup();
    }

    void Cleanup()
    {
        if (_indicator != null) { Destroy(_indicator); _indicator = null; }
        Unsubscribe();
        Destroy(this);          // 销毁组件自己（OnDestroy 里会从注册表移除）
    }

    // ==================== 表现 ====================

    /// <summary>头顶图标：卡上配了 prefab 就用它；否则用代码生成的发光小方块（占位）。
    /// ⚠️ 做成**独立物体**而不是敌人的子物体：敌人有自己的 `bodyScale`，
    ///    挂成子物体的话图标会跟着被缩放（而且以后敌人换模型又会变）。</summary>
    void EnsureIndicator()
    {
        if (_indicator != null) return;

        if (_d.markIndicator != null)
            _indicator = Instantiate(_d.markIndicator);
        else
            _indicator = Effects.CreateMeshObject(PrimitiveType.Cube, "MagicMark", null,
                                                  Effects.Shared(_d.markColor));

        _indicator.name = "MagicMark";
        _indicator.transform.localScale = Vector3.one * 0.22f;
        FollowIndicator();
    }

    void FollowIndicator()
    {
        if (_indicator == null || _hp == null) return;
        _indicator.transform.position = transform.position + Vector3.up * _d.markHeight;
        _indicator.transform.Rotate(Vector3.up, 90f * Time.deltaTime, Space.World);   // 慢慢自转，显眼
    }

    /// <summary>从天而降的雷柱：一个细长的圆柱，出现 `boltLife` 秒后消失。
    /// ⚠️ Unity 内置圆柱的原始尺寸是「高 = 2 单位」→ 想得到 `boltHeight` 的实际高度，
    ///    缩放要填 `boltHeight / 2`（这是最容易写错的一处）。</summary>
    void PlayBolt(Vector3 pos)
    {
        GameObject bolt;
        if (_d.boltPrefab != null)
        {
            bolt = Instantiate(_d.boltPrefab);
        }
        else
        {
            bolt = Effects.CreateMeshObject(PrimitiveType.Cylinder, "LightningBolt", null,
                                            Effects.SharedAlpha(_d.boltColor));
            bolt.transform.localScale = new Vector3(_d.boltWidth, _d.boltHeight * 0.5f, _d.boltWidth);
        }

        bolt.transform.position = pos + Vector3.up * (_d.boltHeight * 0.5f);   // 柱子中心在半高处 → 底部贴地
        Destroy(bolt, _d.boltLife);
    }
}
