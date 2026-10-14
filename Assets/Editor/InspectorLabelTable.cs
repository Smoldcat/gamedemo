using System.Collections.Generic;

/// <summary>
/// Inspector「显示层」的汉化词典：`类型名.字段名` → 中文标签。
///
/// ⚠️ 这里**只放显示名**，一律不改 C# 字段名，理由：
///   · Unity 的 .asset / .prefab 是按**字段名**序列化的 —— 改字段名会让里面已存的值回落脚本默认值
///     （实例：`Player.prefab` 里的 `recoilRecoverJitterRatio = 0.011`、`recoilScreenMargin = 0`
///      一旦因为改名而丢失，手抖余量会变成 0 = "任何移动都打断回正"，是真实的手感回归）；
///   · 显示名不参与序列化，随便改，零风险。
///
/// 维护方式：新增字段后想让它显示中文，就到这里补一行；
/// **不补也不会消失/报错** —— `LocalizedInspectorEditor` 会回落到 Unity 默认英文标签，
/// 并在悬停描述里明确提示"这个字段还没登记中文名"，不会静默漏掉。
/// </summary>
public static class InspectorLabelTable
{
    static readonly Dictionary<string, string> Map = new Dictionary<string, string>
    {
        // ----------------------------- PlayerController -----------------------------
        { "PlayerController.gunVisual",                 "枪模型挂载点" },
        { "PlayerController.gunTip",                    "枪口发射点" },
        { "PlayerController.mainCamera",                "主相机" },
        { "PlayerController.walkSpeed",                 "行走速度" },
        { "PlayerController.runSpeed",                  "奔跑速度" },
        { "PlayerController.aimSpeed",                  "瞄准速度" },
        { "PlayerController.accel",                     "加速度" },
        { "PlayerController.weapon",                    "当前武器（运行时镜像）" },
        { "PlayerController.magAmmo",                   "当前弹匣（运行时镜像）" },
        // 注意：这里**不要和 [Header] 的文字写成一样**，否则 Inspector 上会出现
        //       「分组标题 = 字段名」两行一模一样的字（2026-09-20 踩过：Header 叫"武器背包"，字段也叫"武器背包"）
        { "PlayerController.backpack",                  "背包组件（PlayerBackpack）" },
        { "PlayerController.recoilKickSpeed",           "后坐推出速度" },
        { "PlayerController.mousePixelScale",           "鼠标换算系数（初值）" },
        { "PlayerController.recoilScreenMargin",        "屏幕边缘余量" },
        { "PlayerController.recoilRecoverMaxTime",      "回正时长上限" },
        { "PlayerController.recoilRecoverJitterRatio",  "回正手抖余量" },
        { "PlayerController.headshotGateScale",        "爆头判定圈倍率" },
        { "PlayerController.headshotRadiusRatio",       "爆头判定半径（旧口径）" },
        { "PlayerController.bodyAimRadius",             "身体瞄准半径" },

        // -------------------------------- Crosshair --------------------------------
        { "Crosshair.size",              "十字线长度" },
        { "Crosshair.gap",               "十字中心留空" },
        { "Crosshair.thickness",         "十字线粗细" },
        { "Crosshair.reloadRingSize",    "换弹环直径" },
        { "Crosshair.colorIdle",         "未瞄准颜色" },
        { "Crosshair.colorBody",         "指着身体颜色" },
        { "Crosshair.colorHead",         "指着头部颜色" },
        { "Crosshair.colorHitFlash",     "命中闪白颜色" },
        { "Crosshair.hitMarkerRadius",   "命中标记半径" },
        { "Crosshair.hitMarkerLength",   "命中标记线长" },
        { "Crosshair.hitMarkerThickness", "命中标记粗细" },
        { "Crosshair.hitMarkerDuration", "命中标记时长" },
        { "Crosshair.hitMarkerColor",    "命中标记颜色（普通）" },
        { "Crosshair.hitMarkerHeadColor", "命中标记颜色（爆头）" },
        { "Crosshair.spreadRingAlpha",   "散布圈透明度" },
        { "Crosshair.useVirtualCursor",  "虚拟光标（非游玩态）" },

        // -------------------------------- CameraRig --------------------------------
        // ⚠️ 别和 [Header]（"跟谁" / "相机参数"）写成一样，否则 Inspector 上会有两行一样的字
        { "CameraRig.target",          "跟随目标" },
        { "CameraRig.followLerp",      "相机跟随速度" },
        { "CameraRig.aimLeadRatio",    "镜头前探比例" },
        { "CameraRig.maxAimLead",      "镜头前探上限（米）" },
        { "CameraRig.aimFovNarrow",    "瞄准收窄视角（度）" },
        { "CameraRig.aimLeadExtra",    "瞄准前探增量（米）" },

        // ------------------- ItemData（物品定义基类：所有物品共有）-------------------
        // ⚠️ 登记在**基类**上就够了：`LocalizedInspectorEditor` 查不到本类型时会**逐层往上找基类**，
        //    所以武器 / 血包 / 以后的子弹都能共用这一份中文名，不必给每个子类重复登记一行。
        { "ItemData.icon",              "图标" },
        { "ItemData.pickupColor",       "物品颜色" },
        { "ItemData.pickupModelPrefab", "世界模型（地上外观）" },
        { "ItemData.canStack",          "可否堆叠" },
        { "ItemData.maxStack",          "单格上限" },
        { "ItemData.stackId",           "身份证(stackId)" },
        { "ItemData.description",       "说明文字" },
        { "ItemData.baseValue",         "基础价值（金币）" },

        // -------------------------------- MedkitData --------------------------------
        { "MedkitData.itemName",        "显示名" },
        { "MedkitData.healAmount",      "回血量" },

        // --------------------------------- Chest（箱子）---------------------------------
        // ⚠️ 别和 [Header]（"箱子" / "交互" / "行为开关"）写成一样，否则 Inspector 上会出现两行一样的字
        { "Chest.promptLabel",        "箱子名字" },
        { "Chest.config",             "箱子配方（刷什么）" },
        { "Chest.interactKey",        "交互按键" },
        { "Chest.interactRange",      "交互距离（米）" },
        { "Chest.solid",              "是实体（挡路 / 挡子弹）" },
        { "Chest.disappearWhenEmpty", "拿完消失" },
        { "Chest.promptWhenEmpty",    "空了也提示气泡" },

        // --------------------------------- Shop（商店）---------------------------------
        // ⚠️ 别和 [Header]（"商店" / "交互" / "行为开关"）写成一样，否则 Inspector 上会出现两行一样的字
        { "Shop.promptLabel",   "商店名字" },
        { "Shop.config",        "商店配方（卖什么）" },
        { "Shop.interactKey",   "交互按键" },
        { "Shop.interactRange", "交互距离（米）" },
        { "Shop.solid",         "是实体（挡路 / 挡子弹）" },
    };

    /// <summary>取中文标签；没登记返回 null（调用方负责回落英文标签）</summary>
    public static string Label(string typeName, string fieldName)
    {
        string v;
        return Map.TryGetValue(typeName + "." + fieldName, out v) ? v : null;
    }
}
