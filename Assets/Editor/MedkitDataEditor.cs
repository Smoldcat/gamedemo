using UnityEditor;

/// <summary>
/// 血包定义卡（<see cref="MedkitData"/>）的中文 Inspector。
///
/// 显示逻辑**全在基类** <see cref="LocalizedInspectorEditor"/> 里：
///   自动遍历全部字段 + 自动读 `[Tooltip]` 当悬停描述 + 查词典取中文名 + 自动把字段名附在描述末尾。
/// → **以后新增字段不用改本文件**；想改中文名只要去 `InspectorLabelTable` 加一行。
///
/// ⚠️ 中文名的来源：`MedkitData` 自己的字段登记在 `MedkitData.*`；
///    而 `icon` / `canStack` / `description` 这些**基类字段**登记在 `ItemData.*` 一份即可 ——
///    基类会逐层往上查（见 `LocalizedInspectorEditor.BuildContent` 的注释）。
///
/// ⚠️ **纯显示层**：不改任何 C# 字段名、不参与序列化、不动任何 .asset 里的数值。
/// </summary>
[CustomEditor(typeof(MedkitData))]
public class MedkitDataEditor : LocalizedInspectorEditor
{
}
