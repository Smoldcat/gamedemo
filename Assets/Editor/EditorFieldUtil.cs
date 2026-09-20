using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor 工具：把 4 个配置类 Inspector 共用的「未映射字段兜底绘制」抽到一处，避免复制粘贴。
/// 兜底逻辑：不在映射表里的顶层字段用默认英文标签画出来（防呆设计——新增字段忘了登记
/// 中文映射时也会显示，而不会从 Inspector 里凭空消失）。
/// </summary>
public static class EditorFieldUtil
{
    public static void DrawUnmapped(SerializedObject so, HashSet<string> handled)
    {
        var it = so.GetIterator();
        bool shownHeader = false;

        while (it.NextVisible(true))
        {
            if (it.propertyPath == "m_Script") continue;         // 脚本引用行，不显示
            if (it.depth > 0) continue;                          // 只处理顶层字段
            if (handled.Contains(it.propertyPath)) continue;     // 已手动绘制的跳过

            if (!shownHeader)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("其他字段（未配置中文标签的新字段）", EditorStyles.boldLabel);
                shownHeader = true;
            }
            EditorGUILayout.PropertyField(it);   // 默认绘制：Unity 自动生成英文标签
        }
    }
}