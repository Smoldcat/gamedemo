using UnityEngine;
using UnityEditor;

/// <summary>
/// 编辑器「UI 预置工具」的**共用小工具**（`PickupBubbleBuilder` / `HotbarBuilder` 用同一份）。
///
/// 为什么单独抽出来（2026-09-22）：这类工具都要做同样三件事 ——
/// 把"运行时才生成的 Sprite"落成正式 PNG 资源、把新节点递归设到 UI 层、给占位文字取编辑期预览字体。
/// 抄到每个工具里就会出现 N 份同源代码，改一处漏一处（同 §2.6 的"只留一份实现"）。
/// </summary>
public static class UISpriteToolUtil
{
    /// <summary>按需创建（多级）目录，例如 `Assets/Art/UI/Hotbar`</summary>
    public static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
        string leaf = System.IO.Path.GetFileName(folder);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return;
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    /// <summary>
    /// 把一个运行时生成的 Sprite 落成正式 PNG 资源（**已存在就复用**，不覆盖你调过的导入设置），
    /// 并设好「Sprite + 九宫格 border + 透明 + 无 mipmap」。
    /// 失败返回 null → 调用方退回"运行时由代码自动补图"，不会中断生成。
    /// </summary>
    /// <param name="dir">目标目录，如 `Assets/Art/UI/Hotbar`</param>
    /// <param name="fileName">文件名，如 `HotbarSlotBg.png`</param>
    /// <param name="runtime">运行时生成的临时 Sprite（用来导出 PNG）</param>
    /// <param name="border">九宫格 border（拉伸圆角不变形）</param>
    public static Sprite EnsureSpriteAsset(string dir, string fileName, Sprite runtime, Vector4 border)
    {
        string path = dir + "/" + fileName;
        try
        {
            if (!System.IO.File.Exists(path))
            {
                if (runtime == null || runtime.texture == null) return null;
                EnsureFolder(dir);
                System.IO.File.WriteAllBytes(path, runtime.texture.EncodeToPNG());
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }

            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null)
            {
                imp.textureType = TextureImporterType.Sprite;
                imp.spriteImportMode = SpriteImportMode.Single;
                imp.spriteBorder = border;
                imp.alphaIsTransparency = true;
                imp.mipmapEnabled = false;
                imp.SaveAndReimport();
            }
            else AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[UISpriteToolUtil] 生成贴图 " + fileName + " 失败：会退回\"运行时自动补图\"。" + e.Message);
            return null;
        }
    }

    /// <summary>递归设置 GameObject 及其子节点的 Layer（编辑器程序集**取不到** `UIUtil.UILayer`，它只有运行时那份）</summary>
    public static void SetLayerRecursively(GameObject go, int layer)
    {
        if (go == null) return;
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++)
            SetLayerRecursively(go.transform.GetChild(i).gameObject, layer);
    }

    /// <summary>取内置的 UI 层索引（取不到就退回 5）</summary>
    public static int UILayerIndex()
    {
        int l = LayerMask.NameToLayer("UI");
        return l >= 0 ? l : 5;
    }

    /// <summary>
    /// 编辑期预览字体：优先用「资源总表」里配的**界面字体**（`GameAssets.uiFont`）——
    /// 于是编辑期看到的就和运行时**完全一致**（含中文）；没配才回落内置占位字体（无中文）。
    /// ⚠️ 运行时各 UI 也会把字体统一注入为 `UIUtil.Font`（同样优先读 `GameAssets.uiFont`），所以两者同源。
    /// </summary>
    public static Font PreviewFont()
    {
        var assets = GameAssets.I;
        if (assets != null && assets.uiFont != null) return assets.uiFont;

        var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return f;
    }
}
