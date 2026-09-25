using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 资源总表（GameAssets .asset）在 Inspector 里的中文显示。
/// 与 WeaponDataEditor 同思路：字段名保持英文（序列化安全），显示层转中文。
/// 维护提醒：新增字段后登记到下面的 Fields；忘记登记也不会丢，
/// 未登记字段会以英文标签出现在「其他」区（见 DrawUnmappedFields）。
/// </summary>
[CustomEditor(typeof(GameAssets))]
public class GameAssetsEditor : Editor
{
    /// <summary>字段名 → (中文标签, 悬停提示)。顺序即显示顺序。</summary>
    static readonly (string propName, string label, string tip, string header)[] Fields =
    {
        ("enemyMeleePrefab", "敌人身体模型", "敌人的通用身体模型 Prefab（敌人可单独在 EnemyConfig.bodyPrefab 覆盖）", "敌人模型（留空 = 代码生成胶囊体）"),
        ("enemyGunPrefab",   "敌人枪模型",   "远程敌人手里那把枪的模型 Prefab", null),
        ("healthPackPrefab", "血包模型",     "血包模型 Prefab", "道具模型（留空 = 代码生成默认外观）"),
        ("impactPrefab",     "命中火花",     "命中火花，建议带 ParticleSystem 的 Prefab，播完自动销毁", "特效（留空 = 代码生成默认特效）"),
        ("muzzleFlashPrefab","枪口火光",     "开火时的枪口火光 Prefab", null),
        ("decalPrefab",      "弹孔/血迹",    "地面弹孔/血迹 Prefab", null),

        ("damageTextPrefab", "伤害飘字 Prefab", "伤害数字的预制体（UI 物体，含 Text 即可，可自定义字体/样式/动画）。留空用代码生成默认样式", "UI 特效（留空 = 代码生成默认样式）"),
        ("crosshairSprite",  "准星图片",         "全局默认准星外观（Crosshair 组件上单独拖了则优先用组件的）。留空用代码生成的十字", null),

        ("killIcon",             "普通击杀图标",       "普通击杀时屏幕下方中央显示的图标。留空用代码生成的占位（四角星）", "击杀反馈（留空 = 代码生成占位）"),
        ("killHeadshotIcon",     "爆头击杀图标",       "最后致命一击是爆头时替换普通图标。留空用代码生成的占位（靶心）", null),
        ("killFeedDuration",     "击杀反馈时长(秒)",   "每条从出现开始存活多久，到时向右滑出并淡出。默认 2", null),
        ("killFeedIconSize",     "击杀图标大小(px)",   "参考分辨率 1280×720 下的像素，运行时按屏幕等比缩放。默认 64", null),
        ("killFeedSpacing",      "击杀图标间距(px)",   "多个图标之间的间距（参考分辨率下）。默认 16", null),
        ("killFeedNameFontSize", "击杀名字字号",       "名字文本的字号（参考分辨率下）。默认 20", null),
        ("killFeedMaxCount",     "最多显示条数",       "同时最多显示几条；0 = 不限（默认 0）", null),

        ("killFeedInScale",      "入场起始放大倍数",   "入场「吸到屏幕上」的起始放大倍数（由大 → 小、收束到 1）。越大越有冲击力、越小越含蓄。默认 1.6", "击杀反馈 · 出现 / 消失动画（2026-09-22 起可调）"),
        ("killFeedInAlpha",      "入场起始透明度",     "入场起始透明度（半透明 → 不透明）。0 = 从全透明淡入；1 = 一开始就不透明（只剩缩放）。默认 0.5", null),
        ("killFeedInDuration",   "入场时长(秒)",       "「吸到屏幕上」那一下用时。越短越干脆、越长越明显。默认 0.12", null),
        ("killFeedOutDuration",  "消失时长(秒)",       "存活时间到后「向右滑出 + 淡出」用时。默认 0.3", null),
        ("killFeedSlideDistance","消失滑出距离(px)",   "消失时向右滑出的距离（参考分辨率 1280×720 下）。默认 40", null),
        ("killFeedRelayoutSpeed","重居中速度(px/秒)",  "其余条目因重新居中而挪动的速度。默认 900", null),
        ("killFeedBottomOffset", "距屏幕底部(px)",     "整组距屏幕底部的距离。⚠️ 只在建 Canvas 时读一次，运行中改要重进 Play。默认 150（原 96，2026-09-22 上移给快捷栏腾位）", null),

        ("backpackInventorySlots",  "库存格数",        "同类武器在同一格里叠加，所以等于「最多能装几种武器」。默认 20", "背包（留空 = 代码生成占位）"),
        ("backpackPanelSprite",     "面板底图",        "背包面板底图（Sprite，九宫格）。留空用代码生成的圆角面板。建议白色/浅色 + 透明", null),
        ("backpackFrameSprite",     "描边/格子边框",   "库存格子与丢弃按钮的边框（Sprite，九宫格）。留空用代码生成", null),
        ("backpackSlotFrameSprite", "快捷槽边框",      "快捷武器槽边框（Sprite，九宫格）。留空用代码生成。建议白色描边 + 透明", null),
        ("backpackMedkitIconSprite","血包图标",        "血包十字图标（Sprite）。留空用代码生成", null),
        ("backpackPanelColor",      "面板底色",        "背包面板底色（与底图相乘）", null),
        ("backpackSlotColor",       "槽边框色",        "快捷槽普通边框色（当前槽用「当前槽高亮色」）", null),
        ("backpackSlotActiveColor", "当前槽高亮色",    "当前选中的快捷槽的高亮色", null),
        ("backpackCellColor",       "库存格底色",      "库存格子的底色", null),
        ("backpackUIPrefab",        "背包界面 Prefab", "整套背包 UI 的 Prefab（Canvas + 面板结构）：拖入后背包完全用该 Prefab，可在编辑器里拖拽改布局；留空用代码生成的界面。⚠️ 节点名有约定（见 BackpackPanel 类注释）", null),

        ("shot",          "普通枪声",   "手枪/冲锋枪的枪声", "音效（留空 = 代码合成默认音效）"),
        ("shotgun",       "霰弹枪声",   "霰弹枪的枪声", null),
        ("sniper",        "狙击枪声",   "狙击枪的枪声", null),
        ("hit",           "命中反馈",   "子弹命中时的反馈音", null),
        ("headshotHit",   "爆头命中反馈", "命中敌人头部时的反馈音（替代普通命中音，不必击杀）", null),
        ("kill",          "击杀确认",   "击杀敌人时的确认音", null),
        ("pickup",        "拾取武器",   "捡起武器时的音效", null),
        ("hurt",          "受伤",       "玩家受伤时的音效", null),
        ("heal",          "治疗",       "回血时的音效", null),
        ("magOut",        "拔弹匣",     "换弹开始：拔出弹匣", null),
        ("magInChamber",  "插匣上膛",   "换弹完成：插入弹匣上膛", null),
        ("pump",          "泵动",       "霰弹枪泵动护木", null),
        ("bolt",          "拉栓",       "狙击枪拉栓", null),
        ("explosion",     "爆炸",       "RPG 命中与自爆兵自爆共用的爆炸音效。留空 = 代码合成", null),

        ("invOpen",       "打开背包",   "打开背包面板的音效", "背包 UI 音效（留空 = 代码合成默认音效）"),
        ("invClose",      "关闭背包",   "关闭背包面板的音效", null),
        ("invConfirm",    "点击/确认",  "点击装备、丢弃确认第一步的短音", null),
        ("invDrop",       "丢弃武器",   "丢弃武器时的音效", null),

        ("shopBuy",       "买入成功",   "商店买入成功时的音效（收银机「叮」）。留空 = 代码合成占位音", "商店音效（留空 = 代码合成默认音效）"),
        ("shopSell",      "卖出成功",   "商店卖出成功时的音效（金币入袋「叮当」）。留空 = 代码合成占位音", null),

        ("uiFont",        "界面字体",   "界面统一字体（.ttf/.otf）。留空 → 系统雅黑 → 内置兜底。\n填了则**编辑器与运行时同源**（编辑期所见即运行所得）。建议用开源可商用字体（思源黑体 / Noto Sans CJK）；微软雅黑有版权、勿随工程分发", "字体（留空 = 系统字体 / 内置兜底）"),

        ("virtualCursorSprite", "光标图片",     "虚拟光标（打开背包 / 死亡结算时显示的那个指针）。留空 = 代码生成的白色箭头（白箭头 + 深色描边）。\n⚠️ 导入设置必须是 Sprite；切成多张时取第一张。\n⚠️ 热点（指针尖）取 **Sprite 自己的 pivot** —— 在 Sprite Editor 里把 pivot 拖到箭头尖端；不设则按图片中心。\n⚠️ 只在建画布时读一次 → 换图要重进 Play", "虚拟光标（留空 = 程序生成的箭头）"),
        ("virtualCursorSize",   "光标大小(px)", "显示宽度（参考分辨率 1280×720 下，运行时随 UI 等比缩放）；高度按图片宽高比自动算。默认 24", null),
    };

    readonly HashSet<string> _handled = new HashSet<string>();

    void OnEnable()
    {
        foreach (var f in Fields) _handled.Add(f.propName);
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        string lastHeader = null;
        foreach (var (propName, label, tip, header) in Fields)
        {
            if (header != null && header != lastHeader)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(header, EditorStyles.boldLabel);
                lastHeader = header;
            }
            var p = serializedObject.FindProperty(propName);
            if (p == null) continue;   // 容错：字段被改名也不至于整页崩溃
            EditorGUILayout.PropertyField(p, new GUIContent(label, tip));
        }

        DrawUnmappedFields();
        serializedObject.ApplyModifiedProperties();
    }

    /// <summary>兜底：漏配中文标签的新字段自动以英文标签显示，绝不凭空消失（实现见 EditorFieldUtil）</summary>
    void DrawUnmappedFields() => EditorFieldUtil.DrawUnmapped(serializedObject, _handled);
}