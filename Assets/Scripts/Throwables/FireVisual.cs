using UnityEngine;

/// <summary>
/// 火焰的**贴图与材质**（形状只有这一份实现 —— 运行时兜底 + 编辑器导出器都调它，同 §2.6"只留一份"）。
///
/// 贴图是**程序生成**的（和弹药图标 / 圆环 / 半影 / 烟的那张 puff 一样，项目里不依赖美术出图）：
///   · 一张 128×128 的**火舌**：底部宽、往上收窄成舌尖，边缘用两层 value-noise 打毛（火舌的抖动）；
///   · 颜色**烘进贴图**：芯部近白黄 → 中段橙 → 边缘红（配合粒子"随寿命变色"就得到完整火色）；
///   · alpha = 火舌密度（软边，无硬切）。
/// ⚠️ 想要"更像火"通常靠**加法混合**（黑=透明、亮=发光）——所以材质优先选加法粒子 shader（见下）。
/// 📌 想换美术图：跑一次菜单 `Tools/游戏/FX/导出火焰贴图` 会落成 `Assets/Art/FX/火焰.png` + `火焰.mat`，
///    再把材质拖进效果卡的 `material` 槽即可（卡上没配才用这里的运行时兜底）。
/// </summary>
public static class FireVisual
{
    // ⚠️ 贴图是**纯共享资源**（不持有场景对象）→ 按 `Effects` 的既定约定**刻意不清**（清了反而会把在用它的材质打黑）。
    static Texture2D _tex;

    /// <summary>取缓存的火焰贴图（首次调用生成）。</summary>
    public static Texture2D Texture()
    {
        if (_tex == null) _tex = CreateTexture(128);
        return _tex;
    }

    /// <summary>建一份火焰材质（**加法混合**：黑=透明、亮=发光，最像火）。
    /// 📌 实现在 `Effects.AdditiveMaterial`（唯一实现，闪光弹的"范围光"也用同一份）。
    /// ⚠️ 用 `HasProperty` 探测再写属性（项目铁律：不同 shader 属性集不同，直接写会报错）。</summary>
    public static Material CreateMaterial(Texture2D tex)
        => Effects.AdditiveMaterial(tex, Color.white, "火焰（运行时）");

    /// <summary>画一张火舌贴图（`size × size`，RGBA32）。</summary>
    public static Texture2D CreateTexture(int size)
    {
        size = Mathf.Clamp(size, 32, 512);
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            // t：0 = 贴图底部（火根），1 = 顶部（舌尖）。Unity 贴图的 y=0 在**下**方，正好对应"火往上长"。
            float t = y / (float)(size - 1);
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f;          // [-1, 1]

                // 火舌半宽：底部最宽，往上收窄（舌尖留一点点宽度，否则顶部是一根硬刺）
                float halfW = 0.46f * (1f - Mathf.Pow(t, 1.35f)) + 0.05f;
                // 边缘抖动：用噪声调制半宽（两层 → 大波浪 + 小抖动），这就是"火在舔"的观感
                float wobble = 0.72f + 0.34f * ValueNoise(u * 2.3f + 5.1f, t * 1.7f + 2.0f)
                                    + 0.18f * ValueNoise(u * 5.1f + 1.7f, t * 3.9f + 7.3f);
                halfW *= wobble;

                // 横向软衰减（抛物线，边缘自然）
                float dx = Mathf.Abs(u) / Mathf.Max(halfW, 0.001f);
                float shape = Mathf.Clamp01(1f - dx * dx);
                // 纵向：根部实、顶部收（再叠一点噪声让火苗高度不齐）
                shape *= Mathf.Clamp01(1f - Mathf.Pow(t, 2.1f)) * (0.75f + 0.5f * ValueNoise(t * 2.6f + 9.4f, u + 3f));
                shape = Mathf.Clamp01(shape * 1.25f);

                // 颜色：密度低 = 暗红（边缘/梢），中 = 橙，高 = 白黄（芯）
                float d = shape;
                Color c = Color.Lerp(new Color(0.62f, 0.08f, 0.02f), new Color(1f, 0.52f, 0.10f),
                                     Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d * 1.8f)));
                c = Color.Lerp(c, new Color(1f, 0.96f, 0.72f), Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d * 2.6f - 1.6f)));

                px[y * size + x] = new Color(c.r, c.g, c.b, Mathf.Clamp01(d));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    // ---------------- 两层值噪声（白噪声 + 双线性插值；够用来打毛边缘，不必上柏林/单纯形） ----------------

    static float ValueNoise(float x, float y)
    {
        int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
        float xf = x - xi, yf = y - yi;
        float u = xf * xf * (3f - 2f * xf);      // smoothstep 插值
        float v = yf * yf * (3f - 2f * yf);
        float a = Hash(xi, yi), b = Hash(xi + 1, yi), c = Hash(xi, yi + 1), d = Hash(xi + 1, yi + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
    }

    static float Hash(int x, int y)
    {
        int h = x * 374761393 + y * 668265263;
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        return (h & 0x7fffffff) / (float)0x7fffffff;
    }
}
