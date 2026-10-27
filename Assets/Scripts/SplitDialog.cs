using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 拆分弹窗（2026-10-27）：遮罩 + 居中面板，滑块选 1 ~ 原数量-1。
/// 只拆进**同一容器**的空位（背包 → 背包空位；箱子 → 箱子已揭示空格）；没有空位时菜单里的「拆分」是灰的。
/// 层级 380：高于右键菜单（300），低于虚拟光标（400）。
/// </summary>
public class SplitDialog : MonoBehaviour
{
    static SplitDialog _inst;

    Canvas _canvas;
    RectTransform _panel;
    Text _titleText;
    Slider _slider;
    ItemRef _target;

    const float PanelW = 360f, PanelH = 220f;

    public static void Open(ItemRef target)
    {
        var stack = ItemMoveService.Peek(target);
        if (stack == null || stack.def == null || !stack.def.canStack || stack.count < 2) return;

        Ensure();
        _inst._target = target;
        _inst.gameObject.SetActive(true);

        _inst._slider.minValue = 1;
        _inst._slider.maxValue = stack.count - 1;
        _inst._slider.wholeNumbers = true;
        _inst._slider.value = Mathf.Max(1, stack.count / 2);
        _inst.UpdateValue();
    }

    public static void Close()
    {
        if (_inst != null && _inst.gameObject.activeSelf) _inst.gameObject.SetActive(false);
    }

    internal static void ResetStatics() => _inst = null;

    static void Ensure()
    {
        if (_inst != null) return;

        var canvas = UIUtil.NewOverlayCanvas("SplitDialog", 380);
        canvas.gameObject.AddComponent<GraphicRaycaster>();
        _inst = canvas.gameObject.AddComponent<SplitDialog>();
        _inst._canvas = canvas;

        // 遮罩（点击关闭）
        var mask = new GameObject("Mask", typeof(RectTransform), typeof(Image), typeof(Button));
        mask.transform.SetParent(canvas.transform, false);
        var mrt = mask.GetComponent<RectTransform>();
        mrt.anchorMin = Vector2.zero;
        mrt.anchorMax = Vector2.one;
        mrt.offsetMin = mrt.offsetMax = Vector2.zero;
        var mimg = mask.GetComponent<Image>();
        mimg.color = new Color(0f, 0f, 0f, 0.45f);
        mimg.raycastTarget = true;
        var mbtn = mask.GetComponent<Button>();
        mbtn.transition = Selectable.Transition.None;
        mbtn.onClick.AddListener(Close);

        // 面板
        var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(canvas.transform, false);
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(PanelW, PanelH);
        prt.anchoredPosition = Vector2.zero;
        var pimg = panel.GetComponent<Image>();
        pimg.sprite = UIUtil.RoundedRectSprite(128, 14f, 0f);   // 圆角填充（描边图内部透明）
        pimg.type = Image.Type.Sliced;
        pimg.color = UIUtil.PanelColor();
        _inst._panel = prt;

        // 标题（数量直接显示在标题里 —— 单独的大数字在 Overlay Canvas 里有渲染异常，2026-10-27）
        _inst._titleText = NewText(prt, "Title", "拆分物品", 22, Color.white, new Vector2(0f, PanelH * 0.5f - 34f));
        _inst._titleText.fontStyle = FontStyle.Bold;

        // 滑块
        _inst._slider = NewSlider(prt, new Vector2(0f, -8f), 280f, 22f);
        _inst._slider.onValueChanged.AddListener(_ => _inst.UpdateValue());

        // 拆分按钮
        var btnGo = new GameObject("SplitBtn", typeof(RectTransform), typeof(Image), typeof(Button));
        btnGo.transform.SetParent(prt, false);
        var brt = btnGo.GetComponent<RectTransform>();
        brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0f);
        brt.pivot = new Vector2(0.5f, 0f);
        brt.sizeDelta = new Vector2(128f, 40f);
        brt.anchoredPosition = new Vector2(0f, 18f);
        var bimg = btnGo.GetComponent<Image>();
        bimg.sprite = UIUtil.RoundedRectSprite(128, 10f, 0f);
        bimg.type = Image.Type.Sliced;
        bimg.color = new Color(0.2f, 0.34f, 0.28f, 0.98f);
        var btn = btnGo.GetComponent<Button>();
        btn.targetGraphic = bimg;
        btn.onClick.AddListener(() => { _inst.Confirm(); });

        var btnTxt = NewText(brt, "Txt", "拆分", 17, new Color(0.75f, 1f, 0.85f), Vector2.zero);
        btnTxt.rectTransform.anchorMin = Vector2.zero;
        btnTxt.rectTransform.anchorMax = Vector2.one;
        btnTxt.rectTransform.offsetMin = btnTxt.rectTransform.offsetMax = Vector2.zero;

        canvas.gameObject.SetActive(false);
    }

    void UpdateValue()
    {
        if (_titleText != null && _slider != null)
            _titleText.text = "拆分物品   " + Mathf.RoundToInt(_slider.value);
    }

    void Confirm()
    {
        int n = Mathf.RoundToInt(_slider.value);

        ItemRef? to = null;
        if (_target.container == ItemContainer.ChestSlot)
        {
            var chest = _target.chest;
            if (chest != null)
                for (int i = 0; i < chest.SlotCount; i++)
                    if (chest.CanPlaceAt(i) && chest.GetSlot(i) == null) { to = ItemRef.ChestSlot(chest, i); break; }
        }
        else
        {
            var bp = PlayerController.Instance != null ? PlayerController.Instance.backpack : null;
            if (bp != null)
            {
                int idx = bp.FirstFreeInventorySlot();
                if (idx >= 0) to = ItemRef.Inventory(idx);
            }
        }

        if (to.HasValue) ItemMoveService.Split(_target, to.Value, n);
        Close();
    }

    // ==================== 小工具 ====================

    static Text NewText(Transform parent, string name, string content, int size, Color color, Vector2 pos)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(PanelW - 24f, 40f);
        rt.anchoredPosition = pos;
        var txt = go.GetComponent<Text>();
        txt.font = UIUtil.Font;
        txt.fontSize = size;
        txt.color = color;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.text = content;
        txt.raycastTarget = false;
        return txt;
    }

    static Slider NewSlider(Transform parent, Vector2 pos, float width, float height)
    {
        var go = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, height);
        rt.anchoredPosition = pos;

        // Background
        var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(go.transform, false);
        var bgrt = bg.GetComponent<RectTransform>();
        bgrt.anchorMin = new Vector2(0f, 0.25f);
        bgrt.anchorMax = new Vector2(1f, 0.75f);
        bgrt.offsetMin = bgrt.offsetMax = Vector2.zero;
        var bgimg = bg.GetComponent<Image>();
        bgimg.sprite = UIUtil.RoundedRectSprite(128, 6f, 0f);
        bgimg.type = Image.Type.Sliced;
        bgimg.color = new Color(0.18f, 0.2f, 0.26f, 0.95f);

        // Fill
        var fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(go.transform, false);
        var fart = fillArea.GetComponent<RectTransform>();
        fart.anchorMin = new Vector2(0f, 0.25f);
        fart.anchorMax = new Vector2(1f, 0.75f);
        fart.offsetMin = new Vector2(6f, 0f);
        fart.offsetMax = new Vector2(-6f, 0f);

        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        var frt = fill.GetComponent<RectTransform>();
        frt.anchorMin = Vector2.zero;
        frt.anchorMax = Vector2.one;
        frt.offsetMin = frt.offsetMax = Vector2.zero;
        var fimg = fill.GetComponent<Image>();
        fimg.sprite = UIUtil.RoundedRectSprite(128, 6f, 0f);
        fimg.type = Image.Type.Sliced;
        fimg.color = new Color(0.35f, 0.65f, 0.95f, 0.95f);

        // Handle
        var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(go.transform, false);
        var hart = handleArea.GetComponent<RectTransform>();
        hart.anchorMin = Vector2.zero;
        hart.anchorMax = Vector2.one;
        hart.offsetMin = new Vector2(6f, 0f);
        hart.offsetMax = new Vector2(-6f, 0f);

        var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        var hrt = handle.GetComponent<RectTransform>();
        hrt.sizeDelta = new Vector2(18f, 24f);
        var himg = handle.GetComponent<Image>();
        himg.sprite = UIUtil.RoundedRectSprite(128, 9f, 0f);
        himg.type = Image.Type.Sliced;
        himg.color = new Color(0.9f, 0.95f, 1f, 1f);

        var slider = go.GetComponent<Slider>();
        slider.fillRect = frt;
        slider.handleRect = hrt;
        slider.targetGraphic = himg;
        slider.direction = Slider.Direction.LeftToRight;
        return slider;
    }
}
