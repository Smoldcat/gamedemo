using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 商店配方（`ShopConfig.asset`）的中文 Inspector —— 与 `ChestConfigEditor` 同一套做法。
///
/// ⚠️ 嵌套表的**内部字段**（货架条目里的 物品 / 买入价 / 卖出价 / 最大库存 / 每次补几个）仍由 Unity 默认绘制：
///    标签是英文、悬停说明是中文（`ShopEntry` 上的 `[Tooltip]`）。
///    要连标签也汉化，需要给 `ShopEntry` 另写 `PropertyDrawer`（见交接文档 §5.2）—— 先用着够用。
///
/// ⚠️ 这里**不自己画分组标题**：`ShopConfig.cs` 上的 `[Header]` 由 `PropertyField` 自动画，
///    再画一遍就会每个标题出现两行（项目里踩过两次，见 §5.2 铁律）。
/// </summary>
[CustomEditor(typeof(ShopConfig))]
public class ShopConfigEditor : Editor
{
    /// <summary>字段名 → (中文标签, 悬停提示)。顺序即显示顺序。</summary>
    static readonly (string propName, string label, string tip)[] Fields =
    {
        ("slotCount",      "货架格数",      "面板排版用。⚠️ 条目数不能超过格数 —— 超出的条目不会显示（会告警）"),
        ("entries",        "货架条目",      "每项 = 一样物品 + 买入价 + 卖出价 + 最大库存 + 每次补几个。\n⚠️ 条目里的那几个字段仍是英文标签（悬停有中文说明）\n⚠️ 数据是「引用」物品定义卡，改物品卡三处（背包 / 箱子 / 商店）一起变"),
        ("restockSeconds", "补货间隔（秒）", "整店补货：从开局起算，每过这么久把**没满的**条目补一次（暂停时停表）"),
        ("sellValueRatio", "表外收购比例",   "不在货架上的物品（敌人掉的枪 / 箱子开出来的东西）卖出价 = 物品卡「基础价值」× 本比例。\n0 = 这家店不收表外物品"),
    };

    readonly HashSet<string> _handled = new HashSet<string>();

    void OnEnable()
    {
        foreach (var f in Fields) _handled.Add(f.propName);
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        for (int i = 0; i < Fields.Length; i++)
        {
            var p = serializedObject.FindProperty(Fields[i].propName);
            if (p == null) continue;   // 容错：字段被改名也不至于整页崩溃
            EditorGUILayout.PropertyField(p, new GUIContent(Fields[i].label, Fields[i].tip), true);
        }

        // 兜底：漏配中文标签的新字段自动以英文标签显示，绝不凭空消失
        EditorFieldUtil.DrawUnmapped(serializedObject, _handled);

        serializedObject.ApplyModifiedProperties();
    }
}
