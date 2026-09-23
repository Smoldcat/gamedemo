using UnityEngine;

/// <summary>
/// 血包（<see cref="ItemData"/> 的一个具体子类）。
///
/// 以前「血包」是代码里写死的一个枚举（`ItemKind.Medkit`）+ 一个世界脚本（`HealthPack`），**没有任何数据**；
/// 现在它是一张正式的「定义卡」，于是：
///   · 想调回血量 → 改这张卡（不用改代码）
///   · 想加「大血包 / 小血包」→ 再建一张卡（零代码）
///   · 箱子 / 背包显示它 → 走和其它物品完全相同的那套 UI
///
/// ⚠️ 血包**不可堆叠**（一格一个）—— 用户 2026-09-22 明确要求「有几个一目了然」，所以 `canStack` 保持默认 false。
/// </summary>
[CreateAssetMenu(fileName = "Medkit_新血包", menuName = "游戏/物品/血包", order = 10)]
public class MedkitData : ItemData
{
    [Header("血包")]
    [Tooltip("显示名（背包 / 箱子 / 拾取气泡里显示的名字）。留空则用资源文件名")]
    public string itemName = "血包";
    [Tooltip("使用后回复的生命值")]
    [Min(0f)] public float healAmount = 50f;

    public override string DisplayName => string.IsNullOrEmpty(itemName) ? name : itemName;

    protected override void OnValidate()
    {
        base.OnValidate();      // ⚠️ 必须调：否则基类的数值兜底会被这里覆盖掉
        if (healAmount < 0f) healAmount = 0f;
    }
}
