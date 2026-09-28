using System.IO;
using UnityEngine;
using UnityEditor;

/// <summary>
/// **导出魔法图标**占位图（白色剪影 PNG）×2 → 自动填进对应魔法卡的 `icon` 字段。
/// 菜单：`Tools/游戏/导出魔法图标`（有二次确认；<see cref="Export"/> 供脚本无弹窗调用）。
///
/// 为什么要有它（与 `AmmoIconExporter` 完全同一套理由）：
///   · "占位图标"如果只在运行时用代码画，**编辑器里看不见**、也没法换美术、更没法在上面叠描边
///   · 导出成正式 PNG 资产后：卡上直接引用 → 编辑器里看得见、美术随时替换、运行时零成本
///
/// ⚠️ **输出是纯白剪影**（颜色仍由卡上的 `pickupColor` 给）——
///    这样"改颜色"是改卡上一个值（立刻全场生效），而不是重导图片。
///
/// ⚠️ 形状的**唯一实现**在本文件的两个 `MakeXxxTexture()` 里；弹药那边同理（`ItemVisuals.MakeAmmoTexture`）。
///    以后要改成美术图：直接把 PNG 拖进 `Assets/Art/UI/Magic/` 覆盖同名文件即可（**别再跑本菜单**）。
/// </summary>
public static class MagicIconExporter
{
    const string Dir = "Assets/Art/UI/Magic";   // 与弹药图标（Assets/Art/UI/Ammo）分开，便于美术按类替换
    const int Size = 64;

    [MenuItem("Tools/游戏/导出魔法图标")]
    static void ExportMenu()
    {
        if (!EditorUtility.DisplayDialog("导出魔法图标",
            "将把 2 张占位图标写到：\n" + Dir + "/\n" +
            "（Magic_Orb.png = 圆球 / Magic_Bolt.png = 闪电）\n\n" +
            "并**覆盖**同名文件、自动填进魔法卡的 icon 字段。\n\n" +
            "⚠️ 如果美术已经替换过正式图标，请点「取消」（否则会被占位形状盖回去）。",
            "导出", "取消"))
            return;

        int n = Export(false);
        EditorUtility.DisplayDialog("导出魔法图标", $"完成：处理了 {n} 张卡。", "好");
    }

    /// <summary>导出并回填。返回**被回填的魔法卡数量**。<paramref name="ask"/> 保留给将来扩展（当前无用）。</summary>
    public static int Export(bool ask)
    {
        EnsureFolder();

        WritePng("Magic_Orb", MakeOrbTexture());
        WritePng("Magic_Bolt", MakeBoltTexture());

        AssetDatabase.Refresh();
        return Backfill();
    }

    // ==================== 写文件 + 导入参数 ====================

    static void EnsureFolder()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Art/UI"))
            AssetDatabase.CreateFolder("Assets/Art", "UI");
        if (!AssetDatabase.IsValidFolder(Dir))
            AssetDatabase.CreateFolder("Assets/Art/UI", "Magic");
    }

    static void WritePng(string name, Texture2D tex)
    {
        string path = Dir + "/" + name + ".png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        var imp = AssetImporter.GetAtPath(path) as TextureImporter;
        if (imp == null) return;

        // ⚠️ `spriteMeshType`（FullRect）**只能在 TextureImporterSettings 里设**，
        //    且这段**必须放在设其它属性之前**：`SetTextureSettings` 会把 settings 里的旧值一并写回，
        //    顺序反了会覆盖掉下面设的 textureType / alphaIsTransparency / 压缩等等（踩过）。
        var settings = new TextureImporterSettings();
        imp.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect;   // ⚠️ 小图必须：否则按 alpha 裁紧网格、边缘被削
        imp.SetTextureSettings(settings);

        imp.textureType = TextureImporterType.Sprite;
        imp.spriteImportMode = SpriteImportMode.Single;
        imp.spritePixelsPerUnit = 100f;          // 与程序生成 / 弹药图标一致 → 各处显示大小零变化
        imp.alphaIsTransparency = true;
        imp.isReadable = true;                   // 以后若要给魔法图标叠描边（像弹药那样），需要读像素
        imp.mipmapEnabled = false;
        imp.filterMode = FilterMode.Bilinear;
        imp.wrapMode = TextureWrapMode.Clamp;
        imp.textureCompression = TextureImporterCompression.Uncompressed;   // 压缩会让细描边出噪点
        imp.SaveAndReimport();
    }

    /// <summary>把导出的图回填到所有魔法卡的 `icon`（按"卡需要的形状"匹配，而不是按文件名硬编码）。</summary>
    static int Backfill()
    {
        var orb = AssetDatabase.LoadAssetAtPath<Sprite>(Dir + "/Magic_Orb.png");
        var bolt = AssetDatabase.LoadAssetAtPath<Sprite>(Dir + "/Magic_Bolt.png");
        int n = 0;

        foreach (var guid in AssetDatabase.FindAssets("t:MagicData", new[] { "Assets/Items" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var card = AssetDatabase.LoadAssetAtPath<MagicData>(path);
            if (card == null) continue;

            // ⭐ 形状按**行为类型**选，而不是按卡的名字 —— 以后复制卡/改名都不会填错
            var t = card.BehaviourType;
            Sprite want = null;
            if (t != null && typeof(ProjectileGroupMagic).IsAssignableFrom(t)) want = orb;
            else if (t != null && typeof(ConditionMagic).IsAssignableFrom(t)) want = bolt;

            if (want == null || card.icon == want) continue;
            card.icon = want;
            EditorUtility.SetDirty(card);
            n++;
        }

        if (n > 0) AssetDatabase.SaveAssets();
        return n;
    }

    // ==================== 形状（唯一实现；替换美术时整段可以不管） ====================

    /// <summary>圆球：径向渐变（中心亮 → 边缘略暗）+ 左上高光点。白色 + alpha 剪影。</summary>
    static Texture2D MakeOrbTexture()
    {
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        var px = new Color32[Size * Size];
        float c = (Size - 1) * 0.5f;
        float r = Size * 0.42f;                       // 球半径
        float hi = r * 0.42f;                          // 高光半径
        Vector2 hiPos = new Vector2(c - r * 0.38f, c + r * 0.38f);

        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                // 边缘 1.5px 抗锯齿
                float a = Mathf.Clamp01((r - d) / 1.5f);
                if (a <= 0f) { px[y * Size + x] = new Color32(255, 255, 255, 0); continue; }

                // 径向明暗：中心 1.0 → 边缘 0.72（做出球体感）
                float shade = Mathf.Lerp(1f, 0.72f, Mathf.Clamp01(d / r));
                float dh = Mathf.Sqrt((x - hiPos.x) * (x - hiPos.x) + (y - hiPos.y) * (y - hiPos.y));
                if (dh < hi) shade = Mathf.Lerp(shade, 1.35f, Mathf.Clamp01((hi - dh) / 1.5f));   // 高光

                byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(shade * 255f), 0, 255);
                px[y * Size + x] = new Color32(v, v, v, (byte)Mathf.RoundToInt(a * 255f));
            }

        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    /// <summary>闪电：经典折线多边形（点在多边形内 = 实心）。白色 + alpha 剪影。</summary>
    static Texture2D MakeBoltTexture()
    {
        // 归一化坐标（0~1，y 向上）—— 改形状只需要调这 6 个点
        Vector2[] poly =
        {
            new Vector2(0.58f, 0.97f),
            new Vector2(0.22f, 0.47f),
            new Vector2(0.44f, 0.47f),
            new Vector2(0.30f, 0.03f),
            new Vector2(0.78f, 0.56f),
            new Vector2(0.52f, 0.56f),
        };
        for (int i = 0; i < poly.Length; i++) poly[i] *= Size;   // 换到像素空间（原点左下，与纹理坐标一致）

        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        var px = new Color32[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool inside = PointInPolygon(new Vector2(x + 0.5f, y + 0.5f), poly);
                px[y * Size + x] = inside
                    ? new Color32(255, 255, 255, 255)
                    : new Color32(255, 255, 255, 0);
            }
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    /// <summary>射线法判定"点是否在多边形内"（闪电用）。形状简单、调用量小，不需要空间优化。</summary>
    static bool PointInPolygon(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        int n = poly.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        }
        return inside;
    }
}
