using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 物品清单的**一键重建**工具：菜单 `Tools/游戏/重建物品清单`。
///
/// 为什么是「手动点一下」而不是「每次自动扫」：
///   · 全自动要在每次打开工程时扫全项目（慢）；
///   · 全自动会「偷偷改资源文件」（跟版本管理打架、也容易在没保存时产生意外差异）；
///   · 项目里已有的三个工具（生成快捷栏 / 生成拾取气泡 / 地图尺寸）都是「你点一下，它做一次」——保持一致。
/// 成本：只在**新增了物品之后**点一次，一秒的事。
///
/// 顺手做质检（这三样都不会报错，但都会让玩家困惑）：重复身份证 / 没图标 / 没写说明。
/// </summary>
public static class ItemDatabaseBuilder
{
    const string AssetPath = "Assets/Resources/ItemDatabase.asset";

    /// <summary>扫描哪些目录（「物品定义卡」都放这里）。以后新分类只要往这个列表加一行。</summary>
    static readonly string[] ScanRoots = { "Assets/Items" };

    [MenuItem("Tools/游戏/重建物品清单")]
    static void Rebuild()
    {
        // ---- 1) 找清单资源；没有就建一个（放 Resources 下，运行时可加载）----
        var db = AssetDatabase.LoadAssetAtPath<ItemDatabase>(AssetPath);
        bool created = false;
        if (db == null)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
            db = ScriptableObject.CreateInstance<ItemDatabase>();
            AssetDatabase.CreateAsset(db, AssetPath);
            created = true;
        }

        // ---- 2) 扫出所有「物品定义」（按**类型**过滤，不靠命名约定 —— 以后随便改名都不会漏）----
        var found = new List<ItemData>();
        var guids = AssetDatabase.FindAssets(string.Empty, ScanRoots);
        for (int i = 0; i < guids.Length; i++)
        {
            string p = AssetDatabase.GUIDToAssetPath(guids[i]);
            var d = AssetDatabase.LoadAssetAtPath<ItemData>(p);
            if (d != null && !found.Contains(d)) found.Add(d);
        }
        found.Sort((a, b) => string.CompareOrdinal(Key(a), Key(b)));

        // ---- 3) **全量重建**（不是追加）→ 不会留残留 ----
        db.all = found;
        EditorUtility.SetDirty(db);
        AssetDatabase.SaveAssets();

        // ---- 4) 质检报告 ----
        var sb = new StringBuilder();
        sb.Append("[ItemDatabase] 已重建物品清单：").Append(found.Count).Append(" 件");
        if (created) sb.Append("（清单资源为新建：").Append(AssetPath).Append("）");
        sb.Append('\n');

        int noIcon = 0, noDesc = 0, dupId = 0;
        var ids = new Dictionary<string, int>();
        for (int i = 0; i < found.Count; i++)
        {
            var d = found[i];
            if (d.icon == null) noIcon++;
            if (string.IsNullOrEmpty(d.description)) noDesc++;

            string id = d.StackId;
            if (ids.ContainsKey(id)) { ids[id] = ids[id] + 1; dupId++; }
            else ids[id] = 1;

            sb.Append("  · ").Append(d.GetType().Name).Append("  ").Append(d.DisplayName)
              .Append("   id=").Append(id)
              .Append(d.canStack ? ("   可堆叠 ×" + d.MaxStack) : "   不可堆叠")
              .Append('\n');
        }
        if (dupId > 0) sb.Append("⚠️ 有 ").Append(dupId).Append(" 件物品的「身份证」重复 —— 叠加时会被混成一格，请给它们手填 stackId\n");
        if (noIcon > 0) sb.Append("· ").Append(noIcon).Append(" 件没配图标（会显示自动生成的占位图标）\n");
        if (noDesc > 0) sb.Append("· ").Append(noDesc).Append(" 件没写说明文字（悬停 tooltip 会是空的）\n");
        Debug.Log(sb.ToString());
    }

    /// <summary>排序键：先按类型、再按资源名（保证每次重建的顺序稳定，diff 不会乱跳）。</summary>
    static string Key(ItemData d) => d.GetType().Name + ":" + d.name;
}
