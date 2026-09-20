using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 波次配置（WaveConfig .asset）在 Inspector 里的中文显示。
/// 与 WeaponDataEditor / EnemyConfigEditor 同思路：显示层中文，字段名保持英文（序列化安全）。
/// 新增字段忘了登记到 Fields 时，会自动以默认英文标签显示在「其他字段」区，不会消失。
/// </summary>
[CustomEditor(typeof(WaveConfig))]
public class WaveConfigEditor : Editor
{
    /// <summary>字段名 → (中文标签, 悬停提示)，顺序即 Inspector 里的显示顺序</summary>
    static readonly (string propName, string label, string tip)[] Fields =
    {
        ("initialEnemies",    "开局敌人数",      "第 1 波开局生成的敌人数"),
        ("baseEnemies",       "波次人数基数",    "每波人数 = 基数 + 波次 × 每波增量"),
        ("enemiesPerWave",    "每波人数增量",    "每过一波，该波敌人数增加多少"),
        ("maxEnemies",        "单波人数上限",    "单波敌人数最多多少封顶"),
        ("spawnInterval",     "刷怪间隔（秒）",  "每波内两个敌人之间的生成间隔"),
        ("clearingDelay",     "清场倒计时（秒）", "清完一波到下一波开始的等待时间"),
        ("idleTimeout",       "僵局兜底（秒）",  "敌人长时间没交战，会被传送到玩家附近"),
    };

    /// <summary>所有已被手动绘制的字段名，用于筛出漏配的新字段</summary>
    readonly HashSet<string> _handled = new HashSet<string>();

    void OnEnable()
    {
        foreach (var f in Fields) _handled.Add(f.propName);
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        foreach (var (propName, label, tip) in Fields)
        {
            var p = serializedObject.FindProperty(propName);
            if (p == null) continue;
            EditorGUILayout.PropertyField(p, new GUIContent(label, tip));
        }

        // 兜底：漏配中文标签的新字段，自动以默认英文标签显示，保证绝不凭空消失
        DrawUnmappedFields();

        serializedObject.ApplyModifiedProperties();
    }

    /// 兜底：漏配中文标签的新字段自动以英文标签显示，保证绝不凭空消失（实现见 EditorFieldUtil）
    void DrawUnmappedFields() => EditorFieldUtil.DrawUnmapped(serializedObject, _handled);
}