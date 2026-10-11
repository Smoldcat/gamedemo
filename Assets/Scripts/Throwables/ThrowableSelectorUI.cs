using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 投掷物选择器的**运行时自建 UI**（快捷栏上方一条纵列：名字 + 数量、当前项高亮）。
///
/// 为什么单独成类（2026-10-12 重构）：`PlayerThrowable` 原来同时扛"状态机 + 输入 + 渲染 + 这套 UI"；
/// 把**建/显隐/重绘**搬到这里，主类只管"开没开、超时、高亮第几个"。**行为一字未变**（纯搬移）。
///
/// ⚠️ 分工：**状态在 `PlayerThrowable`**（`_selectorOpen` / `_selectorCloseAt` / `_selected`），
///    本类只按传入的 `visible / 列表 / 当前项` 画 —— 所以"暂停时要关掉"必须由主类显式传 `visible=false`。
/// ⚠️ 自建画布（排序 60）：不依赖场景 HUD、也不受"HUD 隐藏"影响（这正是要显式 Hide 的原因）。
/// ⚠️ 行数按需增长（`EnsureRows`），只增不减；内容签名不变时**一个字节都不写 UI**（避免 Canvas 重建）。
/// </summary>
public class ThrowableSelectorUI
{
    RectTransform _root;
    Canvas _canvas;
    readonly List<Image> _rowsBg = new List<Image>(8);
    readonly List<Text> _rowsText = new List<Text>(8);
    int _sig = int.MinValue;      // 内容签名：种类 / 数量 / 高亮 变了才重建行

    /// <summary>按内容刷新并显示 / 隐藏（每帧调一次；`visible = false` 就收起）。
    /// `countOf` = 某个种类的持有数量（由主类提供，**派生值**：直接数库存）。</summary>
    public void Render(bool visible, IList<ThrowableData> owned, ThrowableData selected, System.Func<ThrowableData, int> countOf)
    {
        if (!visible) { Hide(); return; }
        if (_root == null) Build();
        if (_root == null) return;
        _root.gameObject.SetActive(true);

        int sig = owned.Count * 397;
        for (int i = 0; i < owned.Count; i++)
            sig = sig * 31 + owned[i].DisplayName.GetHashCode() + countOf(owned[i]) * 7;
        sig = sig * 31 + (selected != null ? selected.DisplayName.GetHashCode() : 0);
        if (sig == _sig) return;
        _sig = sig;

        EnsureRows(owned.Count);

        var a = GameAssets.I;
        Color bg = a != null ? a.throwableListBgColor : new Color(0f, 0f, 0f, 0.55f);
        Color hi = a != null ? a.throwableListHiColor : new Color(1f, 0.85f, 0.35f, 0.9f);
        float rowH = a != null ? Mathf.Max(16f, a.throwableListRowH) : 24f;
        float w = a != null ? Mathf.Max(80f, a.throwableListWidth) : 230f;

        for (int i = 0; i < _rowsText.Count; i++)
        {
            bool on = i < owned.Count;
            if (_rowsBg[i] != null) _rowsBg[i].gameObject.SetActive(on);
            if (_rowsText[i] == null) continue;
            _rowsText[i].gameObject.SetActive(on);
            if (!on) continue;

            bool sel = owned[i] == selected;
            _rowsBg[i].color = sel ? hi : bg;
            _rowsText[i].color = sel ? new Color(0.08f, 0.08f, 0.1f) : new Color(0.92f, 0.92f, 0.95f);
            _rowsText[i].text = owned[i].DisplayName + "  ×" + countOf(owned[i]);

            float y = i * (rowH + 2f);
            var rb = _rowsBg[i].rectTransform;
            rb.sizeDelta = new Vector2(w, rowH);
            rb.anchoredPosition = new Vector2(0f, y);
            var rt = _rowsText[i].rectTransform;
            rt.sizeDelta = new Vector2(w - 12f, rowH);
            rt.anchoredPosition = new Vector2(0f, y);
        }
    }

    /// <summary>收起（不销毁；下次 Render(true) 直接复用）。</summary>
    public void Hide()
    {
        if (_root != null) _root.gameObject.SetActive(false);
    }

    /// <summary>强制下次 Render 重建行（打开选择器时调：内容可能变了但签名恰好没变）。</summary>
    public void Invalidate()
    {
        _sig = int.MinValue;
    }

    /// <summary>销毁自建画布（在 `OnDestroy` 里调一次）。</summary>
    public void Dispose()
    {
        if (_canvas != null) Object.Destroy(_canvas.gameObject);
    }

    void Build()
    {
        _canvas = UIUtil.NewOverlayCanvas("投掷物选择器（运行时）", 60);
        if (_canvas == null) return;
        var rootGo = new GameObject("Root", typeof(RectTransform));
        rootGo.transform.SetParent(_canvas.transform, false);
        _root = rootGo.GetComponent<RectTransform>();
        // 锚在屏幕下方居中、往上排（"槽上方一条纵列"的观感）
        _root.anchorMin = new Vector2(0.5f, 0f);
        _root.anchorMax = new Vector2(0.5f, 0f);
        _root.pivot = new Vector2(0.5f, 0f);
        _root.anchoredPosition = new Vector2(0f, 96f);
        UIUtil.SetLayerRecursively(_canvas.gameObject, UIUtil.UILayer);
    }

    void EnsureRows(int n)
    {
        var a = GameAssets.I;
        float rowH = a != null ? Mathf.Max(16f, a.throwableListRowH) : 24f;
        float w = a != null ? Mathf.Max(80f, a.throwableListWidth) : 230f;
        int font = a != null ? Mathf.Max(8, a.throwableListFontSize) : 15;

        while (_rowsBg.Count < n)
        {
            var bgGo = new GameObject("Row" + _rowsBg.Count, typeof(RectTransform));
            bgGo.transform.SetParent(_root, false);
            var img = bgGo.AddComponent<Image>();
            img.raycastTarget = false;
            _rowsBg.Add(img);

            var txGo = new GameObject("Txt", typeof(RectTransform));
            txGo.transform.SetParent(_root, false);
            var tx = txGo.AddComponent<Text>();
            UIUtil.ApplyFont(tx, UIUtil.Font);
            tx.alignment = TextAnchor.MiddleCenter;
            tx.raycastTarget = false;
            _rowsText.Add(tx);
        }

        for (int i = 0; i < _rowsText.Count; i++)
        {
            if (_rowsText[i] != null) _rowsText[i].fontSize = font;
            if (_rowsBg[i] != null) _rowsBg[i].rectTransform.sizeDelta = new Vector2(w, rowH);
        }
    }
}
