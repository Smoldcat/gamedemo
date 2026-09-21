using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// 地图尺寸调整工具：菜单 `Tools/游戏/地图尺寸…`。
///
/// 一键改「Ground 的地面大小 + 四面围墙的位置/长度」（按同样比例），可选把 `Crate*` 箱子按比例摊开、
/// 并把 Ground 接到 `WaveManager.mapGround`。**全程可 Ctrl+Z 撤销**。
///
/// 为什么做工具而不是手改：地图尺寸是会反复试的参数（40 → 60 → 100…），手改 5 个物体的缩放/位置又慢又容易错；
/// 而且直接改场景文件在 Unity 开着的时候还可能被覆盖。工具是"点一下 + 可撤销"，安全得多。
///
/// ⚠️ 本工具**只改场景几何**，不碰代码里写死的刷怪范围（见下方说明）。改完记得 `Ctrl + S` 保存场景。
/// </summary>
public class MapResizerWindow : EditorWindow
{
    float _size = 100f;      // 地图边长（米）
    float _inset = 2f;       // 墙心距地面边缘的距离（米）
    bool _spreadCrates = true;
    bool _wireGround = true;

    [MenuItem("Tools/游戏/地图尺寸…")]
    static void Open()
    {
        var w = GetWindow<MapResizerWindow>(true, "地图尺寸");
        w.minSize = new Vector2(380f, 250f);
        w.Show();
    }

    void OnEnable()
    {
        // 打开时读当前地面尺寸当默认值
        var ground = GameObject.Find("Ground");
        if (ground != null)
        {
            float cur = ground.transform.localScale.x;
            if (cur > 1f) _size = Mathf.Round(cur);
        }
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "会改：Ground 的 XZ 缩放 + 四面 Wall_* 的位置与长度（保持相对比例）。\n" +
            "可选：把 Crate* 箱子的 XZ 位置按同样比例摊开、把 Ground 接到 WaveManager。\n" +
            "全程可 Ctrl+Z 撤销；改完记得保存场景（Ctrl+S）。\n\n" +
            "⚠️ 本工具**不改**代码里写死的刷怪范围（敌人 ±16 / 血包 ±14 / 武器 ±13）——\n" +
            "地图放大后敌人仍只会刷在中心，需要另行处理（见交接文档）。",
            MessageType.Info);

        EditorGUILayout.Space();
        _size = EditorGUILayout.FloatField("地图边长（米）", _size);
        _inset = EditorGUILayout.FloatField("墙心距边缘（米）", _inset);
        EditorGUILayout.LabelField(" ", "→ 战斗区（墙内侧）= " + Mathf.Max(_size - _inset * 2f - 1f, 0f).ToString("0.#") + " × 同值（米）");

        EditorGUILayout.Space();
        _spreadCrates = EditorGUILayout.Toggle("同时摊开箱子（Crate*）", _spreadCrates);
        _wireGround = EditorGUILayout.Toggle("把 Ground 接到 WaveManager", _wireGround);

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(_size < 10f))
            if (GUILayout.Button("应用", GUILayout.Height(30f))) Apply();
    }

    void Apply()
    {
        if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("地图尺寸", "请先退出 Play 模式再改场景。", "好");
            return;
        }

        var ground = GameObject.Find("Ground");
        if (ground == null)
        {
            EditorUtility.DisplayDialog("地图尺寸", "场景里找不到名为「Ground」的物体。\n（本工具按这个名字找地面）", "好");
            return;
        }

        float oldSize = Mathf.Max(ground.transform.localScale.x, 0.001f);
        float k = _size / oldSize;                                  // 缩放比例（箱子摊开用）
        float inset = Mathf.Clamp(_inset, 0.5f, Mathf.Max(_size * 0.5f - 1f, 0.5f));

        Undo.SetCurrentGroupName("调整地图尺寸");
        int group = Undo.GetCurrentGroup();

        // ---- 地面 ----
        var gt = ground.transform;
        Undo.RecordObject(gt, "调整地图尺寸");
        gt.localScale = new Vector3(_size, gt.localScale.y, _size);

        // ---- 四面墙 ----
        PlaceWall("Wall_Front", true,  +1f, _size, inset);
        PlaceWall("Wall_Back",  true,  -1f, _size, inset);
        PlaceWall("Wall_Right", false, +1f, _size, inset);
        PlaceWall("Wall_Left",  false, -1f, _size, inset);

        // ---- 箱子按比例摊开 ----
        int crates = 0;
        if (_spreadCrates)
        {
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root == null || !root.name.StartsWith("Crate")) continue;
                var t = root.transform;
                Undo.RecordObject(t, "调整地图尺寸");
                var p = t.localPosition;
                p.x *= k;
                p.z *= k;
                t.localPosition = p;
                crates++;
            }
        }

        // ---- 接 WaveManager.mapGround ----
        string groundNote = "";
        if (_wireGround)
        {
            var wm = Object.FindObjectOfType<WaveManager>();
            if (wm != null)
            {
                Undo.RecordObject(wm, "调整地图尺寸");
                wm.mapGround = gt;
                EditorUtility.SetDirty(wm);
                // 顺手填「地面物体 Tag」——但地面现在是 Untagged（工程里也没定义 Tag），
                // 所以**场景重载后这个引用不会自动重绑**。要长期稳定：先在 TagManager 定义一个 Tag、
                // 给 Ground 设上、再在 WaveManager 的「地面物体 Tag」里填同名（见交接文档 §6.5）。
                if (!string.IsNullOrEmpty(ground.tag) && ground.tag != "Untagged")
                    wm.mapGroundTag = ground.tag;
                else
                    groundNote = "\n⚠️ Ground 的 Tag 是 Untagged → 已接 mapGround，但**场景重载后不会自动重绑**（要长期稳定需先定义一个 Tag 并填到 WaveManager 的「地面物体 Tag」）。";
            }
            else groundNote = "\n⚠️ 场景里没找到 WaveManager，未接 mapGround。";
        }

        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Undo.CollapseUndoOperations(group);

        Debug.Log(string.Format(
            "[MapResizer] 地图尺寸 → {0}×{0} 米（原 {1}）。\n· 墙心距边缘 {2} 米 → 战斗区约 {3}×{3} 米\n· 摊开箱子 {4} 个\n· 记得 Ctrl+S 保存场景{5}",
            _size, oldSize, inset, Mathf.Max(_size - inset * 2f - 1f, 0f), crates, groundNote));
    }

    /// <summary>摆放一面墙。alongX = 墙身沿 X 方向延伸（横墙，站在 ±Z 上）；否则沿 Z 延伸（竖墙，站在 ±X 上）。</summary>
    static void PlaceWall(string name, bool alongX, float sign, float size, float inset)
    {
        var go = GameObject.Find(name);
        if (go == null)
        {
            Debug.LogWarning("[MapResizer] 找不到「" + name + "」，已跳过。");
            return;
        }
        var t = go.transform;
        Undo.RecordObject(t, "调整地图尺寸");

        float c = size * 0.5f - inset;      // 墙心到原点的距离
        var p = t.localPosition;
        var s = t.localScale;
        if (alongX) { p.z = sign * c; s.x = size; }   // 横墙：沿 X 铺开，站在 ±Z
        else        { p.x = sign * c; s.z = size; }   // 竖墙：沿 Z 铺开，站在 ±X
        t.localPosition = p;
        t.localScale = s;
    }
}
