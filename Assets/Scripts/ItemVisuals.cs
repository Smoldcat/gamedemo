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

    /// <summary>这个物品该显示哪张图标。</summary>
    public static Sprite IconOf(ItemData def)
    {
        if (def == null) return null;
        if (def.icon != null) return def.icon;
        if (def is WeaponData w) return BackpackPanel.WeaponIconSprite(w);
        if (def is MedkitData) return BackpackPanel.MedkitSprite();
        return GenericIcon();
    }

    /// <summary>这个物品该用什么颜色着色（血包用固定的红十字红，其它用定义卡上的 `pickupColor`）。</summary>
    public static Color ColorOf(ItemData def)
    {
        if (def == null) return Color.white;
        if (def is MedkitData) return BackpackPanel.MedkitIconColor;
        return def.pickupColor;
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）：成对销毁自己建的那张占位图。</summary>
    internal static void ResetStatics()
    {
        UIUtil.DestroySprite(_genericIcon);
        _genericIcon = null;
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
