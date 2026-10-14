#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 调试界面专用控件（**终端风：纯色 + 1px 线，不用任何运行时生成的贴图**）。
///
/// ⚠️ 与游戏 UI 的关系（2026-10-14 用户定：调试面板玩家看不到，以简洁与效率优先）：
///   本类**不引用** `BackpackPanel.FrameSprite/PanelSprite/CellColor` 等游戏视觉资源 ——
///   调试工具只依赖游戏的 **public 逻辑入口**，不依赖它的**视觉**资源（发布前删除更干净）。
///
/// 边框怎么来的：用 Unity **内置** 的九宫格白盒 `UI/Skin/Background.psd`（引擎共享资源）
///   + `Image.Type.Sliced` → 一个节点就画出 1px 描边，**零运行时纹理生成**
///   （若拿不到该内置图则退化为纯色块，不影响功能）。
///   ⚠️ 内置图是**共享资源**，绝不能 `Destroy`（交接文档 §4 #25 的教训）。
///
/// ⚠️ 本文件属于 `Assets/Debug/`：发布前整个文件夹删除，详见 README.md。
/// </summary>
internal static class DebugUI
{
    // ---- 配色（终端风：黑底绿字；黄=警告/低血，红=错误/危险）----
    public static readonly Color Bg = new Color(0f, 0f, 0f, 0.92f);          // 面板/常驻条底
    public static readonly Color BgSoft = new Color(0.06f, 0.11f, 0.08f, 1f);// 按钮底
    public static readonly Color Line = new Color(0.22f, 0.45f, 0.28f, 0.9f);// 1px 分隔线
    public static readonly Color Fg = new Color(0.58f, 1f, 0.66f);           // 主文字
    public static readonly Color Dim = new Color(0.30f, 0.56f, 0.37f);       // 次要文字
    public static readonly Color Bright = new Color(0.82f, 1f, 0.86f);       // 数值强调
    public static readonly Color Warn = new Color(1f, 0.85f, 0.35f);         // 警告 / 提示行
    public static readonly Color Error = new Color(1f, 0.45f, 0.40f);        // 错误 / 危险
    public static readonly Color TabOnBg = new Color(0.12f, 0.34f, 0.18f, 1f);

    static Sprite _box;   // 内置九宫格白盒（共享资源，不销毁）

    /// <summary>内置 1px 描边白盒；取不到就返回 null（控件退化为纯色块）。</summary>
    public static Sprite Box
    {
        get
        {
            if (_box == null) _box = Resources.GetBuiltinResource<Sprite>("UI/Skin/Background.psd");
            return _box;
        }
    }

    // ==================== 基础节点（锚点/轴心统一：父节点左上角） ====================

    public static RectTransform NewRect(Transform parent, string name, Vector2 size, Vector2 pos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        return rt;
    }

    /// <summary>纯色块（无贴图、无采样）。</summary>
    public static Image NewSolid(Transform parent, string name, Vector2 size, Vector2 pos, Color color)
    {
        var rt = NewRect(parent, name, size, pos);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = null;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>1px 横向分隔线（用极扁的纯色块实现，零贴图）。</summary>
    public static Image NewHSep(Transform parent, string name, float width, Vector2 pos)
    {
        return NewSolid(parent, name, new Vector2(width, 1f), pos, Line);
    }

    // 说明：原 `NewImage`（带 Sliced 贴图底）已在 2026-10-14 的终端风重构中删除 —— 没有调用方了。

    public static Text NewText(Transform parent, string name, Vector2 size, Vector2 pos,
                               int fontSize, Color color, TextAnchor anchor)
    {
        var rt = NewRect(parent, name, size, pos);
        var t = rt.gameObject.AddComponent<Text>();
        UIUtil.ApplyFont(t, UIUtil.Font);
        t.fontSize = fontSize;
        t.alignment = anchor;
        t.color = color;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.supportRichText = true;
        return t;
    }

    /// <summary>按钮：纯色底 + 内置 1px 描边；悬停/按下用 **Button 自带 ColorTint**（零额外代码）。</summary>
    public static Button NewButton(Transform parent, string name, string label, Vector2 size, Vector2 pos,
                                   int fontSize, Color textColor, System.Action onClick)
    {
        var rt = NewRect(parent, name, size, pos);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = Box;                 // 内置白盒（null 时为纯色块）
        img.type = Image.Type.Sliced;
        img.color = BgSoft;
        img.raycastTarget = true;

        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        var cb = btn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.6f, 1.6f, 1.6f, 1f);   // 提亮底 → 悬停反馈
        cb.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        cb.selectedColor = Color.white;
        cb.fadeDuration = 0.05f;
        btn.colors = cb;
        if (onClick != null) btn.onClick.AddListener(() => onClick());

        var txt = NewText(rt, "Label", size, Vector2.zero, fontSize, textColor, TextAnchor.MiddleCenter);
        var trt = txt.rectTransform;
        trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0.5f, 0.5f);
        trt.anchoredPosition = Vector2.zero;
        txt.text = label;
        return btn;
    }

    /// <summary>取按钮上的文字组件（改标签/颜色用）。</summary>
    public static Text LabelOf(Button b) => b != null ? b.GetComponentInChildren<Text>() : null;
}
#endif
