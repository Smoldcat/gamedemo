using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 敌人配置（EnemyConfig .asset）在 Inspector 里的中文显示。
/// 与 WeaponDataEditor 同思路：显示层中文，字段名保持英文（序列化安全）。
/// 「行为类型」是下拉框：自动列出所有 EnemyBehaviour 子类（含中文名），
/// 之后你新写一个行为脚本（继承 EnemyBehaviour）就会自动出现在下拉里。
/// 新增字段忘了登记到 Fields 时，会自动以默认英文标签显示在「其他字段」区，不会消失。
/// </summary>
[CustomEditor(typeof(EnemyConfig))]
public class EnemyConfigEditor : Editor
{
    /// <summary>字段名 → (中文标签, 悬停提示)，顺序即 Inspector 里的显示顺序</summary>
    static readonly (string propName, string label, string tip)[] Fields =
    {
        ("displayName",        "显示名",           "怪物名字（用于日志和生成物体命名）"),
        ("dropWeaponChance",   "掉落武器概率",      "死亡时掉落武器的概率（0=不掉；只有带武器的远程行为才掉）"),
        ("bodyPrefab",         "身体模型 Prefab",   "身体模型。留空则用代码生成的胶囊体"),
        ("bodyScale",          "体型缩放",          "代码生成胶囊体的整体缩放（用 Prefab 时忽略）"),
        ("bodyColor",          "身体颜色",          "代码生成胶囊体的颜色（用 Prefab 时忽略）"),
        ("maxHp",              "基础血量",          "第 1 波时的血量"),
        ("reactionTime",       "反应时间",          "发现玩家后到第一次攻击的停顿（秒）"),
        ("patrolSpeed",        "巡逻速度",          "没发现玩家时的移动速度"),
        ("sightRange",         "索敌视野距离",      "多远能发现玩家"),
        ("fov",                "视野角度",          "巡逻时的视野角度（追击时全向感知）"),
        ("loseSightTime",      "丢失视线放弃追击",  "失去视线多久后回到巡逻（秒）"),
        ("chaseSpeed",         "追击速度",          "发现玩家后的移动速度"),
        ("meleeDamage",        "近战伤害",          "贴身攻击一次造成的伤害"),
        ("attackRange",        "近战攻击距离",      "近战兵贴身攻击的距离"),
        ("meleeCooldown",      "近战攻击冷却（秒）", "近战兵两次攻击的间隔"),
        ("keepDistance",       "远程保持距离",      "远程兵/狙击手和玩家拉开的距离"),
        ("burstCount",         "连发弹数",          "一次连发打几发（三点射的弹数）"),
        ("burstGap",           "连发间隔（秒）",    "两次连发之间的间隔"),
        ("gunPrefab",          "远程枪模型",        "远程兵手里的枪。留空用代码生成"),
        ("shooterWeapon",      "远程基础武器",      "远程兵武器的基础模板（数值被下方射速/伤害覆盖）。留空用默认冲锋枪"),
        ("shooterDamage",      "远程伤害",          "远程兵/狙击手一枪的伤害"),
        ("fireInterval",       "远程开火间隔（秒）", "远程兵两次开火的间隔"),
        ("bulletSpeed",        "远程子弹速度",      "远程兵/狙击手的子弹速度"),
        ("spread",             "远程散布（度）",     "远程兵弹道散布（狙击手固定为 0）"),
        ("tracerColor",        "远程曳光颜色",      "远程兵/狙击手子弹的颜色"),
        ("explodeRadius",      "爆炸半径",          "自爆兵爆炸的伤害半径（米）"),
        ("explodeDamage",      "爆炸伤害",          "自爆兵爆炸对玩家的伤害"),
        ("fuseTime",           "自爆预警时间",      "自爆兵点燃引信到爆炸的间隔（秒），期间身体闪烁警告"),
        ("sniperAimTime",      "狙击蓄力时间",      "狙击手红线出现到开枪的间隔（秒）"),
        ("sniperCooldown",     "狙击冷却",          "狙击手开枪后到下一次能瞄准的间隔（秒）"),
        ("hpGrowthPerWave",    "每波血量成长",      "每过一波，血量增加多少（只对新刷敌人）"),
        ("hpGrowthCap",        "血量成长封顶",      "血量最多成长到多少封顶"),
        ("dmgGrowthPerWave",   "每波伤害成长",      "每过一波，近战/远程伤害各增加多少"),
        ("dmgGrowthCap",       "伤害成长封顶",      "伤害成长封顶值"),
    };

    /// <summary>头部 / 面部 / 爆头（单独成组显示，像 GameAssets 那样带分组标题）</summary>
    static readonly (string propName, string label, string tip)[] HeadFields =
    {
        ("headOffset",         "头部偏移",   "头相对敌人根物体的本地偏移（世界单位）。默认 (0, 0.93, 0) ≈ 坐在身体顶部。\n导入真实模型后按每个怪物单独调这里，不用改代码"),
        ("headRadius",         "头部半径",   "头部球体半径（世界单位）。默认 0.30"),
        ("headColor",          "头部颜色",   "头部颜色（与身体区分，一眼看出头顶在哪）"),
        ("faceColor",          "面部颜色",   "面部薄片颜色（贴在头的正前方，用来一眼看出面朝哪边）。用高对比亮色"),
    };

    /// <summary>所有已被手动绘制的字段名，用于筛出漏配的新字段</summary>
    readonly HashSet<string> _handled = new HashSet<string>();

    // ---- 行为类型下拉 ----

    static System.Type[] _behaviourTypes;

    /// <summary>所有 EnemyBehaviour 子类（新增行为脚本后会被扫描出来）</summary>
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
                    .Where(t => !t.IsAbstract && !t.IsGenericType && typeof(EnemyBehaviour).IsAssignableFrom(t))
                    .ToArray();
            return _behaviourTypes;
        }
    }

    /// <summary>行为在编辑器里显示的中文名：优先取 [BehaviourName] 特性，否则清掉类名里的敌/行为字样</summary>
    static string BehaviourDisplayName(System.Type t)
    {
        var a = t.GetCustomAttributes(typeof(BehaviourName), false);
        if (a.Length > 0 && a[0] is BehaviourName bn && !string.IsNullOrEmpty(bn.Name)) return bn.Name;
        return t.Name.Replace("Enemy", "").Replace("Behaviour", "");
    }

    void OnEnable()
    {
        foreach (var f in Fields) _handled.Add(f.propName);
        foreach (var f in HeadFields) _handled.Add(f.propName);
        _handled.Add("behaviourTypeName");   // 行为类型由下面的下拉框单独绘制
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawBehaviourPopup();

        foreach (var (propName, label, tip) in Fields)
        {
            var p = serializedObject.FindProperty(propName);
            if (p == null) continue;
            EditorGUILayout.PropertyField(p, new GUIContent(label, tip));
        }

        // 头部 / 面部 / 爆头（单独成组，方便一眼找到）
        // ⚠️ **不要在这里手画标题**：`EnemyConfig` 上已经有 `[Header("头部与爆头（外观 + 爆头判定锚点）")]`，
        //    而 `PropertyField` 会自动把 `[Header]` 画出来 → 再画一遍就是**两行标题**（2026-09-23 修）。
        foreach (var (propName, label, tip) in HeadFields)
        {
            var p = serializedObject.FindProperty(propName);
            if (p == null) continue;
            EditorGUILayout.PropertyField(p, new GUIContent(label, tip));
        }

        // 兜底：漏配中文标签的新字段，自动以默认英文标签显示，保证绝不凭空消失
        DrawUnmappedFields();

        serializedObject.ApplyModifiedProperties();
    }

    /// <summary>「行为类型」下拉：列出运行时所有 EnemyBehaviour 子类，选择结果写回 behaviourTypeName 字符串</summary>
    void DrawBehaviourPopup()
    {
        var typeProp = serializedObject.FindProperty("behaviourTypeName");
        if (typeProp == null) return;

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel(new GUIContent("行为类型", "敌人怎么打。下拉自动列出所有行为脚本，新增行为后自动出现"));

            string current = typeProp.stringValue;
            var t = System.Type.GetType(current);
            string display = t != null ? BehaviourDisplayName(t) : "";

            if (GUILayout.Button(string.IsNullOrEmpty(display) ? "（未设置，默认近战）" : display, EditorStyles.popup, GUILayout.MinWidth(150)))
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
                menu.ShowAsContext();
            }
        }
        EditorGUILayout.Space();
    }

    /// 兜底：漏配中文标签的新字段自动以英文标签显示，保证绝不凭空消失（实现见 EditorFieldUtil）
    void DrawUnmappedFields() => EditorFieldUtil.DrawUnmapped(serializedObject, _handled);
}