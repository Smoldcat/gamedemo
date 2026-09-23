using UnityEditor;

/// <summary>
/// 箱子组件（<see cref="Chest"/>）的中文 Inspector。
///
/// 显示逻辑**全在基类** <see cref="LocalizedInspectorEditor"/>：自动遍历全部字段 + 自动读 `[Tooltip]`
/// 当悬停描述 + 查词典取中文名 + 把字段名附在描述末尾 → **以后加字段不用改本文件**。
/// 中文名在 `InspectorLabelTable` 登记（`Chest.*` 那几行）。
///
/// ⚠️ 纯显示层：不改任何 C# 字段名、不参与序列化、不动任何 .asset / 场景里的数值。
/// </summary>
[CustomEditor(typeof(Chest))]
public class ChestEditor : LocalizedInspectorEditor
{
}
