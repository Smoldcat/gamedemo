using UnityEditor;

/// <summary>
/// 准星组件的 Inspector 汉化。显示逻辑全在 <see cref="LocalizedInspectorEditor"/> 基类里，
/// 这里只需要声明"我要接管这个类型"——**新增字段无需改本文件**。
/// </summary>
[CustomEditor(typeof(Crosshair))]
public class CrosshairEditor : LocalizedInspectorEditor
{
}
