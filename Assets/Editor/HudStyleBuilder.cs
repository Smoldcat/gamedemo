using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// 菜单 `Tools/游戏/优化 HUD 风格`：把 HUD 的信息块（左下角血条 / 武器 / 模式、顶部波次与击杀、死亡结算）
/// 统一成「快捷栏 / 背包」那套容器风格 —— **深色半透明圆角底 + 细圆角描边**（配色集中在 `GameAssets` 的「HUD 风格」组）。
///
/// 与 `HotbarBuilder` 完全同一套路：
///   · **结构 / 图片 / 尺寸 / 字号落到场景**里（编辑期可见、可直接拖、全程 `Ctrl+Z` 可撤销、可重复执行）；
///   · **颜色由代码驱动**（`HUD.ApplyHudStyle` 每局读一次 `GameAssets`）→ **在节点上调色无效**，改颜色请改资源总表；
///   · 会**重挂**现有节点（HPBg / HPText / WeaponText / ModeText / WaveText / KillText / DeathText / DeathStat / RestartBtn）
///     → HUD 上的 Inspector 引用**不用改**（引用的是对象本身，不是位置）；
///   · ⚠️ 执行后场景被标脏 —— **请 Ctrl+S 保存场景**；
///   · 想回退：`Ctrl+Z`（或把 `GameAssets.hudUseNewStyle` 取消勾选 = 颜色与血条模式回到老样子；结构节点可直接删）。
///
/// ⚠️ 与快捷栏的一致性：血条 = 圆角底 + 圆角填充（Filled 模式，血量低时不会被挤扁）；
///    卡片底 = 背包库存格底色、描边 = 快捷槽边框色；死亡按钮 = 圆角底 + 亮蓝描边（与「当前槽高亮」同色）。
/// </summary>
public static class HudStyleBuilder
{
    const string SpriteDir = "Assets/Art/UI/HUD";

    // ---- 布局常量（参考分辨率 1280×720 的像素；本工具生成的节点摆放 = 这里的唯一来源）----
    // 左下角状态卡片（自下而上：武器 → 血条 → 血量数值 → 模式，与原布局顺序一致）
    const float CardPosX = 20f, CardPosY = 20f;
    const float CardW = 304f, CardH = 152f, CardPad = 14f;
    const float RowWeaponY = 14f, RowBarY = 52f, RowHpTextY = 84f, RowModeY = 116f;
    const float BarW = 276f, BarH = 24f, BarInset = 4f;
    // 顶部（屏幕上方居中）
    const float WaveCardW = 340f, WaveCardH = 44f, WaveCardY = -12f;
    const float KillCardW = 150f, KillCardH = 32f, KillCardY = -62f;
    // 死亡面板中央卡片
    const float DeathCardW = 440f, DeathCardH = 300f;
    const float DeathTitleY = 92f, DeathStatY = 26f, DeathBtnY = -72f, DeathBtnW = 220f, DeathBtnH = 54f;
    // 字号（收敛后：波次 26→20、击杀 20→16、血量 20→18、武器 24→20、模式 18→14）
    const int FontWave = 20, FontKill = 16, FontHp = 18, FontWeapon = 20, FontMode = 14, FontBtn = 22;

    [MenuItem("Tools/游戏/优化 HUD 风格")]
    static void Build()
    {
        var hud = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponentInParent<HUD>() : null;
        if (hud == null) hud = Object.FindObjectOfType<HUD>();
        if (hud == null)
        {
            EditorUtility.DisplayDialog("优化 HUD 风格",
                "场景里找不到 HUD 组件。\n请先选中带 HUD 的物体（一般是场景里的 Canvas），或确认场景里有 HUD。", "好");
            return;
        }

        var canvas = hud.GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            EditorUtility.DisplayDialog("优化 HUD 风格", "HUD 所在物体上找不到 Canvas。", "好");
            return;
        }

        Undo.SetCurrentGroupName("优化 HUD 风格");
        int group = Undo.GetCurrentGroup();

        // ---------- 1) 贴图（生成一次即可复用；失败就降级为"没有图"，颜色仍由代码驱动） ----------
        var cardBg    = UISpriteToolUtil.EnsureSpriteAsset(SpriteDir, "HudCardBg.png",    UIUtil.RoundedRectSprite(128, 20f, 0f), new Vector4(20, 20, 20, 20));
        var cardFrame = UISpriteToolUtil.EnsureSpriteAsset(SpriteDir, "HudCardFrame.png", UIUtil.RoundedRectSprite(128, 20f, 4f), new Vector4(20, 20, 20, 20));
        var barBg     = UISpriteToolUtil.EnsureSpriteAsset(SpriteDir, "HudBarBg.png",     UIUtil.RoundedRectSprite(64, 12f, 0f),  new Vector4(12, 12, 12, 12));
        var barFrame  = UISpriteToolUtil.EnsureSpriteAsset(SpriteDir, "HudBarFrame.png",  UIUtil.RoundedRectSprite(64, 12f, 3f),  new Vector4(12, 12, 12, 12));
        var barFill   = UISpriteToolUtil.EnsureSpriteAsset(SpriteDir, "HudBarFill.png",   UIUtil.RoundedRectSprite(64, 12f, 0f),  new Vector4(12, 12, 12, 12));
        var btnBg     = UISpriteToolUtil.EnsureSpriteAsset(SpriteDir, "HudBtnBg.png",     UIUtil.RoundedRectSprite(64, 16f, 0f),  new Vector4(16, 16, 16, 16));

        // 编辑期预览色（运行时由 `HUD.ApplyHudStyle` 覆盖 → 这里只影响"没进 Play 时看到的观感"）
        var ga = GameAssets.I;
        Color cardC  = ga != null ? ga.hudCardColor       : new Color(0.16f, 0.18f, 0.26f, 0.92f);
        Color frameC = ga != null ? ga.hudFrameColor      : new Color(0.32f, 0.36f, 0.44f, 0.9f);
        Color barC   = ga != null ? ga.hudBarBgColor      : new Color(0.09f, 0.11f, 0.17f, 0.95f);
        Color textC  = ga != null ? ga.hudTextColor       : new Color(0.90f, 0.93f, 0.98f, 1f);
        Color dimC   = ga != null ? ga.hudDimTextColor    : new Color(0.62f, 0.68f, 0.80f, 1f);
        Color btnTxt = ga != null ? ga.hudButtonTextColor : new Color(0.95f, 0.97f, 1f, 1f);
        Color hlC    = UIUtil.SlotActiveColor();   // 与背包/快捷栏同一份「高亮色」

        // ---------- 2) 左下角状态卡片 ----------
        var status = EnsureCard(canvas.transform, "StatusCard", cardBg, cardFrame, cardC, frameC);
        SetRect(status, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(CardPosX, CardPosY), new Vector2(CardW, CardH));

        // 血条：复用原 `HPBg` 节点（`HPFill` 是它的子节点，父子关系不动）
        var hpBg = hud.hpFill != null ? hud.hpFill.transform.parent as RectTransform : null;
        if (hpBg != null)
        {
            Reparent(hpBg, status);
            SetRect(hpBg, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(CardPad, RowBarY), new Vector2(BarW, BarH));
            var bgImg = hpBg.GetComponent<Image>();
            if (bgImg != null)
            {
                if (barBg != null) { bgImg.sprite = barBg; bgImg.type = Image.Type.Sliced; }
                bgImg.color = barC;
            }

            // 血条描边（新增；名字固定 `HpFrame` —— 运行时 `HUD.ApplyHudStyle` 按这个名字找它做低血警示）
            var frame = EnsureImage(hpBg, "HpFrame", barFrame, frameC, true);
            if (frame != null) Stretch(frame.rectTransform, hpBg);

            // 填充：换圆角图 + 切到 Filled 模式（血量低时圆角不会被挤扁）；颜色运行时每帧写（红 → 绿血量色）
            var fill = hud.hpFill;
            if (fill != null)
            {
                SetRect(fill.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                        new Vector2(BarInset, 0f), new Vector2(BarW - BarInset * 2f, BarH - BarInset * 2f));
                if (barFill != null) fill.sprite = barFill;
                fill.type = Image.Type.Filled;
                fill.fillMethod = Image.FillMethod.Horizontal;
                fill.fillOrigin = (int)Image.OriginHorizontal.Left;
                fill.fillAmount = 1f;
                fill.color = new Color(0.2f, 0.85f, 0.3f, 1f);   // 预览：满血绿（运行时按血量 Lerp）
            }
        }
        else Debug.LogWarning("[HudStyleBuilder] 找不到血条（HUD.hpFill 为空，或其父节点不是 RectTransform）→ 跳过血条改造。");

        // 三行文字（重挂进卡片 + 统一字号 / 左对齐）
        ReparentText(hud.weaponText, status, new Vector2(0f, 0f), new Vector2(CardPad, RowWeaponY), new Vector2(CardW - CardPad * 2f, 30f), FontWeapon, textC);
        ReparentText(hud.hpText,     status, new Vector2(0f, 0f), new Vector2(CardPad, RowHpTextY), new Vector2(CardW - CardPad * 2f, 26f), FontHp, textC);
        ReparentText(hud.modeText,   status, new Vector2(0f, 0f), new Vector2(CardPad, RowModeY),   new Vector2(CardW - CardPad * 2f, 22f), FontMode, dimC);

        // ---------- 3) 顶部：波次卡片 + 击杀卡片 ----------
        var waveCard = EnsureCard(canvas.transform, "WaveCard", cardBg, cardFrame, cardC, frameC);
        SetRect(waveCard, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, WaveCardY), new Vector2(WaveCardW, WaveCardH));
        ReparentText(hud.waveText, waveCard, Vector2.zero, Vector2.zero, Vector2.zero, FontWave, textC);
        StretchFull(hud.waveText);

        var killCard = EnsureCard(canvas.transform, "KillCard", cardBg, cardFrame, cardC, frameC);
        SetRect(killCard, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, KillCardY), new Vector2(KillCardW, KillCardH));
        ReparentText(hud.killText, killCard, Vector2.zero, Vector2.zero, Vector2.zero, FontKill, hud.killText != null ? hud.killText.color : Color.white);
        StretchFull(hud.killText);

        // ---------- 4) 死亡面板：中央卡片 + 按钮同款化 ----------
        var deathPanel = hud.deathPanel != null ? hud.deathPanel.transform as RectTransform : null;
        RectTransform deathCard = null;
        if (deathPanel != null)
        {
            deathCard = EnsureCard(deathPanel, "DeathCard", cardBg, cardFrame, cardC, frameC);
            SetRect(deathCard, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(DeathCardW, DeathCardH));
            deathCard.SetAsFirstSibling();   // 卡片垫在标题 / 统计 / 按钮底下（它是面板里最底层的装饰）

            // 标题（`DeathText` 无 Inspector 引用，按名字找）
            var title = FindNamed(deathPanel, "DeathText") as RectTransform;
            if (title != null)
            {
                Reparent(title, deathCard);
                SetRect(title, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, DeathTitleY), new Vector2(DeathCardW - 40f, 90f));
            }

            if (hud.deathStat != null)
            {
                Reparent(hud.deathStat.transform, deathCard);
                SetRect(hud.deathStat.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                        new Vector2(0f, DeathStatY), new Vector2(DeathCardW - 40f, 40f));
                hud.deathStat.color = dimC;
            }

            if (hud.restartBtn != null)
            {
                Reparent(hud.restartBtn.transform, deathCard);
                SetRect(hud.restartBtn.transform as RectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                        new Vector2(0f, DeathBtnY), new Vector2(DeathBtnW, DeathBtnH));

                // 按钮底：换成圆角底图（原是 Unity 内置白方块）；描边单独一层（亮蓝 = 与「当前槽高亮」同色）
                if (hud.restartBtn.targetGraphic is Image btnImg)
                {
                    if (btnBg != null) { btnImg.sprite = btnBg; btnImg.type = Image.Type.Sliced; }
                    btnImg.color = new Color(0.30f, 0.34f, 0.44f, 1f);
                }
                var btnFrame = EnsureImage(hud.restartBtn.transform, "BtnFrame", cardFrame, hlC, true);
                if (btnFrame != null)
                {
                    Stretch(btnFrame.rectTransform, hud.restartBtn.transform as RectTransform);
                    btnFrame.raycastTarget = false;   // 装饰层不接点击（点击仍然落在按钮底上）
                }
                if (hud.restartBtnLabel != null)
                {
                    hud.restartBtnLabel.color = btnTxt;
                    hud.restartBtnLabel.fontSize = FontBtn;
                }
            }
        }
        else Debug.LogWarning("[HudStyleBuilder] HUD.deathPanel 为空 → 跳过死亡面板改造。");

        UISpriteToolUtil.SetLayerRecursively(canvas.gameObject, UISpriteToolUtil.UILayerIndex());

        // ---------- 5) 接线到 HUD ----------
        var so = new SerializedObject(hud);
        Assign(so, "statusCard", status);
        Assign(so, "waveCard", waveCard);
        Assign(so, "killCard", killCard);
        Assign(so, "deathCard", deathCard);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(hud);
        EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);

        Undo.CollapseUndoOperations(group);
        Selection.activeGameObject = status.gameObject;

        Debug.Log("[HudStyleBuilder] HUD 容器化风格已生成：StatusCard / WaveCard / KillCard / DeathCard，" +
                  "并已接到 HUD 的四个引用上（贴图在 " + SpriteDir + "）。\n" +
                  "· 布局 / 尺寸 / 图片 / 字号：直接在节点上拖（本工具可重复执行，不会重复造节点）。\n" +
                  "· ⚠️ **颜色改节点无效**：卡片 / 描边 / 血条 / 按钮的颜色由代码每局从 `GameAssets`「HUD 风格」组读取 → 改颜色请改资源总表。\n" +
                  "· ⚠️ 血条已切到 Filled 模式（`HUD` 会在未切模式时自动退回旧的「改宽度」写法）→ 别把 `HPFill` 的 Image Type 改回 Simple。\n" +
                  "· ⚠️ 场景已标脏 → 请 **Ctrl+S** 保存。想回退：Ctrl+Z，或取消 `GameAssets.hudUseNewStyle`。");
    }

    // ==================== 工具函数 ====================

    static void Assign(SerializedObject so, string field, Object value)
    {
        var p = so.FindProperty(field);
        if (p != null) p.objectReferenceValue = value;
    }

    /// <summary>在子树里**递归**按名字找（`Transform.Find` 只找直接子节点）</summary>
    static Transform FindNamed(Transform root, string name)
    {
        if (root == null) return null;
        for (int i = 0; i < root.childCount; i++)
        {
            var c = root.GetChild(i);
            if (c.name == name) return c;
            var hit = FindNamed(c, name);
            if (hit != null) return hit;
        }
        return null;
    }

    static void SetRect(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        if (rt == null) return;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    /// <summary>让子节点铺满父节点（⚠️ `child` 必须已经是 `parent` 的子节点）</summary>
    static void Stretch(RectTransform child, RectTransform parent)
    {
        if (child == null || parent == null) return;
        child.anchorMin = Vector2.zero;
        child.anchorMax = Vector2.one;
        child.offsetMin = Vector2.zero;
        child.offsetMax = Vector2.zero;
    }

    /// <summary>重挂父子（记录 Undo），并放到新父节点的**最后一个兄弟位**（= 画在最上层；卡片底/描边是前两个子节点）</summary>
    static void Reparent(Transform child, Transform newParent)
    {
        if (child == null || newParent == null) return;
        Undo.SetTransformParent(child, newParent, "重挂 HUD 节点");
        child.SetAsLastSibling();
    }

    /// <summary>把文字节点重挂进容器 + 统一字号 / 左对齐 / 颜色（字体走 `UIUtil.ApplyFont`，同时强制不截断）</summary>
    static void ReparentText(Text t, RectTransform parent, Vector2 anchor, Vector2 pos, Vector2 size, int fontSize, Color color)
    {
        if (t == null || parent == null) return;
        Reparent(t.transform, parent);
        SetRect(t.rectTransform, anchor, new Vector2(0f, 0f), pos, size);
        t.fontSize = fontSize;
        t.alignment = TextAnchor.MiddleLeft;
        t.color = color;
        UIUtil.ApplyFont(t, UISpriteToolUtil.PreviewFont());
    }

    static void StretchFull(Text t)
    {
        if (t == null) return;
        Stretch(t.rectTransform, t.transform.parent as RectTransform);
        t.rectTransform.pivot = new Vector2(0.5f, 0.5f);   // ⚠️ 必须居中：击杀文字有「放大」动画，pivot 在角上会放大时跑偏
        t.alignment = TextAnchor.MiddleCenter;
    }

    /// <summary>确保容器节点存在（含 `CardBg` / `CardFrame` 两层），返回它的 RectTransform</summary>
    static RectTransform EnsureCard(Transform parent, string name, Sprite bg, Sprite frame, Color bgC, Color frameC)
    {
        var existing = FindNamed(parent, name) as RectTransform;
        RectTransform card;
        if (existing != null) card = existing;
        else
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            card = go.AddComponent<RectTransform>();
            Undo.RegisterCreatedObjectUndo(go, "创建 HUD 卡片");
        }

        var bgImg = EnsureImage(card, "CardBg", bg, bgC, true);
        if (bgImg != null) { Stretch(bgImg.rectTransform, card); bgImg.transform.SetAsFirstSibling(); }
        var frameImg = EnsureImage(card, "CardFrame", frame, frameC, true);
        if (frameImg != null) { Stretch(frameImg.rectTransform, card); frameImg.transform.SetSiblingIndex(1); }
        return card;
    }

    /// <summary>确保一个直接子节点 Image 存在（不存在就建），并设好图片 / 类型 / 颜色</summary>
    static Image EnsureImage(Transform parent, string name, Sprite sprite, Color color, bool sliced)
    {
        if (parent == null) return null;
        var t = parent.Find(name);
        Image img;
        if (t != null) img = t.GetComponent<Image>();
        else
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            Undo.RegisterCreatedObjectUndo(go, "创建 HUD 部件");
            img = go.AddComponent<Image>();
        }
        if (img == null) return null;
        if (sprite != null) img.sprite = sprite;
        if (sliced) img.type = Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = false;   // 纯装饰（按钮的命中判定由按钮底自己承担）
        return img;
    }
}
