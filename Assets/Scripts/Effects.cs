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
    internal static Material LineMaterial(Color c)
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

    /// <summary>**加色（Additive）**材质：黑 = 透明、亮 = 发光 —— "光"类效果的标准做法。
    ///
    /// ⚠️ 这是加色材质的**唯一实现**（`FireVisual.CreateMaterial` 与闪光弹的"范围光"都用它）。
    ///    原来这段 shader 优先级散落在 `FireVisual` 里，第二处要用时复制一遍就会各自跑偏。
    /// 📌 用 `HasProperty` 探测再写属性（项目铁律：不同 shader 属性集不同，直接写会报错）。
    /// 📌 找不到任何加色 shader 时退回 `Sprites/Default`（仍是 unlit 透明，只是**不发亮**）。</summary>
    internal static Material AdditiveMaterial(Texture2D tex, Color tint, string name = "加色（运行时）")
    {
        Shader sh = Shader.Find("Legacy Shaders/Particles/Additive");
        if (sh == null) sh = Shader.Find("Particles/Additive");
        if (sh == null) sh = Shader.Find("Mobile/Particles/Additive");
        if (sh == null) sh = Shader.Find("Sprites/Default");

        var m = new Material(sh) { name = name };
        if (tex != null && m.HasProperty("_MainTex")) m.mainTexture = tex;
        if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", tint);
        if (m.HasProperty("_Color")) m.color = tint;
        return m;
    }

    /// <summary>清除 MaterialPropertyBlock 的颜色覆盖，让渲染器恢复使用材质本身的颜色</summary>
    public static void ClearColor(Renderer r)
    {
        if (r != null) r.SetPropertyBlock(null);
    }

    // 特效归层已收敛到 `Layers.SetRecursively`（2026-10-15）：本类原来那份 `SetFxLayer` 已删。
    // ⚠️ 特效的碰撞体都会在创建时移除，所以分层**不影响物理**；分层的意义是"语义明确"——
    // 将来若给特效加碰撞体/触发器，子弹（BulletMask）和视线（SightMask）都不会误伤它。

    /// <summary>特效池键前缀：与「资源表留空」时程序化兜底用的 "Impact"/"MuzzleFlash" 池键区分开</summary>
    const string FxPoolPrefix = "FX/";

    // ---------------- 共享图元网格（不要再用 GameObject.CreatePrimitive 造显示物） ----------------
    // ⚠️ `GameObject.CreatePrimitive` 每次调用都会**新建一份网格**（且会带一个碰撞体，项目各处都在删它）。
    // 血条 / 拾取物 / 代码兜底身体 / 特效球这类"每次生成都要造图元"的地方会持续产垃圾，
    // 而且这些网格要等场景卸载才回收。这里统一改成"共享网格只建一次、全部实例共用"。
    // （项目早就为子弹这么做过 —— 子弹的网格现在也统一走 `Effects.SharedMesh`；
    //   ⚠️ 别再引用 `Bullet._cubeMesh`：那个字段已经删掉了，2026-09-29 注释审查修正。）
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

    /// <summary>命中敌人的**血迹**：取 <see cref="GameAssets.bloodPrefab"/>，按 `dir`（子弹方向）摆正朝向，
    /// 播完自动销毁（存活时长 = `PrefabFxLife(prefab) + 0.3`）。
    /// ⚠️ **只在"子弹打中敌人"时调用**（`Combat.ResolveHit` 的敌人分支）—— 打墙 / 爆炸 / 拾取 / 敌人出场都不调。
    /// ⚠️ **朝向直接 `LookRotation(dir)` 就对**（2026-10-16 实测校准）：这个 prefab 的根旋转虽然写着
    ///    `(0,90,270)`、看着像被预先转过，但**四个轴向实测 100% 吻合"喷溅沿局部 +Z"**（Unity 惯例）。
    ///    别再试 `FromToRotation(right, dir)`（偏 90°）或 `FromToRotation(back, dir)`（整体反向）—— 两个都实测过，都不对。
    /// 📌 卡上留空 `bloodPrefab` 时本函数什么都不做 —— 由调用方决定是否回退到 `Impact`。</summary>
    public static void BloodHit(Vector3 pos, Vector3 dir)
    {
        var assets = GameAssets.I;
        if (assets == null || assets.bloodPrefab == null) return;
        // ⚠️ **朝子弹"来的方向"喷**（= 反向）：顺着子弹去的方向喷会像"穿甲弹穿过去了"（用户 2026-10-16 的反馈）。
        //    ⚠️ 这个翻转**只能在代码里做** —— 该 prefab 的主粒子系统挂在**根**上（子物体只有 `BloodDroplets`）：
        //       · 改子物体旋转 → 实测只反向"血滴"，两股血朝相反方向飞（包围盒 2.05 → 3.65）；
        //       · 改 prefab 根旋转 → 会被 `Instantiate(prefab, pos, rot)` 覆盖。
        //    📌 若以后换成**别的**血迹 prefab（喷溅轴约定可能不同）→ 这一行的方向要重新实测校准。
        var rot = dir.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(-dir.normalized)
            : Quaternion.identity;
        var go = Object.Instantiate(assets.bloodPrefab, pos, rot);
        Object.Destroy(go, PrefabFxLife(assets.bloodPrefab) + 0.3f);
    }

    /// <summary>敌人死后留在地面的**血池**：取 <see cref="GameAssets.bloodPoolPrefab"/>，**贴地**生成（y=0.02 + 随机水平朝向），
    /// 存活时长**自动**取 prefab 自己的粒子总时长（+0.5s 余量）。
    /// ⚠️ 只在【敌人死亡】时调用 —— 命中敌人是另一回事（走 <see cref="BloodHit"/>）；打墙不生成。
    /// ⚠️ 为什么要独立于 <see cref="Decal"/>：那是"约 4 秒淡出的贴花"，而生长血池要 **14 秒**才长满
    ///    （`duration 7 + life 7`）→ 用 Decal 会在长到一半时被销毁。
    /// 📌 槽留空 → 回退到 <see cref="Decal"/>（旧行为，零回归）。</summary>
    public static void BloodPool(Vector3 pos)
    {
        var assets = GameAssets.I;
        if (assets == null || assets.bloodPoolPrefab == null)
        {
            Decal(pos, new Color(0.4f, 0.05f, 0.05f, 0.8f));   // 旧行为兜底
            return;
        }
        var groundPos = new Vector3(pos.x, 0.02f, pos.z);
        float yaw = Random.Range(0f, 360f);
        var go = Object.Instantiate(assets.bloodPoolPrefab, groundPos, Quaternion.Euler(0f, yaw, 0f));
        Object.Destroy(go, PrefabFxLife(assets.bloodPoolPrefab) + 0.5f);
    }

    /// <summary>使用血包时的**治疗光环**：取 <see cref="GameAssets.healPrefab"/>，在 `pos` 处播放，
    /// 存活时长**自动**取 prefab 自己的粒子总时长（+0.5s）。
    /// ⚠️ 只在【按 Q 成功用掉一个血包】时调用（`PlayerController`）—— 拾取药箱、其它回血途径都不调。
    /// ⚠️ **必须保留 prefab 自带的旋转**：它是"躺平的光环"（根旋转 `X=270°`）→ 所以这里用 `Instantiate(prefab)`
    ///    再单独设位置；**不要**用 `Instantiate(prefab, pos, Quaternion.identity)`（那会把它立成一堵墙）。
    /// 📌 槽留空 = 什么都不播（调用方无需判断）。</summary>
    public static void HealFx(Vector3 pos)
    {
        var assets = GameAssets.I;
        if (assets == null || assets.healPrefab == null) return;
        var go = Object.Instantiate(assets.healPrefab);
        go.transform.position = pos;
        Object.Destroy(go, PrefabFxLife(assets.healPrefab) + 0.5f);
    }

    /// <summary>爆炸特效：取 <see cref="GameAssets.explosionPrefab"/>，在 `pos` 播放，并按**各自的爆炸半径等比缩放**。
    /// ⭐ **为什么这样能对齐**：缩放由"**人工标定的基准半径**"（`GameAssets.explosionFxBenchRadius`）换算而来 ——
    ///    标定值来自"**人眼看着对上**"，而不是去**测量特效的视觉边界**（那个测不准；2026-10-17 踩过：
    ///    用包围盒量出的 12.96 米当基准 → 环径 12 米 vs 伤害圈 8 米）。
    ///    ⇒ **只需标定一次**，其它半径按纯比例自动算（半径 4 米 = 8 米的一半大），加新半径也不用改代码；
    ///      标定值即使偏了，也只影响"所有爆炸整体大小"，**各半径之间仍然对齐**。
    /// ⚠️ 用**乘法**缩放：prefab 自带的那身缩放要保留（它就是标定时用的缩放），不要覆盖。
    /// ⚠️ 会**移除 prefab 自带的 AudioSource** —— 音效统一由调用方播 `Sfx.Explosion()`（音效归代码，
    ///    否则会出现"两个爆炸声叠着"）。
    /// ⚠️ 保留 prefab 自带旋转（它是躺平的 `X=270°`）→ 用 `Instantiate(prefab)` 再设位置，别传 `Quaternion.identity`。
    /// 📌 返回 **false** = 槽没配 → 调用方回退到自己那套代码生成的火球（零回归）。
    /// 📌 `RPG / 手雷 / 自爆兵` 三处**共用这一个函数** —— 想改爆炸长什么样，只改这里（或换槽上的 prefab）。</summary>
    public static bool ExplosionFx(Vector3 pos, float radius)
    {
        var assets = GameAssets.I;
        if (assets == null || assets.explosionPrefab == null) return false;
        var go = Object.Instantiate(assets.explosionPrefab);
        go.transform.position = pos;
        // ⭐ 按半径等比缩放（基准 = 人工标定值）
        if (assets.explosionFxBenchRadius > 0.01f)
            go.transform.localScale *= Mathf.Max(0.02f, radius / assets.explosionFxBenchRadius);
        // ⚠️ 自带音效必须**先静音再销毁**：`Destroy` 要到帧末才生效，如果只 Destroy，
        //    它的 `playOnAwake` 会先响一声 → 和 `Sfx.Explosion()` 叠成两个爆炸声（2026-10-16 实测发现）。
        //    （prefab 那边也顺手关掉了 `playOnAwake`：双保险。音效统一归代码。）
        var src = go.GetComponentInChildren<AudioSource>(true);
        if (src != null) { src.playOnAwake = false; src.Stop(); src.enabled = false; Object.Destroy(src); }
        Object.Destroy(go, PrefabFxLife(assets.explosionPrefab) + 0.5f);
        return true;
    }

    /// <summary>读一个特效 prefab「播完要多久」= 它所有粒子系统里 `(Duration + 最长粒子寿命)` 的最大值。
    ///
    /// ⚠️ **循环**（`loop = true`）的粒子系统，`Duration` 只是"单个循环的长度"、不代表播完 → 这类只取寿命、不加 Duration。
    /// ⚠️ **为什么要它**：需要"播完就销毁"的地方（斩杀雷柱 `MagicMark`、魔法球的命中特效 `MagicProjectile`、
    ///    命中血迹 / 死亡血池 / 治疗光环 / 爆炸）用它**自动算出销毁时长** → 换特效时**不用改代码、也不用填数值**
    ///    （卡上人工填的值只当"下限"用）。
    /// 📌 调用方一般再加 0.3~0.5s 余量（淡出 / 收尾），例如 `Effects.PrefabFxLife(prefab) + 0.3f`。
    /// 📌 与"卡上人工填的时长"的关系：那里取 `max(人工值, 本函数返回值)` —— 人工值只会**加长**，永远不会把特效掐断。</summary>
    public static float PrefabFxLife(GameObject prefab)
    {
        if (prefab == null) return 0f;
        var pss = prefab.GetComponentsInChildren<ParticleSystem>(true);
        float max = 0f;
        for (int i = 0; i < pss.Length; i++)
        {
            var m = pss[i].main;
            float t = m.startLifetime.constantMax;
            if (!m.loop) t += m.duration + m.startDelay.constantMax;
            if (t > max) max = t;
        }
        return max;
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
            Layers.SetRecursively(once, Layers.FX);
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
            Layers.SetRecursively(go, Layers.FX);
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
            Layers.SetRecursively(go, Layers.FX);
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
            Layers.SetRecursively(go, Layers.FX);
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
        Layers.SetRecursively(lightGo, Layers.FX);
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
        Layers.SetRecursively(go, Layers.FX);
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
