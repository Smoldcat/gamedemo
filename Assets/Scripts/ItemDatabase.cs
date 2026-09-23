using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 物品清单（大白话：**食堂门口的菜单目录**）：只列「我们有哪些物品」，**不装任何物品字段**。
///
/// 它解决什么：
///   · 一个地方看全所有物品（管理 / 查重 / 排查漏配）
///   · 将来要「从所有物品里随机抽」时（随机宝箱 / 空投）有地方可抽
///   · 编辑器里做下拉选择时不用一个个拖
///
/// ⚠️ 它**必须自动生成**：菜单 `Tools/游戏/重建物品清单` 一扫，把 Assets/Items 下所有物品定义收进来。
///    手动维护的清单一定会漏（加了新物品忘了拖 → 那个物品在任何「随机池」里都不存在，而且不报错）。
///
/// ⚠️ 清单**不承载物品数据**：名字 / 图标 / 能不能堆叠都在**物品定义自己**身上（<see cref="ItemData"/>）。
///    如果把字段都堆进清单，清单会变成一个巨大的 if-else，加一种新物品就要改清单结构 —— 那又回到「写死类型」的老坑。
///
/// 放在 `Assets/Resources/` 下 → 运行时可按需加载（和 `GameAssets` 同一套做法）。
/// ⚠️ 它是「便利表」而不是「必需品」：缺失时 `I` 返回 null，调用方判空即可，游戏照常跑。
/// </summary>
[CreateAssetMenu(fileName = "ItemDatabase", menuName = "游戏/物品清单", order = 0)]
public class ItemDatabase : ScriptableObject
{
    /// <summary>Resources 下的加载路径（不含扩展名）</summary>
    public const string ResourcePath = "ItemDatabase";

    [Tooltip("全部物品定义（由菜单 Tools/游戏/重建物品清单 自动收集 —— **不要手拖**）")]
    public List<ItemData> all = new List<ItemData>();

    static ItemDatabase _instance;
    static bool _loaded;

    /// <summary>自动从 Resources 加载；没放清单时返回 null（调用方要判空 —— 清单只是便利表，缺了游戏照常跑）。</summary>
    public static ItemDatabase I
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                _instance = Resources.Load<ItemDatabase>(ResourcePath);
            }
            return _instance;
        }
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）：关闭 Reload Domain 时强制下次重新加载。
    /// ⚠️ 新增 static 必须登记到 StaticReset，否则会跨 Play 会话残留（§5.1 StaticReset 行）。</summary>
    internal static void ResetStatics()
    {
        _instance = null;
        _loaded = false;
    }

    /// <summary>按身份证找物品定义（找不到返回 null）。</summary>
    public ItemData Find(string stackId)
    {
        if (string.IsNullOrEmpty(stackId) || all == null) return null;
        for (int i = 0; i < all.Count; i++)
        {
            var d = all[i];
            if (d != null && d.StackId == stackId) return d;
        }
        return null;
    }

    /// <summary>取清单里**第一个指定类型**的物品定义（例：取"血包定义卡"）。
    /// 用途：世界里那些"只认类型、不认具体哪一张卡"的地方（血迹包 → 随便哪张 MedkitData 都行）。
    /// 静态入口（内部走 `I`，所以调用方不用先判空清单）；找不到返回 null
    /// （调用方**要判空并给告警**，不要静默什么都不做）。</summary>
    public static T FirstOfType<T>() where T : ItemData
    {
        var db = I;
        if (db == null || db.all == null) return null;
        for (int i = 0; i < db.all.Count; i++)
        {
            var t = db.all[i] as T;
            if (t != null) return t;
        }
        return null;
    }

    /// <summary>清单里有几件物品。</summary>
    public int Count => all != null ? all.Count : 0;
}
