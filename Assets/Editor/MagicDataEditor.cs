using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 魔法卡（<see cref="MagicData"/> 及其子类）在 Inspector 里的**「行为类型」下拉框**。
///
/// ⭐ 为什么需要它（2026-09-29 全项目注释审查发现）：
///   `MagicData.behaviourTypeName` 的 Tooltip 一直写着"别手打 —— 用下面的「行为类型」下拉框选"，
///   但**这个下拉框从来不存在** —— 敌人侧有 `EnemyConfigEditor.DrawBehaviourPopup`，
///   魔法侧却什么都没写，作者只能手打 `"类名, 程序集名"`（漏了程序集名就装备不上）。
///   本文件把那句承诺兑现：下拉自动列出**所有 `MagicBehaviour` 子类**，新增行为脚本后自动出现。
///
/// ⚠️ `[CustomEditor(typeof(MagicData), true)]` 的第二个参数 **必须是 true** ——
///    否则本编辑器只作用于 `MagicData` 本身，**子类（ProjectileMagicData / ConditionMagicData）
///    不会继承它**，作者打开具体魔法卡时还是看不到下拉。
///
/// ⚠️ 绘制方式是「**默认绘制 + 追加一行下拉**」而不是"接管全部字段"：
///    · 默认绘制保留了所有 `[Header]` 分组标题与 `[Tooltip]`（自己逐字段画很容易丢掉它们，
///      项目里 `ChestConfigEditor` 就专门为"Header 画两遍"踩过一次坑）；
///    · `behaviourTypeName` 的**文本框仍然保留**（想手改也能改），下拉只是更省事的那条路。
///    · 好处：这个文件**不会随着卡上加字段而过期** —— 不用像 `WeaponDataEditor` 那样维护 Fields 表。
/// </summary>
[CustomEditor(typeof(MagicData), true)]
public class MagicDataEditor : Editor
{
    // ==================== 行为类型清单（带缓存） ====================

    static System.Type[] _behaviourTypes;

    /// <summary>所有 <see cref="MagicBehaviour"/> 子类（非抽象）。
    /// ⚠️ 缓存是 static：**重新编译后编辑器域重载会自动清掉** → 新增行为脚本后无需手动刷新。
    /// ⚠️ `GetTypes()` 可能对某些程序集抛异常（缺少依赖）→ 逐程序集 try，别让一个坏程序集拖垮整个下拉。</summary>
    static System.Type[] BehaviourTypes
    {
        get
        {
            if (_behaviourTypes == null)
                _behaviourTypes = System.AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch { return System.Type.EmptyTypes; }
                    })
                    .Where(t => !t.IsAbstract && !t.IsGenericType && typeof(MagicBehaviour).IsAssignableFrom(t))
                    .OrderBy(t => BehaviourDisplayName(t))
                    .ToArray();
            return _behaviourTypes;
        }
    }

    /// <summary>行为在编辑器里显示的中文名：优先取 <see cref="MagicName"/> 特性，
    /// 否则用类名去掉 `Magic` 后缀（与 `EnemyBehaviour` 的 `[BehaviourName]` 是同一套约定）。
    /// （实现已收敛到 `EditorFieldUtil.DisplayNameOfType`，与 `EnemyConfigEditor` 共用一份 —— 2026-10-15）</summary>
    static string BehaviourDisplayName(System.Type t)
        => EditorFieldUtil.DisplayNameOfType(t, typeof(MagicName), "Magic");

    // ==================== 绘制 ====================

    public override void OnInspectorGUI()
    {
        // ① 原有的一切（[Header] 分组 / [Tooltip] / 所有字段）照常绘制 —— 不接管、不重排
        DrawDefaultInspector();

        // ② 追加"行为类型"下拉（在字段下方）
        EditorGUILayout.Space();
        DrawBehaviourPopup();
    }

    /// <summary>「行为类型」下拉：列出所有 <see cref="MagicBehaviour"/> 子类（含中文名），
    /// 选择结果写回 `behaviourTypeName` 字符串（格式 `"类名, 程序集名"`）。</summary>
    void DrawBehaviourPopup()
    {
        var typeProp = serializedObject.FindProperty("behaviourTypeName");
        if (typeProp == null) return;      // 理论上不会（MagicData 上有这个字段）

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel(new GUIContent(
                "行为类型（下拉选）",
                "这里的下拉 = 上面那个字段的便捷入口，两者改的是同一个值。\n" +
                "自动列出所有 MagicBehaviour 子类（中文名取自 [MagicName] 特性）——新写一个行为脚本后会自动出现。\n" +
                "⚠️ 选中后写入的是「类名, 程序集名」的完整格式，比手打少一个漏程序集名的坑。"));

            string current = typeProp.stringValue;
            var t = System.Type.GetType(current);
            string display = t != null ? BehaviourDisplayName(t) : "";

            string btnLabel = string.IsNullOrEmpty(display)
                ? (string.IsNullOrEmpty(current) ? "（未设置）" : "（解析失败：" + current + "）")
                : display;

            if (GUILayout.Button(btnLabel, EditorStyles.popup, GUILayout.MinWidth(150)))
            {
                var menu = new GenericMenu();
                foreach (var bt in BehaviourTypes)
                {
                    string qualified = bt.FullName + ", " + bt.Assembly.GetName().Name;
                    bool selected = current == qualified;
                    menu.AddItem(new GUIContent(BehaviourDisplayName(bt)), selected, () =>
                    {
                        typeProp.stringValue = qualified;
                        serializedObject.ApplyModifiedProperties();
                    });
                }
                if (BehaviourTypes.Length == 0) menu.AddDisabledItem(new GUIContent("（没有找到任何 MagicBehaviour 子类）"));
                menu.ShowAsContext();
            }
        }

        // ③ 自检提示：填了但解析不出来 → 当场说清楚（配了但不生效的字段最坑，与 HUD / Chest 的自检同一套约定）
        string raw = typeProp.stringValue;
        if (!string.IsNullOrEmpty(raw) && System.Type.GetType(raw) == null)
        {
            EditorGUILayout.HelpBox(
                "这个「行为类型」解析不出来：\"" + raw + "\"\n" +
                "正确格式是 \"类名, 程序集名\"（例：ProjectileGroupMagic, Assembly-CSharp），" +
                "并且该类必须继承 MagicBehaviour。\n用上面的下拉选一个即可修好。",
                MessageType.Warning);
        }
    }
}
