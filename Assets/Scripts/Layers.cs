using UnityEngine;

/// <summary>
/// 层（Layer）常量与 LayerMask 的收敛点。
///
/// ✅ **V-12 已全部完成**（2026-09-18）：项目里**所有** `Physics.Raycast` 都显式传了
///   LayerMask + `QueryTriggerInteraction.Ignore`。
///   ⚠️ 这里刻意**不列"几处 / 分别是哪里"**：那种清单必然随新增代码过期
///      （2026-09-29 注释审查时，原清单已经漏了 `Combat` 的爆炸遮挡和 `LaserBeam` 的激光，
///        还把爆炸遮挡错记在 `Bullet` 名下）。加新的射线时照这个约定写就行，不用回来改本行。
///   场景里的阻挡物也已分层（**动它们时记得一起维护**）：Ground→Ground、4 面墙 + 9 个箱子→Obstacle、Player→Player；
///   敌人由 `WaveManager.SpawnEnemy` 设 Enemy；血包 / 武器拾取物设 Pickup；特效设 FX。
///
/// ⚠️ **为什么必须传 `QueryTriggerInteraction.Ignore`**：`Physics.queriesHitTriggers` 默认是 **true**，
/// 不显式忽略的话，将来任何人加一个触发器（拾取区 / 技能范围 / 触发提示）都会被子弹和视线"当墙"吸收掉，
/// 而且不报错、极难定位。
///
/// ⚠️ **改分层方案时必须一次做完**：只给一半物体分层、或只给一半射线传 mask，会让 mask 非 0 却打不到阻挡物
/// → **射线穿墙**（比不传 mask 更糟，见 §4 #24）。
///
/// ⚠️ 用之前必须先给物体设置 Layer（Inspector 右上角 Layer 下拉）：mask 为 0 代表「什么都打不到」。
/// 工程里已定义：Player=8 / Enemy=9 / Pickup=10 / Ground=11 / Obstacle=12 / FX=13 / **Smoke=14**（2026-10-13 加）。
/// 用到的层若未定义，ValidateOnce() 会在进入 Play 时报错提示。
/// </summary>
public static class Layers
{
    public const string PlayerName = "Player";
    public const string EnemyName = "Enemy";
    public const string PickupName = "Pickup";
    public const string GroundName = "Ground";
    public const string ObstacleName = "Obstacle";
    public const string FxName = "FX";
    public const string SmokeName = "Smoke";

    /// <summary>玩家层索引（-1 = 工程里未定义该层）</summary>
    public static readonly int Player = LayerMask.NameToLayer(PlayerName);
    /// <summary>敌人层索引（-1 = 工程里未定义该层）</summary>
    public static readonly int Enemy = LayerMask.NameToLayer(EnemyName);
    /// <summary>拾取物层索引（-1 = 工程里未定义该层）</summary>
    public static readonly int Pickup = LayerMask.NameToLayer(PickupName);
    /// <summary>地面层索引（-1 = 工程里未定义该层）</summary>
    public static readonly int Ground = LayerMask.NameToLayer(GroundName);
    /// <summary>阻挡物层索引（-1 = 工程里未定义该层）</summary>
    public static readonly int Obstacle = LayerMask.NameToLayer(ObstacleName);
    /// <summary>纯表现特效层索引（-1 = 工程里未定义该层）</summary>
    public static readonly int FX = LayerMask.NameToLayer(FxName);
    /// <summary>烟云层索引（-1 = 工程里未定义该层）。
    /// ⚠️ 从 FX 里**独立出来**的唯一理由：烟是"要按半分辨率单独渲染 / 特殊排序"的那一类，
    ///    混在 FX 层里会连爆炸 / 枪口火光 / 激光 / 弹孔一起被处理。它**同样不在任何射线掩码里**。</summary>
    public static readonly int Smoke = LayerMask.NameToLayer(SmokeName);

    /// <summary>射线遮挡物：地面 + 阻挡物（视线与弹道应该被它们打断）</summary>
    public static readonly LayerMask Blocker = ToMask(Obstacle, Ground);

    /// <summary>弹道（子弹射线）：墙/箱子/地面挡住它，玩家/敌人被它命中</summary>
    public static readonly LayerMask BulletMask = ToMask(Obstacle, Ground, Player, Enemy);

    /// <summary>视线（敌人是否看得见玩家）：墙/箱子/地面遮挡；只有正好打到玩家才算看见</summary>
    public static readonly LayerMask SightMask = ToMask(Obstacle, Ground, Player);

    /// <summary>前方绕障：墙/箱子/地面 + 其它单位（**刻意保留"躲开同伴"的旧行为**，所以不是纯阻挡物）</summary>
    public static readonly LayerMask AvoidMask = ToMask(Obstacle, Ground, Player, Enemy);

    /// <summary>把多个层索引合成一个 LayerMask（自动忽略未定义的层）</summary>
    static int ToMask(params int[] layerIndices)
    {
        int m = 0;
        for (int i = 0; i < layerIndices.Length; i++)
            if (layerIndices[i] >= 0 && layerIndices[i] < 32) m |= 1 << layerIndices[i];
        return m;
    }

    /// <summary>进入 Play 时检查工程里是否定义了本类用到的全部层（没定义就报错，避免 mask=0 静默失效）</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ValidateOnce()
    {
        Check(Player, PlayerName);
        Check(Enemy, EnemyName);
        Check(Pickup, PickupName);
        Check(Ground, GroundName);
        Check(Obstacle, ObstacleName);
        Check(FX, FxName);
        Check(Smoke, SmokeName);
    }

    static void Check(int layerIndex, string layerName)
    {
        if (layerIndex < 0)
            Debug.LogError("[Layers] 工程里没有定义层「" + layerName +
                "」：请到 Project Settings → Tags and Layers 添加；否则按名取到的 mask 为 0（射线什么都打不到）。");
    }
}
