using UnityEditor;

/// <summary>
/// 相机组件（<see cref="CameraRig"/>）的 Inspector 汉化。
/// 显示逻辑全在 <see cref="LocalizedInspectorEditor"/> 基类里，这里只需要声明"我要接管这个类型"——
/// **新增字段无需改本文件**（中文名去 `InspectorLabelTable` 登记一行即可；不登记也不会报错，
/// 只会显示英文标签并在悬停里提示"该往词典加哪一行"）。
/// </summary>
[CustomEditor(typeof(CameraRig))]
public class CameraRigEditor : LocalizedInspectorEditor
{
}
