using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 港口模型的**材质统一 + 颜色保持**工具。
///
/// 背景（这两个坑都是实测踩出来的）：
/// 1) FBX 默认会给**每个模型各生成一份内嵌材质**（同名但不同资产）⇒「改一个颜色只改一个模块」、
///    而且同色不同材质**无法合批**。⇒ 导入前把 importer 设成"按名字全局查找"，
///    让所有模型都指到 <c>Assets/Art/Models/Port/Materials/&lt;名&gt;.mat</c>。
/// 2) ⚠️ **重导 FBX 会把这个模型用到的共享材质的属性覆盖回 FBX 里的值**（= 变成灰白），
///    而且**重命名/重导后重映射也可能失效**。⇒ 所以提供菜单命令，一键"重映射 + 重刷颜色"。
///
/// 📌 **任何时候重导/重命名/新增了 `Assets/Art/Models/Port/` 下的模型，点一次这个菜单即可。**
/// 菜单：**工具 → 港口场景 - 修复模型材质与颜色**
/// </summary>
public class PortModelMaterialFixer : AssetPostprocessor
{
    // ⚠️ 2026-10-17：整批港口资源已归档到「临时场景模型1017」（用户要求把这次生成的所有模型集中存放）。
    //    改这里时**只需改这两行**（菜单命令与导入钩子都读它们）。
    const string PortRoot = "Assets/Art/Models/临时场景模型1017";
    const string MatDir   = "Assets/Art/Models/临时场景模型1017/Materials/";

    /// <summary>材质名|R|G|B（与 Blender 里的一致）。⚠️ 改颜色请改这里，然后点菜单。</summary>
    static readonly string[] Pal =
    {
        "concrete_hi|0.600|0.620|0.635", "concrete_md|0.475|0.500|0.515",
        "concrete_lo|0.330|0.350|0.362", "concrete_ed|0.290|0.303|0.312",
        "water|0.105|0.285|0.335",       "warn_yellow|0.870|0.660|0.020",
        "steel|0.395|0.420|0.435",       "steel_dk|0.235|0.250|0.262",
        "rust|0.430|0.315|0.245",        "rust_dk|0.285|0.205|0.155",
        "wood_hi|0.610|0.435|0.260",     "wood_md|0.500|0.345|0.200",
        "wood_lo|0.375|0.250|0.140",
        "cont_red|0.600|0.175|0.135",    "cont_red_d|0.400|0.105|0.085",
        "cont_blue|0.140|0.305|0.480",   "cont_blue_d|0.090|0.205|0.345",
        "lamp_glow|1.000|0.930|0.700",   "inner_dark|0.180|0.185|0.190",
        "wh_panel|0.520|0.560|0.600",    "wh_panel_d|0.395|0.430|0.470",
        "wh_frame|0.285|0.300|0.315",    "wh_roof|0.455|0.475|0.500",
        "cement|0.555|0.555|0.535",      "cement_d|0.430|0.430|0.412",
        "rack_orange|0.720|0.350|0.105", "rack_blue|0.165|0.310|0.470",
        "hull_red|0.430|0.155|0.120",    "hull_red_d|0.300|0.100|0.080",
        "tank_white|0.680|0.690|0.680",  "tank_green|0.310|0.430|0.330",
        "pipe_yellow|0.760|0.560|0.080", "rubber|0.135|0.135|0.140",
        "rubber_hi|0.215|0.215|0.225",   "sandbag|0.545|0.495|0.360",
        "sandbag_d|0.430|0.385|0.275",   "tarp_blue|0.175|0.290|0.430",
        "tarp_green|0.255|0.365|0.290",  "fork_yellow|0.820|0.620|0.070",
    };

    void OnPreprocessModel()
    {
        if (!assetPath.Replace('\\', '/').StartsWith(PortRoot)) return;
        var mi = assetImporter as ModelImporter;
        if (mi == null) return;
        mi.materialName   = ModelImporterMaterialName.BasedOnMaterialName;
        mi.materialSearch = ModelImporterMaterialSearch.Everywhere;
    }

    [MenuItem("工具/港口场景 - 修复模型材质与颜色")]
    public static void FixAll()
    {
        var files = System.IO.Directory.GetFiles(PortRoot, "*.fbx", System.IO.SearchOption.AllDirectories);

        // ① 先把所有模型重映射到共享材质（重导会覆盖共享材质属性，所以刷色必须放在最后）
        for (int i = 0; i < files.Length; i++)
        {
            string p = files[i].Replace('\\', '/');
            var mi = AssetImporter.GetAtPath(p) as ModelImporter;
            if (mi == null) continue;
            mi.SearchAndRemapMaterials(ModelImporterMaterialName.BasedOnMaterialName,
                                       ModelImporterMaterialSearch.Everywhere);
            mi.SaveAndReimport();
        }

        // ② 最后统一刷颜色
        int n = ApplyColors();
        AssetDatabase.SaveAssets();
        Debug.Log($"[港口] 材质已修复：{files.Length} 个模型重映射，{n} 个共享材质刷新颜色。");
    }

    static int ApplyColors()
    {
        int n = 0;
        for (int i = 0; i < Pal.Length; i++)
        {
            var parts = Pal[i].Split('|');
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatDir + parts[0] + ".mat");
            if (mat == null) continue;

            var c = new Color(float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]));
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", parts[0].Contains("steel") ? 0.42f : 0.10f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", parts[0].Contains("steel") ? 0.55f : 0.05f);
            EditorUtility.SetDirty(mat);
            n++;
        }
        return n;
    }
}
