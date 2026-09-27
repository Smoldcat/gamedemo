using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 物品的**显示**查询（图标 + 颜色）—— 背包 / 箱子 / 快捷栏**共用同一份实现**。
///
/// 为什么单独一个文件：`BackpackPanel` 与 `ChestPanel` 都要把"一个物品显示成什么样"画出来；
/// 各写一份的话，"箱子里的图标和背包里不一样"这类问题迟早出现（项目里已有过"两处算法不一致"的教训）。
///
/// 优先级（与项目一贯的"资源优先 + 代码兜底"一致）：
///   · **弹药** → **合成图**：资产图标（卡上 `icon`）按 `pickupColor` 染色 + 按 `tierOutlineColor` 叠一圈
///     **等级描边**（一级留透明 = 不描边；二级绿）—— 见 `AmmoIconOutlined`
///   · 物品定义卡上配了其它 `icon` → 用它
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
    static readonly Dictionary<AmmoKind, Sprite> _ammoIcons = new Dictionary<AmmoKind, Sprite>();   // 弹药剪影（按口径缓存；**只作"卡上没填 icon"的兜底**）
    static readonly Dictionary<string, Sprite> _ammoOutlined = new Dictionary<string, Sprite>();    // 弹药的"染色 + 等级描边"合成图（按 图标+配色+宽度 缓存）
    static Sprite _shadowSprite;  // 地上的"投影"贴图（径向渐变圆，只建一次）

    /// <summary>这个物品该显示哪张图标。
    /// ⚠️ **弹药是特例**：返回的是"按口径色染好 + 按等级叠了描边"的**合成图**（见 `AmmoIconOutlined`），
    ///    配套 `ColorOf` 对这类弹药返回白色 —— 于是背包 / 箱子 / 商店 / 地上的弹药包**一行都不用改**。</summary>
    public static Sprite IconOf(ItemData def)
    {
        if (def == null) return null;
        if (def is AmmoData ammo)
            return ammo.icon != null ? AmmoIconOutlined(ammo) : AmmoIconSprite(ammo);
        if (def.icon != null) return def.icon;
        if (def is WeaponData w) return BackpackPanel.WeaponIconSprite(w);
        if (def is MedkitData) return BackpackPanel.MedkitSprite();
        return GenericIcon();
    }

    /// <summary>这个物品该用什么颜色着色（血包用固定的红十字红，其它用定义卡上的 `pickupColor`）。
    /// ⚠️ 填了 `icon` 的弹药返回**白色** —— 它的颜色（含等级描边）已经烘进 `IconOf` 的合成图里了。</summary>
    public static Color ColorOf(ItemData def)
    {
        if (def == null) return Color.white;
        if (def is MedkitData) return BackpackPanel.MedkitIconColor;
        if (def is AmmoData a) return a.icon != null ? Color.white : a.pickupColor;
        return def.pickupColor;
    }

    /// <summary>静态状态清理（供 `StaticReset` 调用）：成对销毁自己建的那些图
    /// （占位图 + 弹药剪影 + 弹药合成图 + 投影图）。</summary>
    internal static void ResetStatics()
    {
        UIUtil.DestroySprite(_genericIcon);
        _genericIcon = null;
        foreach (var kv in _ammoIcons) UIUtil.DestroySprite(kv.Value);
        _ammoIcons.Clear();
        DestroyAmmoOutlined(true);   // 退出 Play / 场景重载：连对象一起销毁（§4 #25）
        UIUtil.DestroySprite(_shadowSprite);
        _shadowSprite = null;
    }

    /// <summary>把弹药的"染色 + 描边"合成图缓存**整个清掉**（旧图一起销毁）。
    /// 由 `AmmoData.OnValidate` 调用 —— 编辑器里改了**口径色 / 描边色 / 描边宽度**后，
    /// 背包 / 箱子 / 地上的弹药包会立刻换上新图（不用重启、不用重进 Play）。
    /// 最多十几张 64×64 小图，重建是毫秒级，所以不做"按卡精确失效"。</summary>
    public static void InvalidateAmmoIcon(AmmoData def)
    {
        // ⚠️ **编辑模式下只清缓存、不销毁对象**：
        //    · 这些图此刻可能正被已打开的背包 / 箱子 / 商店面板引用（那几处只在**重建格子**时才重取 sprite），
        //      销毁会让它们当场变空白；
        //    · 非 Play 状态调 `Object.Destroy` 会被 Unity 警告（"Destroy may not be called from edit mode"）。
        //    代价：编辑期改色会留下几张 64×64 的旧图不回收（量可忽略），面板一重建就取到新图。
        DestroyAmmoOutlined(Application.isPlaying);
    }

    /// <param name="destroyObjects">true = 连 texture/sprite 一起销毁（只在运行时：退出 Play / 场景重载）。
    /// 编辑模式下必须 false（理由见 `InvalidateAmmoIcon`）。⚠️ 无论哪种，字典都要清 —— 不清的话
    /// 下次 `IconOf` 会命中旧缓存，改了色也看不到变化。</param>
    static void DestroyAmmoOutlined(bool destroyObjects)
    {
        if (destroyObjects)
            foreach (var kv in _ammoOutlined) UIUtil.DestroySprite(kv.Value);
        _ammoOutlined.Clear();
    }

    /// <summary>弹药的**最终图标**：资产剪影按 `pickupColor` 染色，再垫一圈 `tierOutlineColor` 的
    /// **膨胀描边**（宽度 = `outlineWidth` 像素），合成成一张新图。
    ///
    /// 为什么合成成"一张图"而不是画两层：全项目有 4 处画物品图标（背包格 / 箱子格 / 商店格 /
    /// 地上的弹药包），让它们各加一个子节点就是 4 处改动 + 4 处布局风险；合成后**调用方零改动**。
    ///
    /// ⚠️ 输出尺寸**保持原图不变**（描边画在图内、不扩画布）→ 各按 `sprite.bounds` 算缩放的地方零变化。
    /// ⚠️ 本实现按**整张贴图**取像素（`texture.GetPixels()`），要求弹药图标是"单图"（Sprite Single + FullRect）。
    ///    以后若改成**图集 / 多子图 / Tight mesh**，这里要改成按 `sprite.rect` 取像素 ——
    ///    否则合成图会变成"整张图集"，膨胀描边还会把邻居子图的像素当成描边来源。
    /// ⚠️ 要求图标资产 **Read/Write ✅**（导出工具已设）；没勾的只读图会退原图（不描边，但不崩）。
    /// ⚠️ 返回的是**运行时 Sprite**，必须由 `ResetStatics` / `InvalidateAmmoIcon` 成对销毁（§4 #25）。</summary>
    static Sprite AmmoIconOutlined(AmmoData a)
    {
        var src = a != null ? a.icon : null;
        if (src == null || src.texture == null) return src;

        string key = src.GetInstanceID() + "|" + ColorUtility.ToHtmlStringRGBA(a.pickupColor) + "|"
                   + ColorUtility.ToHtmlStringRGBA(a.tierOutlineColor) + "|" + a.outlineWidth.ToString("F2");
        Sprite cached;
        if (_ammoOutlined.TryGetValue(key, out cached) && cached != null) return cached;

        Color[] sp;
        try { sp = src.texture.GetPixels(); }
        catch (System.Exception) { return src; }   // 只读贴图（导入时没勾 Read/Write）→ 退回原图

        int W = src.texture.width, H = src.texture.height;
        if (W <= 0 || H <= 0 || sp.Length < W * H) return src;

        int ow = Mathf.Clamp(Mathf.RoundToInt(a.outlineWidth), 0, 16);
        bool outlineOn = ow > 0 && a.tierOutlineColor.a > 0.004f;

        // ① 描边 = 剪影的"膨胀"：邻域（半径 ow 的圆）里有实心像素 → 这个点也算实心 → 自然包出一圈。
        //    程序画的剪影是硬 alpha（0/1），按 alpha > 0.5 判定即可，不需要模糊。
        var dil = new bool[W * H];
        if (outlineOn)
        {
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool hit = false;
                for (int dy = -ow; dy <= ow && !hit; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= H) continue;
                    for (int dx = -ow; dx <= ow; dx++)
                    {
                        if (dx * dx + dy * dy > ow * ow) continue;   // 圆形邻域（比方形的"描边"观感更自然）
                        int xx = x + dx;
                        if (xx < 0 || xx >= W) continue;
                        if (sp[yy * W + xx].a > 0.5f) { hit = true; break; }
                    }
                }
                dil[y * W + x] = hit;
            }
        }

        // ② 合成：底层 = 描边色（只在膨胀区域），上层 = 图标本身（标准 over 叠加）
        var outPx = new Color[W * H];
        Color tint = a.pickupColor;
        for (int i = 0; i < outPx.Length; i++)
        {
            Color baseCol = (outlineOn && dil[i]) ? a.tierOutlineColor : new Color(0f, 0f, 0f, 0f);

            float sa = sp[i].a;
            if (sa <= 0.0001f) { outPx[i] = baseCol; continue; }

            // 图标层：剪影是白的，乘口径色即"染色"；同时乘原图 RGB 是为了以后换成彩色美术图也照样能用
            float sr = tint.r * sp[i].r, sg = tint.g * sp[i].g, sb = tint.b * sp[i].b;
            float da = baseCol.a;
            float outA = sa + da * (1f - sa);
            outPx[i] = outA > 0.0001f
                ? new Color((sr * sa + baseCol.r * da * (1f - sa)) / outA,
                            (sg * sa + baseCol.g * da * (1f - sa)) / outA,
                            (sb * sa + baseCol.b * da * (1f - sa)) / outA,
                            outA)
                : new Color(sr, sg, sb, 0f);
        }

        var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.SetPixels(outPx);
        tex.Apply();

        // ⚠️ `pixelsPerUnit` 必须沿用原图 —— 否则所有按 `sprite.bounds` 算缩放的地方（如弹药包尺寸）
        //    会跟着变，出现"二级弹比一级弹大一圈"这种怪事。
        var made = Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), src.pixelsPerUnit);
        _ammoOutlined[key] = made;
        return made;
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

    /// <summary>画一枚"竖着的子弹"剪影**贴图**（64×64、白色 + alpha）：下半段 = 弹壳（等宽矩形），上半段 = 弹头。
    /// 五种口径靠**尺寸 + 弹头形状**区分（不靠颜色 —— 颜色由定义卡上的 `pickupColor` 给）。
    ///
    /// ⚠️ 形状的**唯一实现**：运行时图标（<see cref="MakeAmmoIcon"/>）与编辑器导出工具
    /// （菜单 `Tools/游戏/导出弹药图标` → `AmmoIconExporter`）都调这里 —— 别各画一份（§2.6）。
    /// 返回的贴图**每次都是新建的、不缓存**（调用方负责销毁）；运行时那份按口径的缓存见 `_ammoIcons`。</summary>
    public static Texture2D MakeAmmoTexture(AmmoKind kind)
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
        return tex;
    }

    /// <summary>弹药图标 Sprite（按口径缓存的那层）—— 只是把 <see cref="MakeAmmoTexture"/> 包成 Sprite。</summary>
    static Sprite MakeAmmoIcon(AmmoKind kind)
    {
        var tex = MakeAmmoTexture(kind);
        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
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
