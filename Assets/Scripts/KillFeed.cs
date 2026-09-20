using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 击杀反馈（Kill Feed）：击杀敌人时，在屏幕下方中央**并列**弹出「击杀图标 + 击杀对象名字」。
/// 每条存活固定时长（`GameAssets.killFeedDuration`，默认 2s）后淡出；时长内多次击杀就并列多条
/// （不限制条数：**新的一条加在最左侧、把已有的往右挤**，整组始终居中 —— 即"从左往右依次加入"）。
///
/// 设计要点：
/// - **零手动搭 UI**：运行时自建 Overlay Canvas + CanvasScaler（与准星/背包/飘字同一套参数），
///   所以下面的像素值都是「参考分辨率 1280×720」下的值，运行时按屏幕等比缩放 ——
///   不同分辨率下观感一致（这也是"按比例"的实现方式，与项目其它 UI 完全同源）。
/// - **数据驱动**：图标走 `GameAssets`（配了就用、留空用程序生成的占位），显示参数也都在 `GameAssets` 里可调。
/// - **动画**：从左滑入 → 停留 → 向右滑出 + 淡出（"从左到右"）。
/// - **触发**：`EnemyAI.OnDead`；**自杀不触发**（自爆兵自爆走 `Health.TakeDamage(creditKill: false)`）。
/// </summary>
public static class KillFeed
{
    const string PoolKey = "KillFeed";

    /// <summary>参考分辨率：与项目其它 UI 的 CanvasScaler 一致。
    /// 下面所有像素常量都是这个基准下的值，运行时由 CanvasScaler 按屏幕等比缩放。</summary>
    static readonly Vector2 RefResolution = new Vector2(1280f, 720f);

    const float BottomOffset = 96f;    // 整组距屏幕底部（参考分辨率像素）
    const float SlideDistance = 40f;   // 入场/消失的横向位移（参考分辨率像素）：左滑入、右滑出
    const float InDuration = 0.15f;    // 入场动画时长（秒）
    const float OutDuration = 0.30f;   // 消失动画时长（秒，含淡出）
    const float RelayoutSpeed = 900f;  // 其余条目重新居中时的挪动速度（参考分辨率像素/秒）

    internal static readonly Color NormalIconColor = new Color(1f, 0.92f, 0.35f);    // 普通击杀：金黄
    internal static readonly Color HeadshotIconColor = new Color(1f, 0.42f, 0.10f);  // 爆头击杀：橙红（与爆头飘字同色）
    internal static readonly Color NameColor = new Color(0.96f, 0.96f, 0.96f);

    internal static float Slide => SlideDistance;
    internal static float InDur => InDuration;
    internal static float OutDur => OutDuration;
    internal static float RelayoutSpd => RelayoutSpeed;

    static Canvas _canvas;
    static RectTransform _root;
    static readonly List<KillFeedItem> _live = new List<KillFeedItem>();
    static Sprite _fallbackNormal, _fallbackHeadshot;   // 程序生成的占位图标（ResetStatics 成对释放）

    // ---------- 对外入口 ----------

    /// <summary>显示一条击杀反馈。victimName = 被击杀对象的名字（EnemyConfig.displayName）。</summary>
    public static void Show(string victimName, bool headshot)
    {
        EnsureCanvas();

        var go = ObjectPool.Get(PoolKey);
        KillFeedItem item = go != null ? go.GetComponent<KillFeedItem>() : null;
        if (item != null)
        {
            item.transform.SetParent(_root, false);
        }
        else
        {
            if (go != null) Object.Destroy(go);   // 异常情况：池里取到的东西不是本类条目
            item = CreateItem();
        }

        var a = GameAssets.I;
        float iconSize = Mathf.Max(a != null ? a.killFeedIconSize : 64f, 8f);
        int nameSize = Mathf.Max(a != null ? a.killFeedNameFontSize : 20, 6);
        float duration = Mathf.Max(a != null ? a.killFeedDuration : 2f, 0.1f);

        item.SetContent(
            IconFor(headshot),
            string.IsNullOrEmpty(victimName) ? "敌人" : victimName,
            headshot ? HeadshotIconColor : NormalIconColor,
            iconSize, nameSize, duration);

        _live.Insert(0, item);   // 新条目加在**最左**：把已有的往右挤（整组仍居中）
        Relayout();
        item.Play();

        // 上限（0 = 不限，默认 0）。>0 时让**最旧的一条**（在最右）提前进入消失，避免铺满整条屏幕。
        int max = a != null ? a.killFeedMaxCount : 0;
        if (max > 0 && _live.Count > max)
            _live[_live.Count - 1].BeginOut();
    }

    /// <summary>静态状态清理（供 StaticReset 调用）</summary>
    internal static void ResetStatics()
    {
        _live.Clear();
        _canvas = null;
        _root = null;
        DestroySprite(_fallbackNormal); _fallbackNormal = null;
        DestroySprite(_fallbackHeadshot); _fallbackHeadshot = null;
    }

    // ---------- 布局 / 生命周期 ----------

    /// <summary>把所有条目横向排列在屏幕下方并**整体居中**（间距取 `GameAssets.killFeedSpacing`）。
    /// 列表顺序即从左到右：index 0 是最新的一条（在最左）。</summary>
    static void Relayout()
    {
        var a = GameAssets.I;
        float spacing = Mathf.Max(a != null ? a.killFeedSpacing : 16f, 0f);

        float total = 0f;
        for (int i = 0; i < _live.Count; i++) total += _live[i].Width;
        total += spacing * Mathf.Max(0, _live.Count - 1);

        float x = -total * 0.5f;
        for (int i = 0; i < _live.Count; i++)
        {
            var it = _live[i];
            it.TargetX = x + it.Width * 0.5f;
            x += it.Width + spacing;
        }
    }

    /// <summary>条目播放结束 → 回池，并让剩余条目重新居中</summary>
    internal static void Release(KillFeedItem item)
    {
        _live.Remove(item);
        ObjectPool.Release(PoolKey, item.gameObject);
        Relayout();
    }

    // ---------- Canvas / 条目构建 ----------

    static void EnsureCanvas()
    {
        if (_canvas != null) return;

        var go = new GameObject("KillFeedCanvas");
        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 150;   // HUD(0)/准星(100) 之上、背包(200)/飘字(300) 之下

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = RefResolution;
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        var root = new GameObject("KillFeedRoot");
        root.transform.SetParent(go.transform, false);
        _root = root.AddComponent<RectTransform>();
        _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0.5f, 0f);
        _root.anchoredPosition = new Vector2(0f, BottomOffset);
        _root.sizeDelta = Vector2.zero;

        UIUtil.SetLayerRecursively(go, UIUtil.UILayer);
    }

    static KillFeedItem CreateItem()
    {
        var go = new GameObject("KillFeedItem");
        go.transform.SetParent(_root, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0f);

        var cg = go.AddComponent<CanvasGroup>();
        cg.blocksRaycasts = false;
        cg.interactable = false;

        var iconGo = new GameObject("Icon");
        iconGo.transform.SetParent(go.transform, false);
        var iconRt = iconGo.AddComponent<RectTransform>();
        iconRt.anchorMin = iconRt.anchorMax = iconRt.pivot = new Vector2(0.5f, 0f);
        var icon = iconGo.AddComponent<Image>();
        icon.raycastTarget = false;
        icon.preserveAspect = true;

        var nameGo = new GameObject("Name");
        nameGo.transform.SetParent(go.transform, false);
        var nameRt = nameGo.AddComponent<RectTransform>();
        nameRt.anchorMin = nameRt.anchorMax = nameRt.pivot = new Vector2(0.5f, 0f);
        var name = nameGo.AddComponent<Text>();
        name.font = UIUtil.Font;
        name.alignment = TextAnchor.MiddleCenter;
        name.color = NameColor;
        name.raycastTarget = false;
        name.horizontalOverflow = HorizontalWrapMode.Overflow;
        name.verticalOverflow = VerticalWrapMode.Overflow;

        var item = go.AddComponent<KillFeedItem>();
        item.Bind(icon, name, cg, iconRt, nameRt, rt);

        UIUtil.SetLayerRecursively(go, UIUtil.UILayer);
        return item;
    }

    // ---------- 图标（GameAssets 优先，留空回落程序生成占位） ----------

    static Sprite IconFor(bool headshot)
    {
        var a = GameAssets.I;
        if (a != null)
        {
            var s = headshot ? a.killHeadshotIcon : a.killIcon;
            if (s != null) return s;
        }
        if (headshot)
        {
            if (_fallbackHeadshot == null) _fallbackHeadshot = MakeBullseyeIcon(64);
            return _fallbackHeadshot;
        }
        if (_fallbackNormal == null) _fallbackNormal = MakeStarIcon(64);
        return _fallbackNormal;
    }

    /// <summary>程序生成的普通击杀占位图标：四角星芒（白色，颜色由 Image.color 上）</summary>
    static Sprite MakeStarIcon(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = Mathf.Abs(x - c) / c;
                float v = Mathf.Abs(y - c) / c;
                // 四角星（星形线）：sqrt(|u|)+sqrt(|v|) <= 1 为实心，尖端落在坐标轴上
                float f = Mathf.Sqrt(u) + Mathf.Sqrt(v);
                float a = Mathf.Clamp01((1f - f) / 0.10f);   // 边缘羽化，避免锯齿
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    /// <summary>程序生成的爆头击杀占位图标：靶心（外环 + 中心点）</summary>
    static Sprite MakeBullseyeIcon(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;   // 0..1
                float ring = Mathf.Clamp01((0.74f - Mathf.Abs(d - 0.80f)) / 0.10f);  // 外圈
                float dot = Mathf.Clamp01((0.22f - d) / 0.10f);                       // 中心点
                float a = Mathf.Max(ring, dot);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    /// <summary>销毁运行时生成的 Sprite 及其纹理（两者独立，必须成对销毁，见交接文档 §4 #25）</summary>
    static void DestroySprite(Sprite s)
    {
        if (s == null) return;
        var tex = s.texture;
        Object.Destroy(s);
        if (tex != null) Object.Destroy(tex);
    }
}

/// <summary>
/// 击杀反馈的单条（图标在上、名字在下）。由 `KillFeed` 创建/池化，自己驱动动画与计时
/// （`SetActive(false)` 回池即停，复用语义与项目其它池化对象一致）。
/// </summary>
public class KillFeedItem : MonoBehaviour
{
    Image _icon;
    Text _name;
    CanvasGroup _cg;
    RectTransform _iconRt, _nameRt, _rt;

    float _width = 64f;
    float _targetX;
    float _baseX;        // 布局基准 x：平滑追 _targetX（其余条目重新居中时"平滑挪动"）
    float _lifetime = 2f;
    float _t;

    /// <summary>本条目的宽度（图标/名字取较宽者），供 `KillFeed` 做居中的横向排列</summary>
    public float Width => _width;

    /// <summary>本条目的目标 x（由 `KillFeed` 的居中布局算出）</summary>
    public float TargetX { get => _targetX; set => _targetX = value; }

    internal void Bind(Image icon, Text name, CanvasGroup cg, RectTransform iconRt, RectTransform nameRt, RectTransform rt)
    {
        _icon = icon; _name = name; _cg = cg; _iconRt = iconRt; _nameRt = nameRt; _rt = rt;
    }

    /// <summary>设置内容与尺寸（每次 Show 都会重设）</summary>
    internal void SetContent(Sprite sprite, string label, Color iconColor, float iconSize, int nameSize, float lifetime)
    {
        _lifetime = lifetime;

        _icon.sprite = sprite;
        _icon.color = iconColor;
        _iconRt.sizeDelta = new Vector2(iconSize, iconSize);

        _name.text = label;
        _name.fontSize = nameSize;
        _name.color = KillFeed.NameColor;

        // 名字高度按字号估算；宽度取「图标 / 名字」较宽者（名字测不出来时退化为图标宽）
        float nameH = nameSize * 1.3f;
        float prefW = _name.preferredWidth;
        float w = Mathf.Max(iconSize, prefW > 1f ? prefW : 0f);
        _width = w + 8f;

        float totalH = iconSize + 4f + nameH;
        _rt.sizeDelta = new Vector2(_width, totalH);
        _iconRt.anchoredPosition = new Vector2(0f, totalH - iconSize * 0.5f);   // 图标在上
        _nameRt.sizeDelta = new Vector2(_width, nameH);
        _nameRt.anchoredPosition = new Vector2(0f, nameH * 0.5f);               // 名字在下
    }

    /// <summary>开始播放：计时归零，基准位置直接落在最终位置（入场只有 -Slide 的横向偏移）</summary>
    internal void Play()
    {
        _t = 0f;
        _baseX = _targetX;
        Apply();
    }

    /// <summary>立即进入消失阶段（用于"超出最大条数时挤掉最早那条"）</summary>
    internal void BeginOut()
    {
        _t = Mathf.Max(_t, _lifetime - KillFeed.OutDur);
    }

    void OnEnable()
    {
        // ⚠️ 判空：首次 AddComponent 时会先触发 OnEnable，此时还没 Bind
        _t = 0f;
        if (_cg != null) _cg.alpha = 0f;
    }

    void Update()
    {
        if (_lifetime <= 0f) return;
        _t += Time.deltaTime;   // 暂停（timeScale=0）时 deltaTime=0 → 计时自动冻结

        // 其余条目重新居中 → 基准位置平滑挪过去（约 0.15s 挪完），不瞬间跳
        _baseX = Mathf.MoveTowards(_baseX, _targetX, KillFeed.RelayoutSpd * Time.deltaTime);

        Apply();

        if (_t >= _lifetime) KillFeed.Release(this);
    }

    void Apply()
    {
        if (_rt == null) return;

        float inK = Mathf.Clamp01(_t / KillFeed.InDur);                                   // 0 → 1
        float outStart = Mathf.Max(_lifetime - KillFeed.OutDur, KillFeed.InDur);
        float outK = Mathf.Clamp01((_t - outStart) / KillFeed.OutDur);                    // 0 → 1

        // 「从左到右」：入场从左侧滑入到位，消失继续向右滑出
        float x = _baseX - KillFeed.Slide * (1f - inK) + KillFeed.Slide * outK;
        _rt.anchoredPosition = new Vector2(x, 0f);

        // 入场淡入 + 消失淡出
        if (_cg != null) _cg.alpha = Mathf.Clamp01(Mathf.Min(inK, 1f - outK));
    }
}
