using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// 把「拾取气泡」**预置到场景里**（一键"从代码里剥离出来"），之后就能在编辑器里直接调。
///
/// 为什么需要这个工具：气泡原来是 `HUD` 运行时用代码 `new` 出来的 —— 编辑器里看不见、也就没法调。
/// 菜单 `Tools/游戏/生成拾取气泡到场景` 会在 HUD 的 Canvas 下建一个 `PickupBubble` 节点：
///   · 用 `HorizontalLayoutGroup` + `ContentSizeFitter` 让**宽度随名称长度自适应**（所以布局不用代码算）；
///   · 把 HUD 上「拾取气泡」那组引用**自动接好**；
///   · 顺带把圆角底图 / 描边 / 圆形按键图**生成成正式的 PNG 资源**（`Assets/Art/UI/PickupBubble/`），这样编辑期就能看到真实观感。
///
/// 之后：**颜色 / 字号 / 图片 / 尺寸都在节点上调**（代码只负责填「名称 / 按键字母」和每帧的屏幕位置）。
/// ⚠️ 想回到"代码生成"：把 HUD 上「拾取气泡」那组引用清空即可（HUD 会自动退回代码生成，游戏照常跑）。
/// </summary>
public static class PickupBubbleBuilder
{
    // 与 HUD 的默认值保持一致（改这里不影响运行时代码生成路径的默认值，只影响本工具生成的节点）
    const float Height = 38f, PadX = 16f, Gap = 8f, KeyD = 24f, RadiusPx = 12f;

    [MenuItem("Tools/游戏/生成拾取气泡到场景")]
    static void Build()
    {
        var hud = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponentInParent<HUD>()
            : null;
        if (hud == null) hud = Object.FindObjectOfType<HUD>();
        if (hud == null)
        {
            EditorUtility.DisplayDialog("生成拾取气泡",
                "场景里找不到 HUD 组件。\n请先选中带 HUD 的物体（一般是场景里的 Canvas），或确认场景里有 HUD。", "好");
            return;
        }

        var so = new SerializedObject(hud);
        var bubbleProp = so.FindProperty("pickupBubble");
        if (bubbleProp != null && bubbleProp.objectReferenceValue != null)
        {
            if (!EditorUtility.DisplayDialog("生成拾取气泡",
                "HUD 上已经配好拾取气泡了。\n要继续的话会重建一个并覆盖引用（旧的 PickupBubble 节点会被删掉）。", "重建", "取消"))
                return;
        }

        var canvas = hud.GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            EditorUtility.DisplayDialog("生成拾取气泡", "HUD 所在物体上找不到 Canvas。", "好");
            return;
        }

        Undo.SetCurrentGroupName("生成拾取气泡");
        int group = Undo.GetCurrentGroup();

        // 旧的（按名字找）先删掉，避免同名的残留
        var old = canvas.transform.Find("PickupBubble");
        if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

        // ---------- 贴图资源（生成一次即可复用；失败就退回"运行时自动补图"） ----------
        var bgSprite = EnsureSpriteAsset("BubbleBg.png", UIUtil.RoundedRectSprite(64, RadiusPx, 0f), new Vector4(RadiusPx, RadiusPx, RadiusPx, RadiusPx));
        var edgeSprite = EnsureSpriteAsset("BubbleEdge.png", UIUtil.RoundedRectSprite(64, RadiusPx, 2f), new Vector4(RadiusPx, RadiusPx, RadiusPx, RadiusPx));
        var keySprite = EnsureSpriteAsset("BubbleKey.png", UIUtil.CircleSprite(48), Vector4.zero);

        // ---------- 节点 ----------
        var root = MakeRect("PickupBubble", canvas.transform);
        Undo.RegisterCreatedObjectUndo(root.gameObject, "生成拾取气泡");

        // 自动撑宽：名称多长、气泡就多宽（宽度不用在代码里手算）
        var hlg = root.gameObject.AddComponent<HorizontalLayoutGroup>();
        int padY = Mathf.RoundToInt((Height - KeyD) * 0.5f);   // 上下留白 → 总高 = 名称/按键高度 + padY*2 = Height
        hlg.padding = new RectOffset(Mathf.RoundToInt(PadX), Mathf.RoundToInt(PadX), padY, padY);
        hlg.spacing = Gap;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;
        var csf = root.gameObject.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // 外圈描边（先建 → 渲染在底层）：忽略布局、铺满根节点并外扩 3px
        var edge = MakeRect("Edge", root, out var edgeLe);
        var edgeImg = edge.gameObject.AddComponent<Image>();
        edgeImg.sprite = edgeSprite;
        edgeImg.type = Image.Type.Sliced;
        edgeImg.color = HUD.BubbleEdgeColor;
        edgeImg.raycastTarget = false;
        edgeLe.ignoreLayout = true;
        Stretch(edge, -3f);

        // 内层填充 + 投影
        var bg = MakeRect("Bg", root, out var bgLe);
        var bgImg = bg.gameObject.AddComponent<Image>();
        bgImg.sprite = bgSprite;
        bgImg.type = Image.Type.Sliced;
        bgImg.color = HUD.BubbleBgColor;
        bgImg.raycastTarget = false;
        bgLe.ignoreLayout = true;
        Stretch(bg, 0f);
        var shadow = bg.gameObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.28f);
        shadow.effectDistance = new Vector2(0f, -3f);

        // 物品名（靠左；宽度由文本自适应）
        // 占位文字用「武器名」：预览字体优先取 GameAssets.uiFont（含中文），所以编辑期就能看到真实观感；
        // 运行时 HUD 会写入真实名称（字体与编辑期同源）。
        var nameRt = MakeRect("Name", root, out _);
        var nameTxt = nameRt.gameObject.AddComponent<Text>();
        nameTxt.text = "武器名";
        nameTxt.font = PreviewFont();
        nameTxt.fontSize = HUD.BubbleFontSize;
        nameTxt.color = HUD.BubbleTextColor;
        nameTxt.alignment = TextAnchor.MiddleLeft;
        nameTxt.horizontalOverflow = HorizontalWrapMode.Overflow;
        nameTxt.verticalOverflow = VerticalWrapMode.Overflow;
        nameTxt.raycastTarget = false;

        // 按键圆标（靠右）
        var key = MakeRect("KeyBg", root, out var keyLe);
        key.sizeDelta = new Vector2(KeyD, KeyD);
        keyLe.preferredWidth = KeyD;
        keyLe.preferredHeight = KeyD;
        var keyImg = key.gameObject.AddComponent<Image>();
        keyImg.sprite = keySprite;
        keyImg.color = HUD.BubbleKeyBgColor;
        keyImg.raycastTarget = false;

        var keyTextRt = MakeRect("KeyText", key);
        Stretch(keyTextRt, 0f);
        var keyTxt = keyTextRt.gameObject.AddComponent<Text>();
        keyTxt.text = "E";
        keyTxt.font = PreviewFont();
        keyTxt.fontSize = HUD.BubbleFontSize - 2;
        keyTxt.color = HUD.BubbleKeyTextColor;
        keyTxt.alignment = TextAnchor.MiddleCenter;
        keyTxt.raycastTarget = false;
        // ⚠️ 必须给 Overflow：Text 默认是 Truncate，而**中文/大行高字体（如思源黑体）的行高会超过圆标高度** →
        //    一旦"行高 > 框高"，Truncate 会把整个字**直接不画**（表现为"字母不见了"，编辑器用内置字体时却正常）。
        keyTxt.horizontalOverflow = HorizontalWrapMode.Overflow;
        keyTxt.verticalOverflow = VerticalWrapMode.Overflow;

        // ⚠️ 不用 `UIUtil.UILayer`：它是 internal，编辑器程序集访问不到 → 这里自己取一次内置 UI 层
        int uiLayer = LayerMask.NameToLayer("UI");
        SetLayer(root.gameObject, uiLayer >= 0 ? uiLayer : 5);

        // ---------- 接线到 HUD ----------
        Assign(so, "pickupBubble", root);
        Assign(so, "pickupBubbleEdge", edgeImg);
        Assign(so, "pickupBubbleBg", bgImg);
        Assign(so, "pickupBubbleName", nameTxt);
        Assign(so, "pickupBubbleKeyBg", keyImg);
        Assign(so, "pickupBubbleKeyText", keyTxt);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(hud);
        EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);

        Undo.CollapseUndoOperations(group);
        Selection.activeGameObject = root.gameObject;

        Debug.Log("[PickupBubbleBuilder] 已生成「PickupBubble」并接到 HUD 的「拾取气泡」引用上。\n" +
                  "· 现在可以在编辑器里直接调：颜色 / 字号 / 尺寸 / 圆角图（贴图已生成到 Assets/Art/UI/PickupBubble/）。\n" +
                  "· 编辑期显示的是占位文字「武器名」；运行时 HUD 会写入真实名称（字体同源：都走 GameAssets.uiFont）。\n" +
                  "· 嫌它在编辑期碍事可以在 Inspector 里取消勾选（运行时显隐由 HUD 自己控制，不受影响）。\n" +
                  "· 想回到代码生成：把 HUD 上「拾取气泡」那组引用清空即可。" +
                  (bgSprite == null || edgeSprite == null || keySprite == null
                      ? "\n⚠️ 有贴图资源没生成成功（见上方警告）→ 那几张图会在运行时由代码自动补上，不影响游戏。"
                      : ""));
    }

    // ==================== 工具函数 ====================

    static void Assign(SerializedObject so, string field, Object value)
    {
        var p = so.FindProperty(field);
        if (p != null) p.objectReferenceValue = value;
    }

    static RectTransform MakeRect(string name, Transform parent) => MakeRect(name, parent, out _);

    static RectTransform MakeRect(string name, Transform parent, out LayoutElement le)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        le = go.AddComponent<LayoutElement>();
        return rt;
    }

    /// <summary>铺满父节点（inset 为负 → 比父节点更大，用于"描边比气泡大 3px"）</summary>
    static void Stretch(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    static void SetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++) SetLayer(go.transform.GetChild(i).gameObject, layer);
    }

    /// <summary>编辑期预览字体：优先用「资源总表」里配的**界面字体**（`GameAssets.uiFont`）——
    /// 于是编辑期看到的就和运行时**完全一致**（含中文）；没配才回落内置占位字体（无中文）。
    /// ⚠️ 运行时 HUD 会把字体统一注入为 `UIUtil.Font`（同样优先读 GameAssets.uiFont），所以两者同源。</summary>
    static Font PreviewFont()
    {
        var assets = GameAssets.I;
        if (assets != null && assets.uiFont != null) return assets.uiFont;

        var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return f;
    }

    /// <summary>按需创建（多级）目录，例如 `Assets/Art/UI/PickupBubble`</summary>
    static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
        string leaf = System.IO.Path.GetFileName(folder);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return;
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    /// <summary>
    /// 把一个运行时生成的 Sprite 落成正式 PNG 资源（存在就复用），并设好「Sprite + 九宫格 border + 透明」。
    /// 失败返回 null → 调用方退回"运行时由代码自动补图"，不会中断生成。
    /// </summary>
    static Sprite EnsureSpriteAsset(string fileName, Sprite runtime, Vector4 border)
    {
        const string dir = "Assets/Art/UI/PickupBubble";   // 气泡贴图单独成目录，别把 Assets/Art/UI 堆散
        string path = dir + "/" + fileName;
        try
        {
            if (!System.IO.File.Exists(path))
            {
                if (runtime == null || runtime.texture == null) return null;
                EnsureFolder(dir);
                System.IO.File.WriteAllBytes(path, runtime.texture.EncodeToPNG());
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }

            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null)
            {
                imp.textureType = TextureImporterType.Sprite;
                imp.spriteImportMode = SpriteImportMode.Single;
                imp.spriteBorder = border;          // 九宫格：拉伸时圆角不变形
                imp.alphaIsTransparency = true;
                imp.mipmapEnabled = false;
                imp.SaveAndReimport();
            }
            else AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[PickupBubbleBuilder] 生成贴图 " + fileName + " 失败：会退回\"运行时自动补图\"。" + e.Message);
            return null;
        }
    }
}
