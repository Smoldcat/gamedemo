using UnityEditor;
using UnityEngine;

/// <summary>
/// 模型自发光材质自动修正（只作用于 Assets/Art/Models 下的模型，不碰工程其它内容）。
///
/// 背景：Blender 导出的 FBX 只带 EmissiveColor，Unity 导入后材质默认不开 _EMISSION，
/// 红点镜片 / 全息视窗 / 4 倍镜目镜 / 激光指示灯就不会发光。这里在模型导入完成后，
/// 把名字里带 "Emis" 的材质自动打开 Emission 并设置自发光颜色（= 基础色 × 2.5）。
/// </summary>
public class WeaponEmissiveMaterialFixer : AssetPostprocessor
{
    const string ModelRoot = "/Art/Models/";
    const float EmissionBoost = 2.5f;

    static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
                                       string[] movedAssets, string[] movedFromAssetPaths)
    {
        foreach (var p in importedAssets)
        {
            var path = p.Replace('\\', '/');
            if (!path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (path.IndexOf(ModelRoot, System.StringComparison.Ordinal) < 0) continue;

            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var mat = obj as Material;
                if (mat == null || mat.name.IndexOf("Emis", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!mat.HasProperty("_EmissionColor")) continue;

                // 已经开过就不重复处理（避免反复写 dirty）
                if (mat.IsKeywordEnabled("_EMISSION") &&
                    mat.GetColor("_EmissionColor").maxColorComponent > 0.001f)
                    continue;

                Color c = mat.HasProperty("_Color") ? mat.color
                        : mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor")
                        : Color.white;
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                mat.SetColor("_EmissionColor", c * EmissionBoost);
                EditorUtility.SetDirty(mat);
            }
        }
    }
}
