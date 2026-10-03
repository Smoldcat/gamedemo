using UnityEngine;

/// <summary>
/// 玩家 X 光（挂在 Main Camera 上，配合 `Assets/Shaders/玩家X光.shader`）：
/// 玩家被遮挡时，把"玩家身体的轮廓"**逐像素地叠加画在遮挡物之上**——遮挡物一个像素都不动，
/// 所以既有"看得见玩家"，又不丢"这里有个障碍物"的空间感。
///
/// 工作方式（每帧一次，开销可忽略）：
///   · 取玩家身体（默认子节点 `Body`）的网格与变换 → `Graphics.DrawMesh` **再画一遍**
///     （不新建图层、不加第二相机、不改场景里的玩家对象）；
///   · 材质用 `Town/PlayerXRay`：`ZTest Always`（永远画在最前）+ **深度纹理逐像素比较**
///     →「该像素处的场景比幽灵更近」才画 ⇒ 只挡住左半边时，只有左半边出现幽灵，
///       右半边仍是真实的玩家 —— 两边自动拼成完整的人，不会重影、不会"整个变透明"；
///   · 因此**不需要射线检测**：可见性完全由深度比较决定，**与相机朝向、遮挡方向无关**（东南西北都一样）。
///
/// ⚠️ 约定与边界（都别随手改）：
///   · 相机需要**深度纹理**：`SyncDepthTexture()` 让 `DepthTextureMode.Depth` **跟随 `enable` 开关**
///     （开着功能才开深度；关掉功能立刻按**进入前的原值**归还 —— 既不白开开销，也不误关别人的深度需求）；
///   · 运行时材质自己 `new` 并 `Destroy`（不产生资产、不泄漏材质实例）；
///   · **零 static 状态**（不涉及 StaticReset）；层参数取网格自身所在层，保证相机一定渲染它；
///   · 默认**只画身体**：枪是玩家子物体且偏在身侧（会让轮廓中心偏离玩家真实位置），
///     想把枪/背包一起纳入就往 `extraMeshes` 里拖。
/// </summary>
[DefaultExecutionOrder(110)]   // 排在 CameraRig(默认 0) / CameraOcclusion(100) 之后
public class PlayerXRay : MonoBehaviour
{
    [Header("玩家 X 光（被遮挡时把玩家轮廓叠加在遮挡物上）")]
    [Tooltip("总开关。关闭时：不画幽灵，且相机的深度纹理会立刻归还（不留额外开销）。")]
    public bool enable = true;
    [Tooltip("要描轮廓的玩家。留空则自动找场景里的 PlayerController。")]
    public Transform target;
    [Tooltip("玩家身上作为轮廓的**子节点名**（默认 Body = 身体）。")]
    public string bodyName = "Body";
    [Tooltip("额外也要一起画成轮廓的网格（可选，例如以后想把 Gun / 背包也纳入）。\n" +
             "  ⚠️ 枪偏在身体右侧（localPos 约 0.45, 0.12, 0.45）→ 会让轮廓视觉中心偏离玩家实际位置；默认留空。")]
    public MeshFilter[] extraMeshes;

    [Header("外观（运行时实时生效，可随时调）")]
    [Tooltip("轮廓颜色。")]
    public Color color = new Color(0.45f, 0.85f, 1.0f, 1.0f);
    [Tooltip("整体透明度。")]
    [Range(0f, 1f)] public float alpha = 0.55f;
    [Tooltip("像素密度（网点）：0 = 完全不显示；0.45 ≈ 点阵质感（推荐）；1 = 实心。")]
    [Range(0f, 1f)] public float pixelDensity = 0.45f;
    [Tooltip("深度容差（米）：调大更保守（少显示）、调小更积极；贴面出现闪烁时调大。")]
    [Range(0f, 0.5f)] public float depthBias = 0.08f;
    [Tooltip("边缘提亮（菲涅尔）：让轮廓更立体；0 = 关闭。")]
    [Range(0f, 2f)] public float edgeGlow = 0.35f;

    Camera _cam;
    MeshFilter _body;
    Material _mat;
    DepthTextureMode _prevDepthMode;   // 进入前相机本来的深度需求（归还时按它还原，不误关别人）
    bool _depthOn;                     // 当前是否"由本组件开着"深度纹理（跟随 enable）

    void Awake()
    {
        _cam = GetComponent<Camera>();
        if (_cam == null) _cam = CameraCache.Main;
        if (_cam != null)
            _prevDepthMode = _cam.depthTextureMode;                 // 记下进入前的值
        else Debug.LogWarning("[PlayerXRay] 找不到相机（本组件应挂在 Main Camera 上）→ X 光不会生效。", this);

        if (target == null)
        {
            var pc = PlayerController.Instance;
            if (pc == null) pc = FindObjectOfType<PlayerController>();
            if (pc != null) target = pc.transform;
        }
        if (target != null)
        {
            var body = target.Find(bodyName);
            if (body != null) _body = body.GetComponent<MeshFilter>();
            if (_body == null) _body = target.GetComponentInChildren<MeshFilter>();   // 兜底：拿第一个网格
        }

        var sh = Shader.Find("Town/PlayerXRay");
        if (sh != null)
        {
            _mat = new Material(sh);
            _mat.name = "玩家X光（运行时）";
        }
        else Debug.LogWarning("[PlayerXRay] 找不到着色器 Town/PlayerXRay（是不是 玩家X光.shader 编译失败了？）→ X 光不会生效。", this);

        if (target == null) Debug.LogWarning("[PlayerXRay] 场景里没有 PlayerController → X 光不会生效。", this);
        else if (_body == null) Debug.LogWarning("[PlayerXRay] 玩家身上找不到名为「" + bodyName + "」的网格 → X 光不会生效。", this);

        // 只有在"真的能画"时才开深度纹理：找不到相机/着色器/网格就别白开开销
        bool canDraw = (_cam != null && _mat != null && _body != null);
        SyncDepthTexture(canDraw && enable);
    }

    /// <summary>
    /// 让"相机深度纹理"跟随 `enable` 开关：
    /// 开 → 按需打开（`Depth`）；关 → 按**进入前的原值**归还（不误关别人，也不留白开的开销）。
    /// </summary>
    void SyncDepthTexture(bool want)
    {
        if (_cam == null || want == _depthOn) return;
        _depthOn = want;
        if (want) _cam.depthTextureMode |= DepthTextureMode.Depth;
        else _cam.depthTextureMode = _prevDepthMode;
    }

    void LateUpdate()
    {
        SyncDepthTexture(enable);   // 运行中在 Inspector 关掉开关 → 深度纹理立刻归还

        if (!enable || _cam == null || _mat == null || _body == null || _body.sharedMesh == null) return;

        // 参数每帧写一次（运行中在 Inspector 调实时生效）
        _mat.SetColor("_Color", color);
        _mat.SetFloat("_Alpha", alpha);
        _mat.SetFloat("_Density", pixelDensity);
        _mat.SetFloat("_DepthBias", depthBias);
        _mat.SetFloat("_EdgeGlow", edgeGlow);

        Draw(_body);

        if (extraMeshes != null)
        {
            for (int i = 0; i < extraMeshes.Length; i++)
            {
                var mf = extraMeshes[i];
                if (mf == null || mf.sharedMesh == null) continue;
                Draw(mf);
            }
        }
    }

    /// <summary>把网格用 X 光材质再画一遍（层取网格自身所在层 → 相机一定会渲染它）。</summary>
    void Draw(MeshFilter mf)
    {
        if (_cam == null) return;   // 兜底：相机会引用为空时，传 null 会被画到"所有相机"上
        Graphics.DrawMesh(mf.sharedMesh, mf.transform.localToWorldMatrix, _mat,
                          mf.gameObject.layer, _cam, 0, null,
                          false, false, false);   // 不投影、不接收阴影、不用光照探针
    }

    void OnDisable()
    {
        // 按"进入前的原值"归还深度纹理需求，不误关别人（例如后处理）的深度
        SyncDepthTexture(false);
    }

    void OnDestroy()
    {
        if (_mat != null) Destroy(_mat);
    }
}
