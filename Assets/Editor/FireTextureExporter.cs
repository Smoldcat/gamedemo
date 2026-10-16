using UnityEditor;
using UnityEngine;

/// <summary>
/// 火焰贴图 / 材质**导出工具**：菜单 `Tools/游戏/FX/导出火焰贴图`。
///
/// 为什么需要它：火焰贴图一直是 `FireVisual` 在**运行时**用代码画的（和弹药图标 / 圆环 / 半影 / 烟的 puff 同一套路）——
/// 编辑器里看不见、也没法换美术。本工具把那份形状代码**落成正式资源**：
///   · `Assets/Art/FX/火焰.png` —— 火舌贴图（程序生成）
///   · `Assets/Art/FX/火焰.mat` —— 用它 + 加法混合粒子 shader
///   然后把这份材质拖进 `Effect_火焰.asset` 的 `material` 槽即可
///   （**不拖也能跑**：卡上留空时用的是同一份形状 + 同样的兜底材质，只有"能不能在资源里替换"的差别）。
///
/// ⚠️ 形状只有一份实现：本工具**不重画**，直接调 `FireVisual.CreateTexture` / `CreateMaterial`。
/// ⚠️ 会**覆盖**同名文件（它就是"以程序形状为准"的导出器）→ 有二次确认；以后美术替换了正式图，**别再跑它**。
/// </summary>
public static class FireTextureExporter
{
    const string Dir = "Assets/Art/FX";
    const string PngName = "火焰.png";
    const string MatName = "火焰.mat";
    const int MenuOrder = 312;   // 与「重建物品清单」「导出弹药图标」等工具排在一起

    [MenuItem("Tools/游戏/FX/导出火焰贴图", false, MenuOrder)]
    static void ExportFromMenu()
    {
        string png = Dir + "/" + PngName, mat = Dir + "/" + MatName;
        bool exists = System.IO.File.Exists(png) || System.IO.File.Exists(mat);
        string tip = "将生成 / 覆盖：\n  " + png + "\n  " + mat + "\n\n"
                   + "（火焰贴图由 FireVisual 的程序形状生成；换过美术图的请先备份）\n\n继续？";
        if (exists && !EditorUtility.DisplayDialog("导出火焰贴图", tip, "导出", "取消")) return;
        Export();
    }

    // ==================== 外部图（AI 出图 / 美术给图）→ 可用的粒子贴图 ====================

    /// <summary>菜单入口：**选择一张外部火焰图**（AI 出图或美术给的），处理成可直接用的粒子贴图。</summary>
    [MenuItem("Tools/游戏/FX/导入外部火焰图（AI/美术）", false, MenuOrder + 1)]
    static void ImportExternalFromMenu()
    {
        string src = EditorUtility.OpenFilePanel("选择火焰图（png / jpg）", "", "png,jpg,jpeg");
        if (string.IsNullOrEmpty(src)) return;
        if (ImportExternal(src)) EditorUtility.DisplayDialog("导入火焰图", "完成，见 Console。", "好");
    }

    /// <summary>
    /// 把**外部生成的火焰图**处理成粒子贴图（2026-10-16 加，配合 AI 出图那批）：
    ///   · **按亮度自动裁到火焰外接框**（⭐ 主要目的之一：GLM-Image 会在右下角强制加「AI生成」水印，
    ///     而火苗通常够不到那个角落 → 裁边顺手把水印裁出画面。裁完仍残留的（火焰铺到角落那种）请换一张图）；
    ///   · **压黑背景**：黑点以下的像素 alpha 归零 —— 加法混合下"黑=不发光"，
    ///     所以灰底必须压掉，否则整块 quad 会糊上一层灰雾；
    ///   · **方形居中 + 缩到 size**（粒子 sprite 方形最省心；用**盒式降采样**避免缩小时的锯齿）；
    ///   · 落成 `Assets/Art/FX/&lt;名字&gt;.png` + 同名 `.mat`（加法混合，与程序版同一份材质实现）。
    /// ⚠️ **不覆盖**程序生成的那套（`火焰.png` / `火焰.mat`）—— 两套并存，卡上想用哪套就拖哪套。
    /// </summary>
    public static bool ImportExternal(string srcPath, string outName = "火焰_AI", int size = 256,
                                      float brightThreshold = 0.12f, float blackPoint = 0.14f)
    {
        if (string.IsNullOrEmpty(srcPath) || !System.IO.File.Exists(srcPath))
        {
            Debug.LogWarning("[火焰导入] 找不到源图：" + srcPath);
            return false;
        }

        var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try { src.LoadImage(System.IO.File.ReadAllBytes(srcPath)); }
        catch (System.Exception e) { Debug.LogWarning("[火焰导入] 读图失败：" + e.Message); Object.DestroyImmediate(src); return false; }

        Color[] sp = src.GetPixels();               // ⚠️ y=0 在**下**方
        int sw = src.width, sh = src.height;

        // ---- 0) 先抹掉**右下角的水印区**（GLM-Image 会强制加「AI生成」标签，位置固定在右下角）----
        //     放在找外接框**之前**：否则水印里的亮像素会把外接框往右下角拽（裁完仍带一角，实测第一版就是）。
        //     抹成纯黑 → 后面压黑点自然变透明。若你的图右下角有真实火焰内容，把这块比例调小或设为 0。
        int cw = Mathf.RoundToInt(sw * 0.16f), chh = Mathf.RoundToInt(sh * 0.13f);
        for (int y = 0; y < chh; y++)
            for (int x = sw - cw; x < sw; x++)
                if (x >= 0 && y >= 0) sp[y * sw + x] = new Color(0f, 0f, 0f, 1f);

        // ---- 1) 按亮度找外接框（用 max(r,g,b)：火焰的暗红也要算进去）----
        int x0 = sw, x1 = -1, y0 = sh, y1 = -1;
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                var c = sp[y * sw + x];
                float lum = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (lum <= brightThreshold) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        if (x1 < x0 || y1 < y0) { Debug.LogWarning("[火焰导入] 这张图里找不到亮于阈值的火焰像素。"); Object.DestroyImmediate(src); return false; }

        int fw = x1 - x0 + 1, fh = y1 - y0 + 1;
        int side = Mathf.CeilToInt(Mathf.Max(fw, fh) * 1.08f);     // 留一点边
        int cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        int sx = Mathf.Clamp(cx - side / 2, 0, Mathf.Max(0, sw - side));
        int sy = Mathf.Clamp(cy - side / 2, 0, Mathf.Max(0, sh - side));
        side = Mathf.Min(side, Mathf.Min(sw, sh));                 // 别越界

        // ---- 2) 盒式降采样到 size×size + 压黑点 ----
        var dst = new Texture2D(size, size, TextureFormat.RGBA32, false);
        dst.wrapMode = TextureWrapMode.Clamp;
        var outPx = new Color[size * size];
        for (int dy = 0; dy < size; dy++)
            for (int dx = 0; dx < size; dx++)
            {
                int bx0 = sx + dx * side / size, bx1 = Mathf.Max(bx0 + 1, sx + (dx + 1) * side / size);
                int by0 = sy + dy * side / size, by1 = Mathf.Max(by0 + 1, sy + (dy + 1) * side / size);
                float r = 0f, g = 0f, b = 0f; int n = 0;
                for (int y = by0; y < by1 && y < sh; y++)
                    for (int x = bx0; x < bx1 && x < sw; x++)
                    { var c = sp[y * sw + x]; r += c.r; g += c.g; b += c.b; n++; }
                if (n == 0) { outPx[dy * size + dx] = new Color(0, 0, 0, 0); continue; }
                r /= n; g /= n; b /= n;

                float lum = Mathf.Max(r, Mathf.Max(g, b));
                float a = Mathf.Clamp01((lum - blackPoint) / 0.06f);   // 黑点以下 → 0（加法混合里=不发光）
                outPx[dy * size + dx] = new Color(r, g, b, a);
            }
        dst.SetPixels(outPx);
        dst.Apply();

        // ---- 3) 落成资源 + 同款加法材质 ----
        UISpriteToolUtil.EnsureFolder(Dir);
        string pngPath = Dir + "/" + outName + ".png";
        try { System.IO.File.WriteAllBytes(pngPath, dst.EncodeToPNG()); }
        finally { Object.DestroyImmediate(dst); Object.DestroyImmediate(src); }
        AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);

        var imp = AssetImporter.GetAtPath(pngPath) as TextureImporter;
        if (imp != null)
        {
            imp.textureType = TextureImporterType.Default;
            imp.alphaIsTransparency = true;
            imp.mipmapEnabled = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
        }

        var loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
        var fresh = FireVisual.CreateMaterial(loaded);
        string matPath = Dir + "/" + outName + ".mat";
        var old = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (old != null) { EditorUtility.CopySerialized(fresh, old); Object.DestroyImmediate(fresh); }
        else AssetDatabase.CreateAsset(fresh, matPath);
        AssetDatabase.SaveAssets();

        Debug.Log(string.Format("[火焰导入] 完成：{0}（源图 {1}×{2}，火苗外接框 {3}×{4} → 方形裁切 {5}px → {6}×{6}）\n材质：{7}\n把这份材质拖进 `Effect_火焰.asset` 的 material 槽即可（原来的程序版还在，随时可换回）。",
                                pngPath, sw, sh, fw, fh, side, size, matPath));
        return true;
    }

    /// <summary>无弹窗入口（供脚本 / 自动化调用）。返回是否成功。</summary>
    public static bool Export()
    {
        // ---- 贴图：程序形状 → PNG（临时纹理用完即毁，别漏）----
        var tex = FireVisual.CreateTexture(128);
        if (tex == null) { Debug.LogWarning("[火焰导出] 画不出贴图。"); return false; }
        try
        {
            UISpriteToolUtil.EnsureFolder(Dir);
            System.IO.File.WriteAllBytes(Dir + "/" + PngName, tex.EncodeToPNG());
        }
        finally { Object.DestroyImmediate(tex); }

        AssetDatabase.ImportAsset(Dir + "/" + PngName, ImportAssetOptions.ForceUpdate);

        // ---- 导入设置：这是**粒子贴图**不是 Sprite → Default 类型（不要 Sprite 切图）；
        //      保留 mipmap（远处粒子更干净），Clamp 防边缘采样到另一侧 ----
        var imp = AssetImporter.GetAtPath(Dir + "/" + PngName) as TextureImporter;
        if (imp != null)
        {
            imp.textureType = TextureImporterType.Default;
            imp.alphaIsTransparency = true;
            imp.mipmapEnabled = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
        }

        var loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "/" + PngName);

        // ---- 材质：与运行时兜底同一份实现（加法粒子 shader + 这张贴图）----
        var fresh = FireVisual.CreateMaterial(loaded);
        string matPath = Dir + "/" + MatName;
        var old = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (old != null)
        {
            // 已存在 → **复制进去保住 GUID**（引用了它的卡不会断）
            EditorUtility.CopySerialized(fresh, old);
            Object.DestroyImmediate(fresh);
            fresh = old;
        }
        else AssetDatabase.CreateAsset(fresh, matPath);
        AssetDatabase.SaveAssets();

        Debug.Log("[火焰导出] 完成：\n  " + Dir + "/" + PngName + "\n  " + matPath
                + "\n把这份材质拖进 `Effect_火焰.asset` 的 material 槽即可（不拖也能跑，用的是同一份运行时兜底）。");
        return true;
    }
}
