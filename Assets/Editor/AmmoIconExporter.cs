using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 弹药图标**导出工具**：菜单 `Tools/游戏/导出弹药图标`。
///
/// 为什么需要它：弹药图标一直是 `ItemVisuals` 在**运行时**用代码画出来的（`Sprite.Create`）——
/// 编辑器里看不见、也没法换美术、更没法"再往上加一层描边"。本工具把那份形状代码**落成正式 PNG 资源**：
///   · 5 张图 → `Assets/Art/UI/Ammo/Ammo_{口径}.png`（**白色剪影**，配色仍由卡上的 `pickupColor` 给）
///   · 自动设好导入参数（Sprite / Full Rect / **Read-Write** / **不压缩** —— 后两项是"膨胀描边"要读像素、要干净边缘）
///   · 自动回填到 `Assets/Items` 下**所有**同口径弹药卡的 `icon` 字段
///     → `ItemVisuals.IconOf` 本来就是"卡上配了图标就用它"，所以填完**显示的东西逐像素不变**，只是来源从"每次生成"变成"读资产"
///
/// ⚠️ 本工具会**覆盖**同名 PNG（它就是"以程序形状为准"的导出器）→ 有二次确认。
///    以后美术替换了正式图标，**别再跑它**（否则会被程序形状盖回去）。
/// ⚠️ 导出后 `ItemVisuals` 里那份形状代码**必须保留**：它是"没填 `icon` 的卡"（以及本工具自己）的兜底。
/// ⚠️ 形状只有一份实现：本工具**不重画**，直接调 <see cref="ItemVisuals.MakeAmmoTexture"/>。
/// </summary>
public static class AmmoIconExporter
{
    const string Dir = "Assets/Art/UI/Ammo";
    const int MenuOrder = 310;   // 与「重建物品清单」等工具排在一起

    [MenuItem("Tools/游戏/导出弹药图标", false, MenuOrder)]
    static void ExportFromMenu()
    {
        int existing = CountExisting();
        string tip = "将把 5 张程序画的弹药图标写成 PNG 到：\n" + Dir + "\n\n"
                   + "并自动回填到所有同口径弹药卡的 icon 字段。\n"
                   + (existing > 0 ? "⚠️ 其中 " + existing + " 张文件已存在，会被**覆盖**（美术替换过的请先备份）。\n" : "")
                   + "\n继续？";
        if (!EditorUtility.DisplayDialog("导出弹药图标", tip, "导出", "取消")) return;

        Export(verbose: true);
    }

    /// <summary>无弹窗的入口（供脚本 / 自动化调用），返回"改动了多少张卡"。</summary>
    public static int Export(bool verbose)
    {
        UISpriteToolUtil.EnsureFolder(Dir);

        var kinds = new List<AmmoKind>();
        foreach (AmmoKind k in System.Enum.GetValues(typeof(AmmoKind)))
            if (k != AmmoKind.None) kinds.Add(k);          // None = RPG 那种"不吃弹药"，没有图标

        var sprites = new Dictionary<AmmoKind, Sprite>();
        int written = 0;
        foreach (var kind in kinds)
        {
            string path = Dir + "/Ammo_" + kind + ".png";

            // 形状代码的**唯一实现**在 ItemVisuals（这里不重画）。
            // 拿到的贴图是新建的、不入缓存 → 写完 PNG 必须自己销毁，否则每次导出都漏一张 64×64。
            var tex = ItemVisuals.MakeAmmoTexture(kind);
            if (tex == null) { Debug.LogWarning("[弹药图标导出] " + kind + " 画不出贴图，跳过。"); continue; }
            try
            {
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(tex); }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ApplyImportSettings(path);

            var sp = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sp == null) Debug.LogWarning("[弹药图标导出] " + path + " 导入后取不到 Sprite（请看导入设置）。");
            else { sprites[kind] = sp; written++; }
        }

        int wired = WireCards(sprites);
        AssetDatabase.SaveAssets();
        if (verbose)
            Debug.Log("[弹药图标导出] 写出 " + written + " 张 PNG 到 " + Dir + "；回填了 " + wired + " 张弹药卡的 icon 字段。\n"
                    + "运行时显示的图标与之前逐像素相同；`ItemVisuals` 里的形状代码已保留作兜底（没填 icon 的卡行为不变）。");
        return wired;
    }

    static int CountExisting()
    {
        int n = 0;
        foreach (AmmoKind k in System.Enum.GetValues(typeof(AmmoKind)))
            if (k != AmmoKind.None && System.IO.File.Exists(Dir + "/Ammo_" + k + ".png")) n++;
        return n;
    }

    /// <summary>设导入参数。
    /// ⚠️ 没有复用 `UISpriteToolUtil.EnsureSpriteAsset`：那个是给"纯 UI 显示"用的（且"已存在就跳过"），
    ///    而这里需要**多两项**（Read-Write / 不压缩 —— 描边生成要读像素、压缩会在描边边缘出噪点），
    ///    且导出器要能**覆盖**。两处参数若将来趋同，再合并。</summary>
    static void ApplyImportSettings(string path)
    {
        var imp = AssetImporter.GetAtPath(path) as TextureImporter;
        if (imp == null) return;

        // ⚠️ `meshType`（SpriteMeshType）不在 TextureImporter 的普通属性上，只在 `TextureImporterSettings` 里
        //    → 必须两段式：Read（拿到当前值）→ 改 → Set（写回）。
        //    ⚠️ 顺序：必须先做这段、再设下面那些普通属性 —— 因为 `SetTextureSettings` 会把 settings 里的**旧值**一并写回，
        //    反过来的话 textureType / alphaIsTransparency 等会被它覆盖掉。
        // ⚠️ FullRect 对小图是必须的：否则 Unity 按 alpha 裁紧网格，图标边缘会被削。
        var settings = new TextureImporterSettings();
        imp.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        imp.SetTextureSettings(settings);

        imp.textureType = TextureImporterType.Sprite;
        imp.spriteImportMode = SpriteImportMode.Single;
        imp.spritePixelsPerUnit = 100f;      // 与 Sprite.Create 的默认一致 → 各处显示大小零变化
        imp.alphaIsTransparency = true;
        imp.mipmapEnabled = false;
        imp.filterMode = FilterMode.Bilinear;
        imp.wrapMode = TextureWrapMode.Clamp;
        imp.npotScale = TextureImporterNPOTScale.None;
        imp.isReadable = true;               // ⚠️ 「膨胀描边」要 GetPixels，必须可读
        imp.textureCompression = TextureImporterCompression.Uncompressed;   // ⚠️ 压缩会让描边边缘出噪点
        imp.maxTextureSize = 64;             // 图就是 64×64，不用更大
        imp.SaveAndReimport();
    }

    /// <summary>把导出的图标回填到**所有**同口径弹药卡上
    /// （一级/二级/三级以后共用同一张图 —— 等级靠描边色区分，所以按 `ammoKind` 匹配、不按 tier）。</summary>
    static int WireCards(Dictionary<AmmoKind, Sprite> sprites)
    {
        int n = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:AmmoData", new[] { "Assets/Items" }))
        {
            var ammo = AssetDatabase.LoadAssetAtPath<AmmoData>(AssetDatabase.GUIDToAssetPath(guid));
            if (ammo == null) continue;

            Sprite sp;
            if (!sprites.TryGetValue(ammo.ammoKind, out sp) || sp == null) continue;
            if (ammo.icon == sp) continue;                 // 已经是它就别动（避免制造无意义的资源差异）

            ammo.icon = sp;
            EditorUtility.SetDirty(ammo);
            n++;
        }
        return n;
    }
}
