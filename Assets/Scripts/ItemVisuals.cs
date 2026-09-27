using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 物品的**显示**查询（图标 + 颜色）—— 背包 / 箱子 / 快捷栏**共用同一份实现**。
///
/// 为什么单独一个文件：`BackpackPanel` 与 `ChestPanel` 都要把"一个物品显示成什么样"画出来；
/// 各写一份的话，"箱子里的图标和背包里不一样"这类问题迟早出现（项目里已有过"两处算法不一致"的教训）。
///
/// 优先级（与项目一贯的"资源优先 + 代码兜底"一致）：
///   · 物品定义卡上配了 `icon` → 用它
///   · 武器 → 复用 `BackpackPanel.WeaponIconSprite`（按形状程序生成，也认 `WeaponData.icon`）
///   · 血包 → 复用 `BackpackPanel.MedkitSprite`（红十字）
///   · 其它 → 程序生成的**菱形占位图**（以后有正式美术，就在定义卡上拖 `icon` 即可，不用改代码）
///
/// ⚠️ 占位图是本类**自己创建的运行时 Sprite**，必须由 `StaticReset` → `ResetStatics` 成对销毁（§4 #25）。
/// ⚠️ 武器 / 血包那两张图的**生命周期归 `BackpackPanel.ResetStatics`** —— 本类**不要**去销毁它们。
/// </summary>
public static class ItemVisuals
{
    static Sprite _genericIcon;   // 菱形占位（只建一次）
    static readonly Dictionary<AmmoKind, Sprite> _ammoIcons = new Dictionary<AmmoKind, Sprite>();   // 弹药图标（按口径缓存，最多 5 张）
    static Sprite _shadowSprite;  // 地上的"投影"贴图（径向渐变圆，只建一次）

    /// <summary>这个物品该显示哪张图标。</summary>
    public static Sprite IconOf(ItemData def)
    {
        if (def == null) return null;
        if (def.icon != null) return def.icon;
        if (def is WeaponData w) return BackpackPanel.WeaponIconSprite(w);
        if (def is MedkitData) return BackpackPanel.MedkitSprite();
        if (def is AmmoData a) return AmmoIconSprite(a);
        return GenericIcon();
    }

    /// <summary>这个物品该用什么颜色着色（血包用固定的红十字红，其它用定义卡上的 `pickupColor`）。</summary>
    public static Color ColorOf(ItemData def)
    {
        if (def == null) return Color.white;
        if (def is MedkitData) return BackpackPanel.MedkitIconColor;
        return def.pickupColor;
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）：成对销毁自己建的那些图（占位图 + 弹药图标 + 投影图）。</summary>
    internal static void ResetStatics()
    {
        UIUtil.DestroySprite(_genericIcon);
        _genericIcon = null;
        foreach (var kv in _ammoIcons) UIUtil.DestroySprite(kv.Value);
        _ammoIcons.Clear();
        UIUtil.DestroySprite(_shadowSprite);
        _shadowSprite = null;
    }

    /// <summary>地上的**投影**贴图：一个**径向渐变圆**（白 alpha，中心实、边缘平滑渐隐 → 天然的"半影"效果）。
    /// 颜色由调用方给（一般是黑色 + 可调透明度），所以这里只画形状。
    /// ⚠️ 生命周期归 `ItemVisuals.ResetStatics`（与占位图 / 弹药图标一起销毁）。</summary>
    public static Sprite ShadowSprite()
    {
        if (_shadowSprite != null) return _shadowSprite;

        const int size = 64;
        const float solid = 0.45f;   // 半径 45% 内是实心的，外面 55% 渐隐（这就是"半影"范围）
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;   // 0 = 圆心，1 = 外缘
            float a = 1f - Mathf.SmoothStep(solid, 1f, d);                     // 实心 → 平滑渐隐
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(a)));
        }
        tex.Apply();
        _shadowSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        return _shadowSprite;
    }

    /// <summary>弹药图标（按口径缓存；白色 alpha 剪影，颜色由 `Image.color` 上色 —— 与武器图标同一套做法）。</summary>
    static Sprite AmmoIconSprite(AmmoData a)
    {
        if (a == null) return GenericIcon();
        Sprite s;
        if (_ammoIcons.TryGetValue(a.ammoKind, out s) && s != null) return s;
        s = MakeAmmoIcon(a.ammoKind);
        _ammoIcons[a.ammoKind] = s;
        return s;
    }

    /// <summary>画一枚"竖着的子弹"剪影（弹头朝上）：下半段 = 弹壳（等宽矩形），上半段 = 弹头。
    /// 五种口径靠**尺寸 + 弹头形状**区分（不靠颜色 —— 颜色由定义卡上的 `pickupColor` 给）。</summary>
    static Sprite MakeAmmoIcon(AmmoKind kind)
    {
        const int size = 64;

        // 参数：弹壳长 / 弹头长 / 半宽 / 弹头形状（0 = 尖、1 = 圆、2 = 平顶）
        float caseLen, headLen, halfW; int headShape;
        switch (kind)
        {
            case AmmoKind.Handgun: caseLen = 0.20f; headLen = 0.13f; halfW = 0.105f; headShape = 1; break;  // 短小·圆头
            case AmmoKind.SMG:     caseLen = 0.24f; headLen = 0.12f; halfW = 0.115f; headShape = 1; break;  // 短粗·圆头
            case AmmoKind.Rifle:   caseLen = 0.30f; headLen = 0.18f; halfW = 0.095f; headShape = 0; break;  // 中等·尖头
            case AmmoKind.Sniper:  caseLen = 0.34f; headLen = 0.25f; halfW = 0.078f; headShape = 0; break;  // 细长·尖头
            case AmmoKind.Shotgun: caseLen = 0.44f; headLen = 0.00f; halfW = 0.120f; headShape = 2; break;  // 粗圆柱·平顶
            default:               caseLen = 0.30f; headLen = 0.16f; halfW = 0.095f; headShape = 1; break;
        }

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        float total = caseLen + headLen;
        float vBot = 0.5f - total * 0.5f;      // 弹壳底
        float vMid = vBot + caseLen;           // 弹壳顶 = 弹头底
        float vTop = vMid + headLen;           // 弹头顶

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = (x + 0.5f) / size;
            float v = 1f - (y + 0.5f) / size;                 // v 越大越靠上
            float du = Mathf.Abs(u - 0.5f);
            float a = 0f;

            if (v >= vBot && v <= vMid)                        // 弹壳：等宽矩形（底缘略宽一点点）
                a = du <= (v <= vBot + 0.03f ? halfW * 1.12f : halfW) ? 1f : 0f;
            else if (headLen > 0f && v > vMid && v <= vTop)    // 弹头
            {
                float t = (v - vMid) / headLen;                // 0 = 弹头底，1 = 顶端
                float w = headShape == 0 ? halfW * (1f - t * t * 0.82f)                          // 尖头（略鼓的锥）
                        : headShape == 1 ? halfW * Mathf.Sqrt(Mathf.Max(0f, 1f - t * t))         // 圆头
                        : halfW;
                a = du <= w ? 1f : 0f;
            }

            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    static Sprite GenericIcon()
    {
        if (_genericIcon != null) return _genericIcon;

        // 菱形（与武器图标里的 `Generic` 形状同一套画法）：64×64、白色 + alpha，颜色由 Image.color 上
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size, v = 1f - (y + 0.5f) / size;
                float a = (Mathf.Abs(u - 0.5f) + Mathf.Abs(v - 0.5f)) <= 0.24f ? 1f : 0f;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        _genericIcon = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        return _genericIcon;
    }
}
