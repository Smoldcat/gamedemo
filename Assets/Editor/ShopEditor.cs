using UnityEditor;

/// <summary>
/// 商店组件（`Shop`）的中文 Inspector —— 与 `ChestEditor` 同一套：
/// 一行搞定，以后给 `Shop` 加字段**不用改这个文件**（显示逻辑全在通用基类里）。
/// ⚠️ 新增字段记得往 `InspectorLabelTable` 登记中文名（不登记不会报错，只会显示英文标签 + 悬停提示"该往词典加哪一行"）。
/// </summary>
[CustomEditor(typeof(Shop))]
public class ShopEditor : LocalizedInspectorEditor { }
