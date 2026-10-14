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

    /// <summary>
    /// 取"行为 / 魔法在编辑器里显示的中文名"：**优先读名字特性**（如 `[BehaviourName]` / `[MagicName]`），
    /// 否则用类名裁剪掉给定词（如 "Enemy"/"Behaviour"、或 "Magic"）。
    /// 2026-10-15：原来 `EnemyConfigEditor` 与 `MagicDataEditor` 各写一份同名同逻辑的私有实现 → 收敛到这里，
    /// 两处调用改成一行（各自的差异 = 特性类型 + 裁剪词，由参数表达）。
    /// </summary>
    public static string DisplayNameOfType(System.Type t, System.Type nameAttrType, params string[] strip)
    {
        if (t == null) return "";
        if (nameAttrType != null)
        {
            var attrs = t.GetCustomAttributes(nameAttrType, false);
            if (attrs.Length > 0 && attrs[0] != null)
            {
                // 特性里的名字成员可能是字段也可能是属性，两种都试（不猜死）
                var f = nameAttrType.GetField("Name");
                var p = f == null ? nameAttrType.GetProperty("Name") : null;
                var v = (f != null ? f.GetValue(attrs[0]) : (p != null ? p.GetValue(attrs[0], null) : null)) as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        string n = t.Name;
        if (strip != null)
            for (int i = 0; i < strip.Length; i++)
                if (!string.IsNullOrEmpty(strip[i])) n = n.Replace(strip[i], "");
        return n;
    }
}