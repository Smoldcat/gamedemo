using UnityEditor;
using UnityEngine;

/// <summary>
/// 音频导入设置的一键工具（2026-09-25 新增）。
///
/// 为什么需要它：**短音效**与**长音**（音乐 / 环境）的导入参数是两个方向，而 Unity 的默认值对
/// "一秒内可能连发多次的枪声"恰好是最差的一档 —— `Vorbis` 解码开销 + 不预载 → 连发时能听出延迟。
/// 手点 17 个文件 × 5 个下拉框既慢又容易漏，所以收敛成一处、可反复执行。
///
/// 📌 **目录约定（= 本工具的判断依据，别在别处再写一份）**：
///   <code>
///   Assets/Art/Audio/
///   ├─ Weapons/ Combat/ Player/ Reload/ UI/ Enemies/   → 短音效：Decompress On Load + PCM + 预载
///   ├─ Music/                                          → 长音：Streaming + Vorbis + 不预载
///   └─ Ambience/                                       → 长音（同上）
///   </code>
///   ⚠️ 新增音频类别时：短音放上面那一档的目录里就自动被正确设置；确实需要另一档再改本文件（**只改这一处**）。
///
/// ⚠️ 本工具**只改导入设置、不动音频文件本身**，且**幂等**：值已经正确时直接跳过、不 `SaveAndReimport`
///    （否则每次执行都会把全部音频重新导入一遍，既慢又可能脏化资源 —— 同 `EnemyConfig.OnValidate` 那条约定）。
/// </summary>
public static class AudioImportTool
{
    const string Root = "Assets/Art/Audio";

    /// <summary>长音（音乐 / 环境）的目录名：命中即用 Streaming + Vorbis</summary>
    static readonly string[] LongFormFolders = { "/Music/", "/Ambience/" };

    [MenuItem("Tools/游戏/音频导入设置/按目录约定应用到 Art-Audio", false, 300)]
    public static void ApplyByConvention()
    {
        var guids = AssetDatabase.FindAssets("t:AudioClip", new[] { Root });
        int shortCount = 0, longCount = 0, skipped = 0;

        foreach (var g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            bool longForm = IsLongForm(path);
            if (Apply(path, longForm)) { if (longForm) longCount++; else shortCount++; }
            else skipped++;
        }

        Debug.Log($"[AudioImportTool] 完成：短音效改了 {shortCount} 个、长音改了 {longCount} 个、本来就是对的跳过 {skipped} 个" +
                  $"（共 {guids.Length} 个，目录 {Root}）。" +
                  "⚠️ 导入设置生效无需改代码：下次播放即是新设置。");
    }

    [MenuItem("Tools/游戏/音频导入设置/应用到当前选中的音频文件", false, 301)]
    public static void ApplyToSelection()
    {
        int n = 0;
        foreach (var o in Selection.objects)
        {
            string path = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(path) || !(o is AudioClip)) continue;
            if (Apply(path, false)) n++;   // 手动指定的一律按"短音效"处理（需要长音档就走目录约定那条）
        }
        Debug.Log($"[AudioImportTool] 选中项：按「短音效」档改了 {n} 个。");
    }

    /// <summary>是不是长音（音乐 / 环境）：按目录约定判断</summary>
    static bool IsLongForm(string path)
    {
        for (int i = 0; i < LongFormFolders.Length; i++)
            if (path.IndexOf(LongFormFolders[i], System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>把某个音频设成目标档位；返回 true = 真的改了（false = 本来就是这个设置，跳过）</summary>
    static bool Apply(string path, bool longForm)
    {
        var imp = AssetImporter.GetAtPath(path) as AudioImporter;
        if (imp == null) return false;

        var want = imp.defaultSampleSettings;
        want.loadType = longForm ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
        want.compressionFormat = longForm ? AudioCompressionFormat.Vorbis : AudioCompressionFormat.PCM;
        // ⚠️ `preloadAudioData` 在本版本已从 `AudioImporter` **迁移进 `AudioImporterSampleSettings`**
        //    （直接在 AudioImporter 上读写会报 CS0619 已过时）→ 统一从 sampleSettings 走。
        want.preloadAudioData = !longForm;   // 短音效预载：避免第一次开枪卡一下；长音不预载（省内存）
        if (longForm) want.quality = 0.7f;

        var cur = imp.defaultSampleSettings;
        bool changed = cur.loadType != want.loadType
                       || cur.compressionFormat != want.compressionFormat
                       || cur.preloadAudioData != want.preloadAudioData
                       || imp.loadInBackground
                       || (longForm && !Mathf.Approximately(cur.quality, want.quality));
        if (!changed) return false;

        imp.defaultSampleSettings = want;
        imp.loadInBackground = false;
        imp.SaveAndReimport();
        return true;
    }
}
