using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 全项目统一的「物品格网格」度量（背包 / 商店货架 / 商店库存 / 箱子共用，**只留一份**）。
///
/// 为什么收敛到一处（用户 2026-10-13 定）：格宽 / 间隙 / 列数此前在 BackpackPanel / ShopUI / ChestPanel
/// 各有一份等值拷贝，改一处忘两处就会出现"格子大小不一致"。各面板用 const 别名引用这里的值
/// （编译期内联，零运行时开销）；真要调格子大小，只改这一个文件，四处一起变。
/// </summary>
internal static class InvGridUI
{
    public const float CellW = 68f, CellH = 68f, CellGap = 6f;   // 格子 68×68，间隙 6
    public const int Cols = 5;                                    // 每行 5 格
    public const float GridW = Cols * CellW + (Cols - 1) * CellGap;   // 5 列网格总宽（364）

    /// <summary>给 slots 个格子算行数（向上取整，至少 1 行）。</summary>
    public static int RowsFor(int slots) => Mathf.Max(1, Mathf.CeilToInt(slots / (float)Cols));

    /// <summary>
    /// 物品格右下角的「数量角标」（背包库存 / 商店库存面板共用，**只留一份** —— 2026-10-13 收敛，
    /// 此前两处各写一份：字号 12 vs 11、对齐 MiddleRight vs LowerRight、定位算法都不同）。
    /// 只创建不填字：调用方自己写 text / SetActive（数量 >1 才显示）。
    /// </summary>
    public static Text CreateCountBadge(RectTransform cell, float cellW)
    {
        var go = new GameObject("Count");
        go.transform.SetParent(cell, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);   // 锚在格子右下角
        rt.sizeDelta = new Vector2(cellW - 8f, 14f);
        rt.anchoredPosition = new Vector2(-3f, 2f);
        var t = go.AddComponent<Text>();
        UIUtil.ApplyFont(t, UIUtil.Font);
        t.fontSize = 11;
        t.alignment = TextAnchor.LowerRight;
        t.color = Color.white;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        return t;
    }
}

/// <summary>
/// 可滚动网格：RectMask2D 裁剪 + 滚轮平滑滚动 —— **只此一份实现**（背包库存 / 商店库存面板共用）。
///
/// 2026-10-13 三连修的教训全部沉淀在这里，后来者不要绕开它：
///  · **绝不改动网格本体（grid）的 transform** —— prefab 坐标系一动就全乱（首版重挂父级导致
///    库存格子叠到快捷武器区上）。裁剪靠挂在 grid 上的 RectMask2D；
///    滚动 = 平移调用方用 <see cref="AddMover"/> 登记的节点（格子、价格行……）的 y。
///  · **全部**格子都要登记，包括空格子 —— 背包的 <c>_invCells</c> 给空格子占位 null（tooltip
///    靠它判断"格上有没有物品"），拿它滚动会出现"只有带物品的格子动、空格子掉队"。
///  · 滚轮必须由**面板自己的 Update** 里调 <see cref="Tick"/> 读（面板开着 = 暂停态，
///    PlayerController.Update 早已 early-return；且暂停下 Time.deltaTime = 0 → 平滑必须用
///    unscaledDeltaTime）。
///  · 坐标约定：basePos 是"未滚动"的锚定坐标；滚动量 cur ∈ [0, max]（0 = 顶部），
///    实际 y = basePos.y + cur —— 内容向上平移露出下方内容，对中心锚定（背包）与
///    顶部锚定（商店）的网格都成立（Unity 的 y+ 恒为上）。
/// </summary>
public class ScrollableGrid
{
    RectTransform _grid;   // 裁剪视口 = 网格本体（只挂 mask，不动它的 transform）
    readonly List<RectTransform> _movers = new List<RectTransform>();   // 随滚动平移的节点
    readonly List<Vector2> _base = new List<Vector2>();                 // 与 _movers 平行的基准位置
    float _scrollTarget, _scrollCur, _scrollMax;

    /// <summary>内容是否超出可视区（没超出时 Tick 是 no-op）。</summary>
    public bool Scrollable { get; private set; }

    const float ScrollStep = 60f;     // 每格滚轮滚动距离（px）
    const float ScrollSmooth = 12f;   // 平滑跟随系数（越大越快追上目标）

    /// <summary>登记网格并幂等挂 RectMask2D。可视高**不在这里记** —— 每次 <see cref="Sync"/> 时
    /// 实时读 grid.rect.height，面板运行中改网格尺寸（如商店面板按容量收缩）也能正确重算滚动范围。</summary>
    public void Attach(RectTransform grid)
    {
        if (grid == null) return;
        _grid = grid;
        if (grid.GetComponent<RectMask2D>() == null)
            grid.gameObject.AddComponent<RectMask2D>();   // 纯裁剪、不挡点击、不需要贴图
    }

    /// <summary>滚动归零（面板 Open 时调；每次打开都从顶开始）。</summary>
    public void ResetToTop()
    {
        _scrollTarget = _scrollCur = 0f;
        Apply();
    }

    /// <summary>Refresh 重建格子前调：清空上一轮登记（格子是全量重建的，旧引用已销毁）。</summary>
    public void ClearMovers()
    {
        _movers.Clear();
        _base.Clear();
    }

    /// <summary>登记一个"随滚动平移"的节点及其基准位置（每个格子 / 价格行各登记一次）。</summary>
    public void AddMover(RectTransform rt, Vector2 basePos)
    {
        if (rt == null) return;
        _movers.Add(rt);
        _base.Add(basePos);
    }

    /// <summary>Refresh 末尾调：按当前内容高 / 可视高重算滚动范围，夹住偏移并立即套用
    /// （平时 cur = 0；滚动中 Refresh 完保持位置不跳回顶）。</summary>
    public void Sync(float contentH)
    {
        float viewH = _grid != null ? _grid.rect.height : 0f;
        Scrollable = contentH > viewH + 0.5f;
        _scrollMax = Mathf.Max(0f, contentH - viewH);
        _scrollTarget = Mathf.Clamp(_scrollTarget, 0f, _scrollMax);
        _scrollCur = Mathf.Clamp(_scrollCur, 0f, _scrollMax);
        Apply();
    }

    /// <summary>面板 Update 里调（面板自己判断开着才调）：读滚轮 + 平滑跟随 + 套偏移。</summary>
    public void Tick()
    {
        if (!Scrollable || _movers.Count == 0) return;

        float dy = Input.mouseScrollDelta.y;
        if (dy != 0f) _scrollTarget = Mathf.Clamp(_scrollTarget - dy * ScrollStep, 0f, _scrollMax);
        _scrollCur = Mathf.Lerp(_scrollCur, _scrollTarget, 1f - Mathf.Exp(-ScrollSmooth * Time.unscaledDeltaTime));
        if (Mathf.Abs(_scrollCur - _scrollTarget) < 0.3f) _scrollCur = _scrollTarget;
        Apply();
    }

    void Apply()
    {
        int n = Mathf.Min(_movers.Count, _base.Count);
        for (int i = 0; i < n; i++)
        {
            if (_movers[i] == null) continue;   // 防御：理论上不该出现
            _movers[i].anchoredPosition = _base[i] + new Vector2(0f, _scrollCur);
        }
    }
}
