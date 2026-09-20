using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 通用「中文 Inspector」基类 —— 组件端只需一行 `[CustomEditor(typeof(X))] public class XEditor : LocalizedInspectorEditor {}`。
///
/// 它做四件事：
///   ① **自动遍历该组件的全部序列化字段**（不手写清单 → 不可能漏字段、也不会漏掉任何分组）；
///   ② 自动把 `[Tooltip]` 当悬停描述 —— **描述的唯一来源是脚本里的特性**
///      （想改描述就改脚本，不在这里抄第二份，避免两处不一致）；
///   ②-b ⚠️ **绝不自己画 `[Header]`**：Unity 的 `PropertyField` 已经会把 `[Header]` 装饰器画出来。
///      实证：`EditorGUI.GetPropertyHeight(有 Header 的字段)` = **45**、同组无 Header 的 = **18**，
///      差的 27px 就是 Header；`UnityEditor.PropertyHandler` 里也确实有 `m_DecoratorDrawers`。
///      **自己再画一遍 → 每个分组标题重复两行**（2026-09-20 踩过这个坑）。
///   ③ 查 `InspectorLabelTable` 取中文标签；查不到就回落 Unity 默认英文标签，
///      并在悬停描述里提示"该字段未登记中文名"（防止新增字段静默变英文、没人发现）；
///   ④ 自动把 **C# 字段名** 附在悬停描述末尾 —— 方便直接拿去代码 / 交接文档里搜。
///
/// ⚠️ 只画**顶层字段**：数组 / 嵌套可序列化类的**内部元素**仍由 Unity 默认绘制（要汉化它们需另写 PropertyDrawer）。
/// ⚠️ 这是**纯显示层**：不参与序列化、不进包，不会改动 .asset / .prefab 里的任何数值。
/// </summary>
public abstract class LocalizedInspectorEditor : Editor
{
    Dictionary<string, FieldInfo> _fields;   // 字段名 → FieldInfo（用来读 [Header] / [Tooltip] 特性）

    void EnsureFields()
    {
        if (_fields != null) return;
        _fields = new Dictionary<string, FieldInfo>();
        // 连同基类字段一起收集（DeclaredOnly + 逐层向上），保证继承来的字段也能拿到特性
        var t = target.GetType();
        while (t != null && t != typeof(object))
        {
            var fs = t.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (int i = 0; i < fs.Length; i++)
                if (!_fields.ContainsKey(fs[i].Name)) _fields[fs[i].Name] = fs[i];
            t = t.BaseType;
        }
    }

    public override void OnInspectorGUI()
    {
        EnsureFields();
        serializedObject.Update();

        var it = serializedObject.GetIterator();
        bool enterChildren = true;
        while (it.NextVisible(enterChildren))
        {
            enterChildren = false;              // false → 只迭代顶层属性（嵌套内容交给下面的 PropertyField 画）
            string field = it.propertyPath;

            if (field == "m_Script")
            {
                // 与 Unity 默认 Inspector 一致：脚本引用只读展示（便于确认组件挂对了脚本）
                GUI.enabled = false;
                EditorGUILayout.PropertyField(it);
                GUI.enabled = true;
                continue;
            }

            // ⚠️ 这里**不要**再自己画 [Header] —— PropertyField 已经画了（重复会变成两行，见类注释 ②-b）
            EditorGUILayout.PropertyField(it, BuildContent(field), true);
        }

        serializedObject.ApplyModifiedProperties();
    }

    /// <summary>中文标签 + 「脚本描述 + 未登记提示 + 字段名」三合一悬停描述</summary>
    GUIContent BuildContent(string fieldName)
    {
        string typeName = target.GetType().Name;

        string zh = InspectorLabelTable.Label(typeName, fieldName);
        string label = zh != null ? zh : ObjectNames.NicifyVariableName(fieldName);   // 回落 Unity 默认英文标签

        var sb = new System.Text.StringBuilder();
        var fi = Find(fieldName);
        if (fi != null)
        {
            var tips = fi.GetCustomAttributes(typeof(TooltipAttribute), true);
            if (tips != null && tips.Length > 0)
            {
                string tip = ((TooltipAttribute)tips[0]).tooltip;
                if (!string.IsNullOrEmpty(tip)) sb.Append(tip);
            }
        }

        if (zh == null)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("⚠ 这个字段还没登记中文名。想让它显示中文，到 Assets/Editor/InspectorLabelTable.cs 里加一行：\"")
              .Append(typeName).Append('.').Append(fieldName).Append("\" → \"中文名\"。");
        }

        if (sb.Length > 0) sb.Append("\n\n");
        sb.Append("字段名：").Append(fieldName);   // 方便直接拿去代码 / 交接文档里搜

        return new GUIContent(label, sb.ToString());
    }

    FieldInfo Find(string fieldName)
    {
        FieldInfo fi;
        return _fields.TryGetValue(fieldName, out fi) ? fi : null;
    }
}
