using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// 把「HUD 快捷栏」**预置到场景里**（一键"从代码里剥离出来"），之后就能在编辑器里直接拖布局。
///
/// 为什么需要：快捷栏原本只有 `HUD` 运行时用代码 `new` 的那条路 —— 编辑器里看不见、也就没法调。
/// 菜单 `Tools/游戏/生成快捷栏到场景` 会在 HUD 的 Canvas 下建一个 `Hotbar` 节点（4 格：1 / 2 / 3 / Q）：
///   · 每格 = `Bg`（半透明深色底）+ `Frame`（描边）+ `Icon`（图标）+ `KeyBg`（键位小圆角矩形）+ `KeyText`；
///   · 最后一格（Q = 血包）额外有 `CountBg` + `CountText`（右下角的数量圆标）；
///   · 把 HUD 上 `hotbar` 引用**自动接好**；
///   · 顺带把圆角底 / 描边 / 键位小矩形 / 数量圆标**生成成正式 PNG 资源**（`Assets/Art/UI/Hotbar/`），
///     这样编辑期就能看到真实观感。
///
/// 之后：**布局 / 尺寸 / 图片 / 字号都在节点上调**（代码只写「图标 + 槽框状态色 + 血包数量」）。
/// ⚠️ **改节点的颜色没用**：槽底 / 槽框 / 键位 / 数量的颜色全部由代码统一驱动（读 `GameAssets` 的「背包」那组
///    颜色 + `HUD` 的「按键圆标」配色），好让快捷栏与背包 / 拾取气泡**永远一致** → 想改颜色请改 `GameAssets`。
/// ⚠️ 图标在编辑期是**空的**：它是运行时按背包内容填的（有几个武器、有几个血包），编辑期没有数据可填。
/// ⚠️ 想回到"代码生成"：把 HUD 上 `hotbar` 引用清空即可（HUD 会自动退回代码生成，游戏照常跑）。
/// </summary>
public static class HotbarBuilder
{
    const string SpriteDir = "Assets/Art/UI/Hotbar";   // 快捷栏贴图单独成目录，别把 Assets/Art/UI 堆散

    // ⚠️ 与 `HUD` 里的同名常量保持一致（编辑器程序集读不到 HUD 的 private const）。
    //    改 HUD 里那份记得一起改这里 —— 这只影响**本工具生成的节点**，不影响运行时兜底路径。
    const int   SlotCount = 4;
    const float SlotSize  = 56f;   // 槽框边长
    const float Gap       = 8f;    // 槽间距
    const float Bottom    = 14f;   // 整组距屏幕底部
    const float KeyGap    = 4f;    // 槽框与下方键位小矩形的间距
    const float KeyW      = 22f;   // 键位小圆角矩形（宽 × 高）
    const float KeyH      = 18f;
    const float IconSize  = 40f;   // 槽内图标边长
    const float BadgeSize = 15f;   // 血包数量圆标直径（2026-09-22 由 20 调小，用户要求）
    const int   KeyFont   = 12;
    const int   CountFont = 10;    // 圆标变小了 → 字号跟着收

    [MenuItem("Tools/游戏/生成快捷栏到场景")]
    static void Build()
    {
        var hud = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponentInParent<HUD>()
            : null;
        if (hud == null) hud = Object.FindObjectOfType<HUD>();
        if (hud == null)
        {
            EditorUtility.DisplayDialog("生成快捷栏",
                "场景里找不到 HUD 组件。\n请先选中带 HUD 的物体（一般是场景里的 Canvas），或确认场景里有 HUD。", "好");
            return;
        }

        var so = new SerializedObject(hud);
        var prop = so.FindProperty("hotbar");
        if (prop != null && prop.objectReferenceValue != null)
        {
            if (!EditorUtility.DisplayDialog("生成快捷栏",
                "HUD 上已经配好快捷栏了。\n要继续的话会重建一个并覆盖引用（旧的 Hotbar 节点会被删掉）。", "重建", "取消"))
                return;
        }

        var canvas = hud.GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            EditorUtility.DisplayDialog("生成快捷栏", "HUD 所在物体上找不到 Canvas。", "好");
            return;
        }

        Undo.SetCurrentGroupName("生成快捷栏");
        int group = Undo.GetCurrentGroup();

        // 旧的（按名字找）先删掉，避免同名的残留
        var old = canvas.transform.Find("Hotbar");
        if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

        // ---------- 贴图资源（生成一次即可复用；失败就退回"运行时自动补图"） ----------
        var bgSprite = UISpriteToolUtil.EnsureSpriteAsset(SpriteDir, "HotbarSlotBg.png",
            UIUtil.RoundedRectSprite(64, 14f, 0f), new Vector4(14f, 14f, 14f, 14f));
        // 槽框优先**复用 GameAssets 里给背包配的「快捷槽边框」** → 与背包共用同一张图（改一处两边变）
        var frameSprite = GameAssets.I != null ? GameAssets.I.backpackSlotFrameSprite : null;
        if (frameSprite == null)
            // ⚠️ 描边取 **6px**（不是 4px）：4px 压在深色槽底上太细，看不出"当前槽被框住"（用户 2026-09-22 反馈）
            frameSprite = UISpriteToolUtil.EnsureSpriteAsset(SpriteDir, "HotbarSlotFrame.png",
                UIUtil.RoundedRectSprite(64, 14f, 6f), new Vector4(14f, 14f, 14f, 14f));
        var keySprite = UISpriteToolUtil.EnsureSpriteAsset(SpriteDir, "HotbarKey.png",
            UIUtil.RoundedRectSprite(64, 10f, 0f), new Vector4(10f, 10f, 10f, 10f));
        var badgeSprite = UISpriteToolUtil.EnsureSpriteAsset(SpriteDir, "HotbarBadge.png",
            UIUtil.CircleSprite(48), Vector4.zero);

        // ---------- 根节点（屏幕底部正中） ----------
        float w = SlotCount * SlotSize + (SlotCount - 1) * Gap;
        float h = SlotSize + KeyGap + KeyH;
        var root = MakeRect("Hotbar", canvas.transform, new Vector2(w, h), Vector2.zero);
        Undo.RegisterCreatedObjectUndo(root.gameObject, "生成快捷栏");
        root.anchorMin = root.anchorMax = new Vector2(0.5f, 0f);   // Canvas 底部中心
        root.pivot = new Vector2(0.5f, 0f);                        // 贴底：整组往上长
        root.anchoredPosition = new Vector2(0f, Bottom);

        // ---------- 4 个槽 ----------
        for (int i = 0; i < SlotCount; i++)
        {
            bool isLast = i == SlotCount - 1;
            float x = -w * 0.5f + SlotSize * 0.5f + i * (SlotSize + Gap);
            var slot = MakeRect("Slot" + (i + 1), root, new Vector2(SlotSize, h), new Vector2(x, h * 0.5f));

            float boxY = h - SlotSize * 0.5f;   // 槽框中心（贴顶，键位在下面）

            // 槽底（半透明深色）→ 与背包的「库存格底色」同源
            MakeImage(slot, "Bg", new Vector2(SlotSize, SlotSize), new Vector2(0f, boxY),
                bgSprite, BackpackPanel.CellColor(), true);

            // 描边。⚠️ 颜色由代码每帧写（当前槽 = 高亮色 / 其余 = 普通色）→ 这里只是**预览值**：
            //    1 号槽先填高亮色，好让你在编辑器里就看到"当前槽"长什么样。
            MakeImage(slot, "Frame", new Vector2(SlotSize, SlotSize), new Vector2(0f, boxY),
                frameSprite, i == 0 ? BackpackPanel.SlotActiveColor() : BackpackPanel.SlotColor(), true);

            // 图标：**留空并先关掉**（没有 sprite 的 Image 会画成白方块）；运行时按背包内容填。
            var icon = MakeImage(slot, "Icon", new Vector2(IconSize, IconSize), new Vector2(0f, boxY),
                null, Color.white, false);
            icon.preserveAspect = true;   // 换了非方形的正式图标也不会被压扁
            icon.gameObject.SetActive(false);

            // 键位小圆角矩形（白底 + 深字，与拾取气泡的「按键圆标」同一对配色）
            var keyBg = MakeImage(slot, "KeyBg", new Vector2(KeyW, KeyH), new Vector2(0f, KeyH * 0.5f),
                keySprite, HUD.BubbleKeyBgColor, true);
            // ⚠️ 文字必须**建成键位小矩形的子节点**再铺满 —— `Stretch` 只改锚点、**不重挂父子**；
            //    如果建成 Slot 的子节点，锚点就是相对整个槽算的 → 文字会铺满整格、数字跑到图标正中间（踩过）。
            var keyText = MakeText(keyBg.transform, "KeyText", isLast ? "Q" : (i + 1).ToString(), KeyFont, HUD.BubbleKeyTextColor);
            Stretch(keyText.rectTransform, keyBg.rectTransform);

            if (!isLast) continue;

            // 血包数量圆标（贴槽框右下角内侧；占位数字用 2，运行时写真实值）
            float half = BadgeSize * 0.5f;
            var badge = MakeImage(slot, "CountBg", new Vector2(BadgeSize, BadgeSize),
                new Vector2(SlotSize * 0.5f - half, boxY - SlotSize * 0.5f + half),
                badgeSprite, HUD.BubbleKeyBgColor, false);
            var badgeText = MakeText(badge.transform, "CountText", "2", CountFont, HUD.BubbleKeyTextColor);
            Stretch(badgeText.rectTransform, badge.rectTransform);
        }

        UISpriteToolUtil.SetLayerRecursively(root.gameObject, UISpriteToolUtil.UILayerIndex());

        // ---------- 接线到 HUD ----------
        Assign(so, "hotbar", root);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(hud);
        EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);

        Undo.CollapseUndoOperations(group);
        Selection.activeGameObject = root.gameObject;

        Debug.Log("[HotbarBuilder] 已生成「Hotbar」（4 格：1 / 2 / 3 / Q）并接到 HUD 的 hotbar 引用上。\n" +
                  "· 现在可以在编辑器里直接拖布局 / 改尺寸 / 换图 / 改字号（贴图已生成到 " + SpriteDir + "）。\n" +
                  "· ⚠️ **颜色改节点无效**：槽底 / 槽框 / 键位 / 数量圆标的颜色由代码统一驱动（读 GameAssets 的「背包」组）" +
                  "→ 改颜色请改 GameAssets，这样背包与快捷栏永远一致。\n" +
                  "· 1 号槽的描边在编辑期先填了「当前槽高亮色」当预览；运行时由代码按实际当前槽决定。\n" +
                  "· 图标在编辑期是空的（运行时按背包内容填）；血包数量的占位数字是 2。\n" +
                  "· 想回到代码生成：把 HUD 上 hotbar 引用清空即可。");
    }

    // ==================== 工具函数 ====================

    static void Assign(SerializedObject so, string field, Object value)
    {
        var p = so.FindProperty(field);
        if (p != null) p.objectReferenceValue = value;
    }

    /// <summary>建一个 RectTransform 子节点：锚在父节点**底部中心**、pivot 居中，按 anchoredPosition 摆放</summary>
    static RectTransform MakeRect(string name, Transform parent, Vector2 size, Vector2 pos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        return rt;
    }

    /// <summary>建一个 Image：`sprite` 为 null 时**整节点关掉**（没有图的 Image 会画成白方块）</summary>
    static Image MakeImage(Transform parent, string name, Vector2 size, Vector2 pos, Sprite sprite, Color color, bool sliced)
    {
        var img = MakeRect(name, parent, size, pos).gameObject.AddComponent<Image>();
        img.sprite = sprite;
        if (sliced) img.type = Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = false;   // 纯显示：快捷栏不做点击（键盘 1/2/3/Q 才是入口）
        if (sprite == null) img.gameObject.SetActive(false);
        return img;
    }

    static Text MakeText(Transform parent, string name, string label, int size, Color color)
    {
        var rt = MakeRect(name, parent, new Vector2(KeyW, KeyH), Vector2.zero);
        var t = rt.gameObject.AddComponent<Text>();
        // 字体（与运行时同源：都优先 GameAssets.uiFont）+ 强制不截断：都走共用实现
        // ⚠️ 不截断必须有：中文大行高字体（思源黑体）的行高会超过小圆角矩形高度 → Truncate 会把整个字**直接不画**
        UIUtil.ApplyFont(t, UISpriteToolUtil.PreviewFont());
        t.text = label;
        t.fontSize = size;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = color;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        return t;
    }

    /// <summary>让子节点铺满父节点 —— ⚠️ `child` 必须**已经是** `parent` 的子节点（本函数只调锚点、**不重挂父子**；
    /// 传错父子关系 = 铺满的是别的节点，键位/数量文字就会跑到槽中央）。</summary>
    static void Stretch(RectTransform child, RectTransform parent)
    {
        if (child == null || parent == null) return;
        child.anchorMin = Vector2.zero;
        child.anchorMax = Vector2.one;
        child.offsetMin = Vector2.zero;
        child.offsetMax = Vector2.zero;
    }
}
