using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 特效工具：枪口火光、命中火花、地面弹孔，以及无光照材质的获取。
/// 全部是静态方法，不需要挂到任何物体上。
/// 每个特效都优先使用 GameAssets 里配置的 Prefab，没配置则用代码生成默认外观。
/// </summary>
public static class Effects
{
    /// <summary>材质颜色属性 ID（MaterialPropertyBlock 用，避免每帧拼字符串）</summary>
    public static readonly int ColorProp = Shader.PropertyToID("_Color");

    static Material _unlitTemplate;
    static Texture2D _whiteTex;

    /// <summary>1x1 白色贴图（给需要贴图的透明着色器补底，避免渲染成黑色）</summary>
    static Texture2D WhiteTex
    {
        get
        {
            if (_whiteTex == null)
            {
                _whiteTex = new Texture2D(1, 1);
                _whiteTex.SetPixel(0, 0, Color.white);
                _whiteTex.Apply();
            }
            return _whiteTex;
        }
    }

    /// <summary>创建一个新的无光照纯色材质。每次调用都会新建实例，仅供 Shared 内部使用。</summary>
    static Material Unlit(Color c)
    {
        if (_unlitTemplate == null)
        {
            // 首选 Unlit/Color：它直接输出 _Color，不依赖贴图和顶点色，任何网格都能正确显示颜色。
            // 注意不要用 Sprites/Default 打头：它依赖网格顶点色和贴图，用在普通网格上会渲染成黑色。
            Shader sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Standard");
            _unlitTemplate = new Material(sh);

            // 万一最终落到 Sprites/Default，补一张纯白 1x1 贴图，否则贴图为空会导致渲染发黑
            if (_unlitTemplate.HasProperty("_MainTex") && _unlitTemplate.mainTexture == null)
                _unlitTemplate.mainTexture = WhiteTex;
        }

        var m = new Material(_unlitTemplate);
        if (m.HasProperty("_Color")) m.color = c;
        if (m.HasProperty("_MainColor")) m.SetColor("_MainColor", c);
        return m;
    }

    static readonly Dictionary<Color, Material> _sharedCache = new Dictionary<Color, Material>();
    static readonly Dictionary<Color, Material> _alphaCache = new Dictionary<Color, Material>();
    static Material _alphaTemplate;

    /// <summary>
    /// 按颜色缓存的共享材质：同一种颜色全局复用同一份，避免每个物体都 new 一个材质。
    /// 只用于「颜色在运行时不会改变」的物体（子弹、特效、模型部件）。
    /// 需要运行时改色的（受击白闪、血条填充）请改用 SetColor，不要直接改材质。
    /// </summary>
    public static Material Shared(Color c)
    {
        Material m;
        if (_sharedCache.TryGetValue(c, out m) && m != null) return m;
        m = Unlit(c);
        _sharedCache[c] = m;
        return m;
    }

    /// <summary>
    /// 半透明共享材质（弹孔/血条背景等需要 alpha 混合的物体用）。
    /// Unlit/Color 不支持混合：带 alpha 的颜色仍渲染成不透明、淡出动画也不生效。
    /// 这里用 Sprites/Default（自带 alpha 混合）+ 白图打底，按颜色缓存，MPB 改色不影响材质本体。
    /// </summary>
    public static Material SharedAlpha(Color c)
    {
        Material m;
        if (_alphaCache.TryGetValue(c, out m) && m != null) return m;
        if (_alphaTemplate == null)
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            _alphaTemplate = new Material(sh);
            if (_alphaTemplate.HasProperty("_MainTex") && _alphaTemplate.mainTexture == null)
                _alphaTemplate.mainTexture = WhiteTex;   // 空贴图会发黑
        }
        m = new Material(_alphaTemplate);
        if (m.HasProperty("_Color")) m.color = c;
        if (m.HasProperty("_MainColor")) m.SetColor("_MainColor", c);
        _alphaCache[c] = m;
        return m;
    }

    /// <summary>
    /// 用 MaterialPropertyBlock 给渲染器改颜色。
    /// 这是 Unity 推荐做法：只覆盖这一次绘制，不复制材质，所以不会产生材质实例泄漏。
    /// block 由调用方缓存复用（通常一个字段即可），避免每次分配。
    /// </summary>
    public static void SetColor(Renderer r, MaterialPropertyBlock block, Color c)
    {
        if (r == null || block == null) return;
        r.GetPropertyBlock(block);
        block.SetColor(ColorProp, c);
        r.SetPropertyBlock(block);
    }

    /// <summary>
    /// 半透明纯色材质（线条/瞄准线用，比如狙击手的红色预警线、爆炸范围圈）。
    /// Sprites/Default 优先：内置 Alpha 混合 + 有 _Color 属性，颜色透明度都能直接控制；
    /// Unlit/Transparent 在团结引擎里没有 _Color，只做次级兜底。
    /// 注意：每次调用都新建实例，调用方用完直接丢弃即可（特效材质量小，无需缓存）。
    /// </summary>
    public static Material LineMaterial(Color c)
    {
        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Transparent");
        if (sh == null) sh = Shader.Find("Unlit/Color");

        var m = new Material(sh);
        // 用 HasProperty 探测：不同引擎/着色器的 _Color 属性不一定存在，绝不能直接 get/set
        if (m.HasProperty("_Color")) m.color = c;
        if (m.HasProperty("_MainColor")) m.SetColor("_MainColor", c);
        // 透明混合依赖 _MainTex 的 alpha 输出，空贴图会发黑，补一张纯白
        if (m.HasProperty("_MainTex") && m.mainTexture == null) m.mainTexture = WhiteTex;
        return m;
    }

    /// <summary>
    /// 地面爆炸范围圈：半透明橙红圆环，出现后快速扩散并淡出。
    /// ⚠️ **已从「LineRenderer + 每次新建材质」改成「池化 Quad + 共享圆环贴图」**：
    /// 以前每次爆炸都要 new 一个 GameObject + LineRenderer + 一份材质 + 49 个点，
    /// 现在走对象池、材质全局共用（动画交给 `TimedEffect` 的缩放 + 淡出）。
    /// 视觉差异：圆环线宽会随半径一起变粗（旧版是固定宽度），观感更接近"冲击波"。
    /// </summary>
    public static void BlastRing(Vector3 center, float radius)
    {
        var go = ObjectPool.Get(BlastRingPoolKey);
        if (go == null)
        {
            go = CreateMeshObject(PrimitiveType.Quad, "BlastRing", null, RingMaterial());
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // 躺平在地面上
            SetFxLayer(go);
            var fx0 = go.AddComponent<TimedEffect>();
            fx0.poolKey = BlastRingPoolKey;
            fx0.fadeOut = true;
            fx0.fadeLight = false;
        }

        go.transform.position = new Vector3(center.x, 0.06f, center.z);   // 略高于地面，避免和地面贴图闪烁

        var fx = go.GetComponent<TimedEffect>();
        fx.lifetime = 0.35f;
        // TimedEffect 用的是**绝对**缩放，所以这里按直径给起止值（0.35 倍 → 1.5 倍半径，与旧版扩散幅度一致）
        fx.startScale = radius * 2f * 0.35f;
        fx.endScale = radius * 2f * 1.5f;
    }

    /// <summary>清除 MaterialPropertyBlock 的颜色覆盖，让渲染器恢复使用材质本身的颜色</summary>
    public static void ClearColor(Renderer r)
    {
        if (r != null) r.SetPropertyBlock(null);
    }

    /// <summary>把特效物体（含子物体）归入 FX 层。
    /// ⚠️ 特效的碰撞体都会在创建时移除，所以分层**不影响物理**；分层的意义是"语义明确"——
    /// 将来若给特效加碰撞体/触发器，子弹（BulletMask）和视线（SightMask）都不会误伤它。</summary>
    static void SetFxLayer(GameObject go)
    {
        if (go == null || Layers.FX < 0) return;
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layers.FX;
    }

    /// <summary>特效池键前缀：与「资源表留空」时程序化兜底用的 "Impact"/"MuzzleFlash" 池键区分开</summary>
    const string FxPoolPrefix = "FX/";

    // ---------------- 共享图元网格（不要再用 GameObject.CreatePrimitive 造显示物） ----------------
    // ⚠️ `GameObject.CreatePrimitive` 每次调用都会**新建一份网格**（且会带一个碰撞体，项目各处都在删它）。
    // 血条 / 拾取物 / 代码兜底身体 / 特效球这类"每次生成都要造图元"的地方会持续产垃圾，
    // 而且这些网格要等场景卸载才回收。这里统一改成"共享网格只建一次、全部实例共用"。
    // （项目早就为子弹这么做过，见 `Bullet._cubeMesh`；现在把这条经验推广到全项目。）
    static readonly Dictionary<PrimitiveType, Mesh> _meshCache = new Dictionary<PrimitiveType, Mesh>();

    /// <summary>取内置图元的共享网格（只创建一次）。</summary>
    public static Mesh SharedMesh(PrimitiveType type)
    {
        Mesh m;
        if (_meshCache.TryGetValue(type, out m) && m != null) return m;

        var tmp = GameObject.CreatePrimitive(type);
        m = tmp.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(tmp);   // 只取网格：模板物体立刻销毁（网格本身不随物体销毁）
        _meshCache[type] = m;
        return m;
    }

    /// <summary>
    /// 用共享网格建一个**纯显示**物体（**没有碰撞体**）。
    /// 替代 `GameObject.CreatePrimitive`：不再产出每实例网格，也不必再"创建完马上删碰撞体"。
    /// 需要碰撞体请显式 AddComponent。
    /// </summary>
    public static GameObject CreateMeshObject(PrimitiveType type, string name, Transform parent, Material mat)
    {
        var go = new GameObject(name);
        go.AddComponent<MeshFilter>().sharedMesh = SharedMesh(type);
        var mr = go.AddComponent<MeshRenderer>();
        if (mat != null) mr.sharedMaterial = mat;
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }

    // ---------------- 爆炸范围圈（池化 Quad + 程序生成的圆环贴图） ----------------
    const string BlastRingPoolKey = "BlastRing";
    static Texture2D _ringTex;
    static Material _ringMat;

    /// <summary>程序生成圆环贴图（白色 + alpha），只建一次，供所有爆炸圈共用。</summary>
    static Texture2D RingTexture()
    {
        if (_ringTex != null) return _ringTex;

        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float c = (size - 1) * 0.5f;
        float outer = c * 0.98f;
        float inner = c * 0.80f;
        float feather = Mathf.Max(1f, c * 0.05f);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float a = Mathf.Clamp01((outer - d) / feather) * Mathf.Clamp01((d - inner) / feather);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        _ringTex = tex;
        return _ringTex;
    }

    /// <summary>爆炸圈专用共享材质（支持 alpha 混合，`TimedEffect` 才能淡出）。</summary>
    static Material RingMaterial()
    {
        if (_ringMat != null) return _ringMat;
        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Transparent");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        _ringMat = new Material(sh);
        _ringMat.mainTexture = RingTexture();
        if (_ringMat.HasProperty(ColorProp)) _ringMat.color = new Color(1f, 0.55f, 0.1f, 0.95f);
        return _ringMat;
    }

    /// <summary>染色用的共享 MaterialPropertyBlock（只做一次性覆盖，用完不留引用）</summary>
    static MaterialPropertyBlock _tintBlock;

    /// <summary>
    /// 池化生成一个特效 Prefab：首次 Instantiate 出模板并记下池键，之后一律从对象池复用。
    /// 这是「资源表已配置」这条路径的性能关键——以前这里是每次 Instantiate + Destroy（池化形同虚设）。
    /// - startScale/endScale：根节点缩放动画（爆炸大火花 2.6 必须明显大于命中火花 0.5）
    /// - tint：非空则覆盖子 Renderer 颜色（曳光色染色等）；留空沿用 Prefab 自带材质
    /// - fadeOut：透明淡出。Prefab 常见的 Unlit/Color 不支持 alpha 混合，直接写 alpha 毫无视觉效果，
    ///   所以这里会换成支持混合的共享材质（Effects.SharedAlpha）
    /// </summary>
    static void SpawnPrefabEffect(GameObject prefab, Vector3 pos, Quaternion rot, float lifetime,
                                  float startScale, float endScale, bool fadeOut, Color? tint)
    {
        // 粒子系统特效不做池化：它靠 Play/Stop 播放，复用语义与纯网格特效不同 → 走「播完即销毁」
        if (prefab.GetComponentInChildren<ParticleSystem>() != null)
        {
            var once = Object.Instantiate(prefab, pos, rot);
            once.name = "FX_" + prefab.name;
            SetFxLayer(once);
            Object.Destroy(once, lifetime + 0.5f);
            return;
        }

        string key = FxPoolPrefix + prefab.name;
        var go = ObjectPool.Get(key);
        if (go == null)
        {
            // 模板只建这一次，之后全靠池复用
            go = Object.Instantiate(prefab);
            go.name = "FX_" + prefab.name;
            SetFxLayer(go);
            // 防御：若预制体在磁盘上被保存成"未激活"，实例化后特效会看不见，
            // 而且 ObjectPool.Release 的"已失活即视为已归还"守卫会把它永久留在池外 → 这里显式激活。
            go.SetActive(true);
            // 特效不应参与物理/射线（内置三个预制体本就没有碰撞体，这里是防御性清理）
            var cols = go.GetComponentsInChildren<Collider>();
            for (int i = 0; i < cols.Length; i++) Object.DestroyImmediate(cols[i]);
        }

        // 若 prefab 已经带了 TimedEffect（例如保存自运行时的特效对象），不要重复挂第二个计时器
        var fx = go.GetComponent<TimedEffect>();
        if (fx == null) fx = go.AddComponent<TimedEffect>();
        fx.poolKey = key;
        fx.lifetime = lifetime;
        fx.startScale = startScale;
        fx.endScale = endScale;
        fx.fadeOut = fadeOut;
        fx.fadeLight = false;

        go.transform.SetPositionAndRotation(pos, rot);

        var r = go.GetComponentInChildren<MeshRenderer>();
        if (r == null) return;

        if (fadeOut)
        {
            Color baseColor = tint ?? (r.sharedMaterial != null && r.sharedMaterial.HasProperty(ColorProp)
                ? r.sharedMaterial.color : Color.white);
            r.sharedMaterial = SharedAlpha(baseColor);
        }
        else if (tint.HasValue)
        {
            if (_tintBlock == null) _tintBlock = new MaterialPropertyBlock();
            SetColor(r, _tintBlock, tint.Value);
        }
    }

    /// <summary>命中火花：快速缩小消失的小球（对象池复用，避免高频创建销毁）</summary>
    public static void Impact(Vector3 pos, Color c, float size)
    {
        var assets = GameAssets.I;
        if (assets != null && assets.impactPrefab != null)
        {
            // 尺寸必须传进去：爆炸的大火花（size=2.6）不能和命中的小火花（size=0.5）一样大；
            // 颜色跟随武器曳光色（这两点以前在 Prefab 路径上被丢掉了）
            SpawnPrefabEffect(assets.impactPrefab, pos, Quaternion.identity, 0.18f,
                              size, 0.25f * size, false, c);
            return;
        }

        // 从池取小球；没有则创建并挂上回池标记的 TimedEffect
        var go = ObjectPool.Get("Impact");
        if (go == null)
        {
            go = CreateMeshObject(PrimitiveType.Sphere, "Impact", null, null);
            SetFxLayer(go);
            var fx = go.AddComponent<TimedEffect>();
            fx.poolKey = "Impact";
        }

        go.transform.position = pos;
        go.transform.localScale = Vector3.one * 0.18f * size;
        go.GetComponent<MeshRenderer>().sharedMaterial = Shared(c);

        var f = go.GetComponent<TimedEffect>();
        f.lifetime = 0.18f;
        f.startScale = 0.2f * size;
        f.endScale = 0.05f;
        f.fadeOut = false;
        f.fadeLight = false;
    }

    /// <summary>枪口火光：黄色小球 + 一盏瞬间点光源</summary>
    public static void MuzzleFlash(Vector3 pos, Vector3 dir)
    {
        var assets = GameAssets.I;
        if (assets != null && assets.muzzleFlashPrefab != null)
        {
            // 火光颜色由预制体材质决定（Mat_Effect_Spark 本就是黄色），不需要额外染色
            SpawnPrefabEffect(assets.muzzleFlashPrefab, pos + dir * 0.25f, Quaternion.LookRotation(dir),
                              0.06f, 1f, 0.25f, false, null);
            return;
        }

        // 火光小球走对象池（与 Impact 同模式：高频开火，避免每次新建/销毁 GameObject）
        var go = ObjectPool.Get("MuzzleFlash");
        if (go == null)
        {
            go = CreateMeshObject(PrimitiveType.Sphere, "MuzzleFlash", null, null);
            SetFxLayer(go);
            var fx0 = go.AddComponent<TimedEffect>();
            fx0.poolKey = "MuzzleFlash";
        }
        go.transform.position = pos + dir * 0.25f;
        go.transform.localScale = Vector3.one * 0.22f;
        go.GetComponent<MeshRenderer>().sharedMaterial = Shared(new Color(1f, 0.9f, 0.3f));

        var fx = go.GetComponent<TimedEffect>();
        fx.lifetime = 0.06f;
        fx.startScale = 0.22f;
        fx.endScale = 0.02f;
        fx.fadeLight = false;
        fx.fadeOut = false;

        var lightGo = new GameObject("MuzzleLight");
        lightGo.transform.position = pos;
        SetFxLayer(lightGo);
        var li = lightGo.AddComponent<Light>();
        li.type = LightType.Point;
        li.color = new Color(1f, 0.85f, 0.4f);
        li.range = 5f;
        li.intensity = 2.2f;

        var lf = lightGo.AddComponent<TimedEffect>();
        lf.lifetime = 0.07f;
        lf.fadeLight = true;
    }

    /// <summary>地面弹孔/血迹：贴在地面、慢慢淡出的装饰</summary>
    public static void Decal(Vector3 pos, Color c)
    {
        var groundPos = new Vector3(pos.x, 0.02f, pos.z);
        float yaw = Random.Range(0f, 360f);

        var assets = GameAssets.I;
        if (assets != null && assets.decalPrefab != null)
        {
            // 预制体本身已经躺平（X 旋转 90 度），这里只加一个水平随机朝向。
            // 缩放保持 1 不变：淡出期间再缩放会让弹孔"缩没"而不是"渐隐"
            SpawnPrefabEffect(assets.decalPrefab, groundPos, Quaternion.Euler(0f, yaw, 0f),
                              4f, 1f, 1f, true, c);
            return;
        }

        var go = CreateMeshObject(PrimitiveType.Quad, "Decal", null, null);
        SetFxLayer(go);
        go.transform.position = groundPos;
        go.transform.rotation = Quaternion.Euler(90f, yaw, 0f);
        float s = Random.Range(0.12f, 0.22f);
        go.transform.localScale = new Vector3(s, s, 1f);
        go.GetComponent<MeshRenderer>().sharedMaterial = SharedAlpha(c);   // 半透明材质，淡出才生效

        var fx = go.AddComponent<TimedEffect>();
        fx.lifetime = 4f;
        // TimedEffect 每帧会把 localScale 写成 Vector3.one * Lerp(start, end)，
        // 所以实际尺寸必须写进 startScale/endScale（否则上面那行 localScale 会被覆盖成 1 米大方块）。
        // 淡出期间不缩放（start = end）：弹孔保持原尺寸渐隐，而不是缩小消失。
        fx.startScale = s;
        fx.endScale = s;
        fx.fadeOut = true;
    }
}
