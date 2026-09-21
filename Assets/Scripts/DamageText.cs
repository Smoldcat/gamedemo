using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 伤害飘字工具（静态）。所有伤害显示集中从这里走：
/// - 优先用 GameAssets 资源表里配置的「伤害飘字 Prefab」（可完全自定义样式）
/// - 留空则用代码生成默认文字（黄=打敌人、红=被打、绿=回血、橙红=爆头，大伤害字号更大）
/// - 走对象池复用，避免高频开火时反复创建/销毁
/// 与 Effects/Sfx 同模式：Prefab 优先 + 代码兜底。
/// </summary>
public static class DamageText
{
    public enum Kind { DamageToEnemy, DamageToPlayer, Heal, Headshot }

    const string PoolKey = "DamageText";
    static Canvas _canvas;
    // 字体已改用 UIUtil 的**全局共用字体**（原来这里也自己造一份动态字体，见 UIUtil 的注释）

    /// <summary>静态状态清理（供 StaticReset 调用）：关闭 Reload Domain 时避免指向已销毁的 Canvas/字体</summary>
    internal static void ResetStatics()
    {
        _canvas = null;
        // ⚠️ 字体是 UIUtil 的**共用资源**，由 `UIUtil.ResetStatics` 统一释放，这里不要碰
    }

    static readonly Color EnemyColor = new Color(1f, 0.92f, 0.3f);
    static readonly Color PlayerColor = new Color(1f, 0.32f, 0.25f);
    static readonly Color HealColor = new Color(0.45f, 1f, 0.55f);
    static readonly Color HeadshotColor = new Color(1f, 0.42f, 0.10f);   // 爆头：醒目橙红（与「打敌黄」「被打红」都区分得开）

    static void EnsureCanvas()
    {
        // 场景重载后旧 Canvas 被销毁（Unity 会把已销毁对象判为 null），这里自动重建
        if (_canvas == null)
        {
            var go = new GameObject("DamageTextCanvas");
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 300;   // 高于 HUD/准星，飘字永远可见
            go.layer = UIUtil.UILayer;    // 统一 UI 层（Overlay 下不影响渲染）
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
        }
    }

    /// <summary>在指定世界坐标生成一个伤害飘字。worldPos 请传「头顶/身上」的最终位置。</summary>
    public static void Spawn(Vector3 worldPos, float amount, Kind kind)
    {
        EnsureCanvas();

        var assets = GameAssets.I;
        var go = ObjectPool.Get(PoolKey);
        if (go == null)
        {
            // Prefab 优先（可换字体/样式/动画），没有则代码生成默认文字
            if (assets != null && assets.damageTextPrefab != null)
                go = Object.Instantiate(assets.damageTextPrefab, _canvas.transform);
            else
                go = CreateDefault();
            go.name = "DamageText";
        }
        go.layer = UIUtil.UILayer;   // 统一 UI 层（Overlay 下不影响渲染）

        // ⚠️ 不截断（含「资源总表里配的飘字 Prefab」这条路径）：见 CreateDefault 的注释
        var safeTxt = go.GetComponentInChildren<Text>(true);
        if (safeTxt != null) safeTxt.verticalOverflow = VerticalWrapMode.Overflow;

        var popup = go.GetComponent<DamagePopup>();
        if (popup == null) popup = go.AddComponent<DamagePopup>();
        popup.poolKey = PoolKey;   // 覆盖 Prefab 里的默认值，保证回池键一致

        Color c;
        int size;
        switch (kind)
        {
            case Kind.DamageToEnemy: c = EnemyColor; size = amount >= 50f ? 30 : 22; break;
            case Kind.DamageToPlayer: c = PlayerColor; size = amount >= 50f ? 32 : 24; break;
            case Kind.Headshot: c = HeadshotColor; size = amount >= 50f ? 40 : 30; break;   // 比普通伤害更大更醒目
            default: c = HealColor; size = 22; break;
        }
        int v = Mathf.RoundToInt(amount);
        string s = kind == Kind.Heal ? "+" + v : v.ToString();
        popup.Show(worldPos, s, c, size);
    }

    static GameObject CreateDefault()
    {
        var go = new GameObject("DamageText");
        go.transform.SetParent(_canvas.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(220f, 44f);
        var txt = go.AddComponent<Text>();
        txt.font = UIUtil.Font;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.raycastTarget = false;   // 纯显示，不挡任何点击
        // ⚠️ 不截断：Truncate + 换字体后行高变大 → 大字号飘字（爆头 40）可能整段不显示（同 HUD 的注释）
        txt.verticalOverflow = VerticalWrapMode.Overflow;
        go.AddComponent<DamagePopup>();
        return go;
    }
}
