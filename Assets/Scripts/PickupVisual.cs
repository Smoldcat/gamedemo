using UnityEngine;

/// <summary>
/// 地上拾取物「**图标 billboard + 脚下半影**」这套外观的**共用实现**
/// （弹药包 `AmmoPickup` / 投掷物 `ThrowablePickup` / 魔法卡 `MagicPickup`，2026-10-15 抽取）。
///
/// ⚠️ **只抽流程、不抽数值**：三类拾取物的「图标尺寸 / 离地高 / 阴影尺寸与透明度」**本来就不同**
///    （各自读自己卡上的字段），所以那些数字仍由调用方按参数传进来 —— 本类绝不写死任何观感。
/// ⚠️ 抽取前这三个类里这段代码**逐段同构**（连注释都差不多）：想改观感得改三遍、还容易漏一处；
///    现在只有这一份（"PPU 换算""阴影必须平躺 + 画在图标之下"这些约定也只写一次）。
/// ⚠️ 阴影必须是**图标的兄弟节点**、不能做成子节点：否则它会跟着 billboard 一起立起来（历史踩坑）。
/// </summary>
public static class PickupVisual
{
    /// <summary>Sprite 的默认世界尺寸由导入 PPU 决定（程序生成的 64px 图 = 0.64m）→
    /// 按它在平面内缩放到目标尺寸（阴影平躺后，localScale 的 x/y 仍对应平面内两个方向）。</summary>
    public static void FitScale(SpriteRenderer sr, float targetSize)
    {
        if (sr == null) return;
        float baseSize = sr.sprite != null ? Mathf.Max(0.0001f, sr.sprite.bounds.size.x) : 1f;
        float k = targetSize / baseSize;
        sr.transform.localScale = new Vector3(k, k, 1f);
    }

    /// <summary>建 billboard 图标节点（名字 `Icon`）：取图标 + 染色（都走 `ItemVisuals`），关阴影投射/接收。</summary>
    public static SpriteRenderer CreateIcon(Transform root, ItemData def, float iconSize)
    {
        var go = new GameObject("Icon");
        go.transform.SetParent(root, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = ItemVisuals.IconOf(def);
        sr.color = ItemVisuals.ColorOf(def);
        sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        sr.receiveShadows = false;
        FitScale(sr, iconSize);
        return sr;
    }

    /// <summary>建脚下半影节点（名字 `Shadow`）：平躺（绕 X 转 90°）+ 永远画在图标之下 + 贴地略抬（防 z-fighting）。
    /// ⚠️ 贴图是程序生成的**径向渐变圆片**（`ItemVisuals.ShadowSprite`）：只有贴图能做到"中心实、边缘渐隐"的半影，
    ///    实心几何体（球 / 圆盘）永远只能是硬边。</summary>
    public static SpriteRenderer CreateShadow(Transform root, float shadowSize, float shadowAlpha)
    {
        var go = new GameObject("Shadow");
        go.transform.SetParent(root, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = ItemVisuals.ShadowSprite();
        sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        sr.receiveShadows = false;
        sr.sortingOrder = -1;                                        // 永远画在图标之下
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // 平躺
        go.transform.localPosition = new Vector3(0f, 0.008f, 0f);     // 贴地（略抬，避免与地面 z-fighting）
        ApplyShadow(sr, shadowSize, shadowAlpha);
        return sr;
    }

    /// <summary>阴影的**尺寸 + 颜色**（黑色，透明度 = `shadowAlpha`，自动夹到 0~1）。</summary>
    public static void ApplyShadow(SpriteRenderer shadow, float shadowSize, float shadowAlpha)
    {
        if (shadow == null) return;
        FitScale(shadow, shadowSize);
        shadow.color = new Color(0f, 0f, 0f, Mathf.Clamp01(shadowAlpha));
    }

    /// <summary>朝相机（billboard）：与相机旋转一致 = 任何俯视角都看到完整形状。</summary>
    public static void Billboard(SpriteRenderer sr)
    {
        if (sr == null) return;
        var cam = CameraCache.Main;
        if (cam != null) sr.transform.rotation = cam.transform.rotation;
    }

    /// <summary>把图标放到「离地高 + 浮动量」的位置（浮动由调用方按自己的节奏给）。</summary>
    public static void PlaceIcon(SpriteRenderer sr, float hover, float bob)
    {
        if (sr != null) sr.transform.localPosition = new Vector3(0f, hover + bob, 0f);
    }
}
