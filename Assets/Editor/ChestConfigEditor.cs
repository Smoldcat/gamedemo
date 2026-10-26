using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 箱子配方（`ChestConfig.asset`）的中文 Inspector。
///
/// ⚠️ 嵌套表的**内部字段**（物品池里的 物品/权重/数量、抽取次数表里的 次数/权重）仍由 Unity 默认绘制：
///    标签是英文、悬停说明是中文（`ItemPoolEntry` / `RollChance` 上的 `[Tooltip]`）。
///    要连标签也汉化，需要给它们另写 `PropertyDrawer`（见交接文档 §5.2 的说明）—— 先用着够用。
///
/// ⚠️ 这里**不自己画分组标题**：`ChestConfig.cs` 上的 `[Header]` 由 `PropertyField` 自动画，
///    再画一遍就会每个标题出现两行（项目里踩过两次，见 §5.2 铁律）。
/// </summary>
[CustomEditor(typeof(ChestConfig))]
public class ChestConfigEditor : Editor
{
    /// <summary>字段名 → (中文标签, 悬停提示)。顺序即显示顺序。</summary>
    static readonly (string propName, string label, string tip)[] Fields =
    {
        ("slotCount",      "显示格数",         "以后大箱子填 10 也不用改代码。默认 5"),
        ("pool",           "物品池",           "每项 = 一样物品 + 出现权重 + 一次抽到几个。空表 = 箱子永远是空的"),
        ("guaranteedItem", "保底物品",         "【必出】留空 = 不保底。军火箱填一把枪、医疗箱填一个血包 —— **不写死类型**，由你指定"),
        ("guaranteedMin",  "保底数量（最少）",  "保底物品一次出几个（最少）"),
        ("guaranteedMax",  "保底数量（最多）",  "保底物品一次出几个（最多）"),
        ("rollTable",      "抽取次数表",       "每项 = 次数 + 权重（例：3 样 50% / 5 样 10%）→ 箱子不会每箱都塞满。\n留空 = 每箱都抽满（次数 = 格数）"),
        ("searchSecondsPerItem", "每件搜刮时长(秒)", "有物品的格子从「未知态」变成「显示图标」要经过多久。\n⚠️ 0 = 用资源总表 GameAssets 的全局默认（0.6）；填 >0 = 这个箱子统一按它来（个别物品仍可在物品卡上覆盖）"),
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
