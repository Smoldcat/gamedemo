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
    /// <summary>爆头：醒目橙红（与「打敌黄」「被打红」都区分得开）。
    /// ⚠️ `internal` 是刻意的：`KillFeed` 的爆头图标色**直接引用它** → 爆头配色全项目只有这一份（2026-09-22 收敛）。</summary>
    internal static readonly Color HeadshotColor = new Color(1f, 0.42f, 0.10f);

    static void EnsureCanvas()
    {
        // 场景重载后旧 Canvas 被销毁（Unity 会把已销毁对象判为 null），这里自动重建
        if (_canvas == null)
        {
            _canvas = UIUtil.NewOverlayCanvas("DamageTextCanvas", 300);   // 300 = 高于 HUD/准星，飘字永远可见
            _canvas.gameObject.layer = UIUtil.UILayer;                   // 统一 UI 层（Overlay 下不影响渲染）
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

            // ⚠️ 字体只在**新建这一个**的时候注入一次（不截断，含"资源总表里配的飘字 Prefab"这条路径；
            //    理由见 `UIUtil.ApplyFont` 的注释）。
            //    ⭐ 2026-10-18（外部审查 A4）：原来这行在池外、**每次飘字**都跑
            //    `GetComponentInChildren<Text>(true)` ⇒ 每发子弹都分配一个 `Text[]` + 遍历子树。
            UIUtil.ApplyFont(go.GetComponentInChildren<Text>(true), null);
        }
        go.layer = UIUtil.UILayer;   // 统一 UI 层（Overlay 下不影响渲染）

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
        // ⚠️ 不截断：Truncate + 换字体后行高变大 → 大字号飘字（爆头 40）可能整段不显示（见 UIUtil.ApplyFont）
        UIUtil.ApplyFont(txt, null);
        go.AddComponent<DamagePopup>();
        return go;
    }
}
