using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 物品右键菜单（2026-10-27）：深色 + 描边，竖排在物品右侧；超出屏幕右边缘自动翻到左侧。
/// 按钮由 <see cref="ItemActions.Build"/> 决定（使用 / 装备 / 卸载子弹 / 拆分 / 丢弃 / 拿取）。
///
/// 层级 300：高于背包 / 箱子（200），低于拖拽图标（350）与虚拟光标（400）。
/// 关闭方式：点按钮、点空白遮罩、开始拖拽、关闭面板。
/// </summary>
public class ItemContextMenu : MonoBehaviour
{
    static ItemContextMenu _inst;

    Canvas _canvas;
    RectTransform _panel;
    GameObject _mask;
    readonly List<GameObject> _items = new List<GameObject>();

    const float W = 132f, ItemH = 42f, Gap = 4f, Pad = 6f;

    public static bool IsOpen => _inst != null && _inst.gameObject.activeSelf;

    public static void Open(ItemRef target, Vector2 screenPos)
    {
        Ensure();
        // ⚠️ 必须先激活再定位：Canvas 未激活时 RectTransform 还没布局，ScreenPointToLocalPointInRectangle 会失败
        _inst.gameObject.SetActive(true);
        // 没有可用操作（未揭示格 / 空槽）→ 不打开空菜单（2026-10-28 审查 A-6）
        if (_inst.Build(target) <= 0)
        {
            _inst.gameObject.SetActive(false);
            return;
        }
        _inst.Place(screenPos);
    }

    public static void Close()
    {
        if (_inst != null && _inst.gameObject.activeSelf) _inst.gameObject.SetActive(false);
    }

    /// <summary>静态清理（供 `StaticReset`；Canvas 随场景销毁，这里只清引用）。</summary>
    internal static void ResetStatics() => _inst = null;

    static void Ensure()
    {
        if (_inst != null) return;

        var canvas = UIUtil.NewOverlayCanvas("ItemContextMenu", 300);
        canvas.gameObject.AddComponent<GraphicRaycaster>();   // ⚠️ 可交互画布必须显式加（项目踩坑 §4）
        _inst = canvas.gameObject.AddComponent<ItemContextMenu>();
        _inst._canvas = canvas;

        // 全屏透明遮罩：点空白关闭
        var mask = new GameObject("Mask", typeof(RectTransform), typeof(Image), typeof(Button));
        mask.transform.SetParent(canvas.transform, false);
        var mrt = mask.GetComponent<RectTransform>();
        mrt.anchorMin = Vector2.zero;
        mrt.anchorMax = Vector2.one;
        mrt.offsetMin = mrt.offsetMax = Vector2.zero;
        var mimg = mask.GetComponent<Image>();
        mimg.color = new Color(0f, 0f, 0f, 0f);   // 完全透明但仍接射线
        mimg.raycastTarget = true;
        var mbtn = mask.GetComponent<Button>();
        mbtn.transition = Selectable.Transition.None;
        mbtn.onClick.AddListener(Close);
        _inst._mask = mask;

        // 菜单面板
        var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(canvas.transform, false);
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = new Vector2(0f, 1f);   // 左上
        prt.pivot = new Vector2(0f, 1f);
        var pimg = panel.GetComponent<Image>();
        pimg.sprite = UIUtil.RoundedRectSprite(128, 14f, 0f);   // ⚠️ 纯描边图内部透明 → 必须用"圆角填充"
        pimg.type = Image.Type.Sliced;
        pimg.color = UIUtil.PanelColor();
        pimg.raycastTarget = false;   // 面板本身不挡（按钮自己接）
        _inst._panel = prt;

        canvas.gameObject.SetActive(false);
    }

    int Build(ItemRef target)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i] != null) Destroy(_items[i]);
        _items.Clear();

        var actions = ItemActions.Build(target);
        int count = actions.Count;
        float h = Pad * 2f + actions.Count * ItemH + Mathf.Max(0, actions.Count - 1) * Gap;
        _panel.sizeDelta = new Vector2(W, h);

        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];

            var go = new GameObject("Btn" + i, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(_panel, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(W - Pad * 2f, ItemH);
            rt.anchoredPosition = new Vector2(0f, -Pad - i * (ItemH + Gap));

            var img = go.GetComponent<Image>();
            img.sprite = UIUtil.RoundedRectSprite(128, 10f, 0f);
            img.type = Image.Type.Sliced;
            img.color = a.enabled
                ? new Color(0.16f, 0.18f, 0.24f, 0.98f)
                : new Color(0.11f, 0.12f, 0.15f, 0.9f);

            var txtGo = new GameObject("Txt", typeof(RectTransform), typeof(Text));
            txtGo.transform.SetParent(go.transform, false);
            var trt = txtGo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            var txt = txtGo.GetComponent<Text>();
            txt.font = UIUtil.Font;
            txt.fontSize = 15;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.raycastTarget = false;
            txt.text = a.label;
            txt.color = a.enabled ? a.color : new Color(0.42f, 0.42f, 0.46f);

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.interactable = a.enabled;
            var act = a.run;
            btn.onClick.AddListener(() => { act?.Invoke(); Close(); });

            _items.Add(go);
        }

        return count;
    }

    void Place(Vector2 screenPos)
    {
        var canvasRt = _canvas != null ? _canvas.transform as RectTransform : null;
        if (canvasRt == null || _panel == null) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, screenPos, null, out var local)) return;

        float halfW = canvasRt.rect.width * 0.5f;
        float halfH = canvasRt.rect.height * 0.5f;
        float h = _panel.sizeDelta.y;

        float x = local.x + 16f;
        if (x + W > halfW) x = local.x - W - 16f;           // 贴右边缘 → 翻到左侧

        float y = local.y - 8f;                             // 出现在鼠标下方
        if (y - h < -halfH) y = -halfH + h + 8f;            // 贴底 → 往上抬

        // anchor 左上 + pivot 左上：anchoredPosition = 面板左上角相对父左上角（y 向下为负）
        _panel.anchoredPosition = new Vector2(x + halfW, y - halfH);
    }
}
