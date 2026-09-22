using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 武器资源（WeaponData .asset）在 Inspector 里的中文显示。
/// 说明：Unity 的 [InspectorName] 只对枚举生效，对普通字段无效，
/// 所以这里用自定义 Inspector（CustomEditor）逐字段改成中文标签和提示。
/// 字段名保持英文（序列化安全），只有显示层变成中文。
///
/// 维护提醒：在 WeaponData 里新增字段后，建议登记到下面的 Fields（中文名 + 顺序）。
/// 即使忘记登记也不会丢——未登记的字段会自动以默认英文标签显示在「其他」区（见 DrawUnmappedFields）。
/// </summary>
[CustomEditor(typeof(WeaponData))]
public class WeaponDataEditor : Editor
{
    /// <summary>字段名 → (中文标签, 悬停提示)，顺序即 Inspector 里的显示顺序</summary>
    static readonly (string propName, string label, string tip)[] Fields =
    {
        ("weaponName", "武器名称",          "武器显示名（出现在拾取提示和 HUD 上）"),
        ("damage",     "单发伤害",          "命中一发的伤害值"),
        ("fireInterval","开火间隔（秒）",    "两次开火的间隔，越小射速越快"),
        ("pellets",    "弹丸数",            "一次发射的弹丸数（霰弹枪 > 1）"),
        ("headshotMultiplier", "爆头倍率", "爆头伤害倍率：1 = 不翻倍（默认 2）。判定与准星颜色同源（准星变绿时开枪必定爆头），本值只决定「打头伤害多大」"),
        ("hipSpread",  "腰射散布（度）",     "腰射（不按右键）时的弹道散布角，0 = 完美精度"),
        ("aimSpread",  "瞄准散布（度）",     "按住右键瞄准时的弹道散布角。与腰射完全独立配置"),
        ("hipToAimTime", "腰射→瞄准过渡（秒）", "按下右键后，散布从「腰射散布」线性过渡到「瞄准散布」用时。0 = 瞬时（旧行为）。\n⚠️ 定速推进：过渡到一半就松开，回落只用「过渡时间 × 剩余比例」；只有散布走过渡，后坐/横向闸门/移速仍立即生效"),
        ("aimToHipTime", "瞄准→腰射过渡（秒）", "松开右键后，散布从「瞄准散布」线性回到「腰射散布」用时。0 = 瞬时（旧行为）。\n⚠️ 它是**上界**：过渡到一半就松开时按剩余比例缩短（只探了半个头就收回 → 只用一半时间）"),
        ("bulletSpeed","子弹速度",          "子弹飞行速度，越快曳光越长"),
        ("bulletLife","子弹存活时间（秒）", "子弹飞多久后消失。射程 = 子弹速度 × 存活时间"),
        ("magSize",    "弹匣容量",          "一次装填的子弹数"),
        ("reloadTime", "换弹时间（秒）",     "打空弹匣后换弹所需时间"),
        ("tracerColor","曳光颜色",          "子弹曳光的颜色"),
        ("pickupColor","拾取物高亮色",       "地上武器标识条颜色，也是 HUD 武器文字颜色"),
        ("gunLength",  "枪身长度",          "枪身长度，无模型时用于估算枪口位置"),
        ("shake",      "开火镜头震动",       "开火时镜头震动强度"),
        ("hipRecoilUp",       "腰射纵向后坐",    "腰射（不按右键）时每次开火把准星沿「玩家→准星」方向往外顶开的米数。\n⚠️「纵向」= 沿推出方向（往外推远），不是屏幕上的「上下」"),
        ("hipRecoilJitter",   "腰射横向后坐",    "腰射（不按右键）时每次开火垂直于推出方向的左右随机偏移（米），通常比纵向小。\n这是唯一会让弹道真正打偏的分量，也是「横向后坐上限」所限制的对象"),
        ("aimRecoilUpMultiplier",     "瞄准纵向后坐倍率", "按住右键瞄准时，纵向后坐 × 本值。1 = 与腰射一致；< 1 = 瞄准更稳（建议 0.5~0.7）；0 = 瞄准时无纵向后坐"),
        ("aimRecoilJitterMultiplier", "瞄准横向后坐倍率", "按住右键瞄准时，横向后坐 × 本值（语义同上）。⚠️ 它也同时作用于「横向后坐上限」，瞄准时横向带宽同比缩放"),
        ("recoilMaxSide",  "横向后坐上限（0=不限）", "本梭子横向最多偏多少米。0 = 不限（只受屏幕边界约束）；\n实现 = 把每发**实际推出去的**横向量累加，到顶**只拒绝继续往外推**、绝不回搬已推出去的位移。\n高射速 + 大横向后坐的枪建议填 1 左右：屏幕左右很宽，横向随机游走会累积到米级、变成压不住的乱飘。\n⚠️ 瞄准时上限会按「瞄准横向后坐倍率」同比缩放"),
        ("recoilRecover",  "回正速度",        "松手后准星回正速度（米/秒）"),
        ("recoilRecoverDelay", "回正延迟（秒）", "停止开火后准星静置多少秒才开始回正。0 = 立即回正（原行为）；\n静置期内再次开火 / 移动鼠标都会重新计时，鼠标一动则本次回正作废。\n只影响「何时开始回正」，回正总时长仍由回正速度 + 玩家组件的回正时长上限决定"),
        ("sound",      "枪声类型",          "决定开火/换弹音效风格：普通=手枪冲锋枪、霰弹=霰弹枪、狙击=狙击枪"),
        ("explosionRadius", "爆炸半径（米）", "子弹命中/到点后的爆炸半径。0 = 普通子弹无爆炸"),
        ("explosionDamage", "爆炸伤害",      "爆炸对范围内敌人造成的伤害（仅爆炸半径 > 0 时生效）"),
        ("iconKind",        "图标类型",       "背包里的图标形状。Auto = 按武器名/枪身长度自动推测"),
    };

    /// <summary>所有已被手动绘制的字段名（Fields + 外观三个），用于筛出漏配的新字段</summary>
    readonly HashSet<string> _handled = new HashSet<string>
    {
        "pickupModelPrefab", "heldModelPrefab", "bulletPrefab", "icon",
    };

    SerializedProperty _pickupModel;
    SerializedProperty _heldModel;
    SerializedProperty _bulletModel;
    SerializedProperty _icon;

    void OnEnable()
    {
        _pickupModel = serializedObject.FindProperty("pickupModelPrefab");
        _heldModel   = serializedObject.FindProperty("heldModelPrefab");
        _bulletModel = serializedObject.FindProperty("bulletPrefab");
        _icon        = serializedObject.FindProperty("icon");

        foreach (var f in Fields) _handled.Add(f.propName);
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // 数值与手感区
        foreach (var (propName, label, tip) in Fields)
        {
            var p = serializedObject.FindProperty(propName);
            if (p == null) continue;   // 容错：万一字段被改名也不至于整页崩溃
            EditorGUILayout.PropertyField(p, new GUIContent(label, tip));
        }

        // 外观区
        // ⚠️ 这里**不要**再画一条「外观（留空 = 用代码生成的默认外观）」标题：
        //    WeaponData.cs 上已经有同名的 [Header]，而 PropertyField 会自动把它画出来 → 再画一遍就是两行（2026-09-20 修）。
        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(_pickupModel, new GUIContent("地上模型 Prefab", "这把枪掉在地上时的模型 Prefab"));
        EditorGUILayout.PropertyField(_heldModel,   new GUIContent("手持模型 Prefab", "拿在玩家手里时的模型。留空则自动复用「地上模型」，通常只配一个就够"));
        EditorGUILayout.PropertyField(_bulletModel, new GUIContent("子弹模型 Prefab", "子弹/曳光模型。留空则用代码生成的曳光条，颜色跟随「曳光颜色」"));
        EditorGUILayout.PropertyField(_icon, new GUIContent("武器图标 Sprite", "背包/HUD 显示的武器图标。留空则用程序生成的形状图标"));

        // 兜底：漏配中文标签的新字段，自动以默认英文标签显示，保证绝不凭空消失
        DrawUnmappedFields();

        serializedObject.ApplyModifiedProperties();
    }

    /// 防呆设计：漏配中文标签的新字段画到「其他字段」区（英文标签），不会凭空消失。
    /// 实现已抽到 EditorFieldUtil，四个配置类 Inspector 共用一份。
    void DrawUnmappedFields() => EditorFieldUtil.DrawUnmapped(serializedObject, _handled);
}