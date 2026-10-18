using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 「**闪光弹**」的效果卡（投掷物效果，和爆炸 / 燃烧 / 烟雾并列）。
///
/// 效果（用户 2026-10-18 定）：
///   · **范围内敌人**：① **致盲**（与烟雾同一个出口 —— `EnemyAI.CanSeePlayer` 返回 false）
///                     ② **减速**（`EnemyAI.MoveTo` 里乘一个倍率）
///     致盲期间敌人**不再听指挥**：不追人、不攻击，只以半速**缓慢乱走**（像真瞎了）。
///   · **范围内的玩家**：**屏幕闪白** + **减速**（参数与敌人**完全分开**，可单独调）。
///   · 爆点一个**白色闪光**（留空则代码生成占位，以后换正式特效只改引用）。
///
/// ⚠️ **隔墙不闪**（与爆炸「隔墙不伤」、火圈「隔墙不烧」同一判据 `Combat.HasClearPath`）。
/// ⚠️ **一圈统一效果**（用户明确取消"内外圈衰减"）：只要在半径内，致盲/减速都是满值。
/// </summary>
[CreateAssetMenu(fileName = "Effect_闪光弹", menuName = "游戏/投掷物/效果/闪光弹", order = 33)]
public class FlashbangEffectData : ThrowableEffectData
{
    [Header("【范围】")]
    [Tooltip("生效半径（米）。圈内统一满效果（不做内外圈衰减）")]
    [Min(0.5f)] public float radius = 4f;

    [Header("【对敌人】")]
    [Tooltip("致盲时长（秒）：这期间敌人**看不见玩家**，并且**不追人、不攻击**、只缓慢乱走")]
    [Min(0f)] public float blindDuration = 4f;
    [Tooltip("减速百分比：0.5 = 速度只剩 50%。圈内统一，不随距离衰减")]
    [Range(0f, 1f)] public float slowPercent = 0.5f;
    [Tooltip("减速持续（秒）。**0 = 与致盲时长相同**（填了就以它为准）")]
    [Min(0f)] public float slowDuration = 0f;

    [Header("【对玩家】（与敌人的参数完全分开）")]
    [Tooltip("玩家屏幕闪白时长（秒）。⚠️ 太长会完全看不见 ≈ 送死，建议 1~2 秒")]
    [Min(0f)] public float playerBlindDuration = 1.5f;
    [Tooltip("玩家减速百分比：0.5 = 速度只剩 50%（时长为上面的闪白时长）")]
    [Range(0f, 1f)] public float playerSlowPercent = 0.5f;

    // ==================== 表现：两个**独立**部件（用户 2026-10-18 定：\"爆炸特效 + 范围光\"）====================

    [Header("① 爆开特效（你已有的资源放这里）")]
    [Tooltip("**爆点爆开**的特效 prefab。留空 = 什么都不放（只剩下面的范围光）")]
    public GameObject flashPrefab;

    [Header("② 范围光（\"整片区域充满光\"）")]
    [Tooltip("**范围光**特效 prefab（想让美术接管时拖进来）。留空 = 用程序占位。\n" +
             "⚠️ 拖进来后会被**自动缩放**到 `radius × 2` 的直径，所以美术资源按\"直径 1\"做即可")]
    public GameObject flashAreaPrefab;
    [Tooltip("程序占位用的**柔边噪声图**（留空 = 没有光云，只闪一下；默认已配 `FlashArea_Soft`）。\n" +
             "⚠️ 只有 `flashAreaPrefab` 留空时才会用它")]
    public Texture2D flashAreaTexture;
    [Tooltip("范围光的颜色/亮度（**加色混合**：越亮越白）")]
    public Color flashColor = new Color(1f, 0.98f, 0.92f);
    [Tooltip("范围光**持续**（秒）。用户要的是\"一瞬间\" ⇒ 默认 0.15（前 35% 撑开，之后淡出）")]
    [Min(0.03f)] public float flashLife = 0.15f;
    [Tooltip("范围光的**峰值强度**（0~1）。它是 3 层贴地光片的**总强度**（层间权重和为 1，叠层不会额外过曝）。\n" +
             "0.30 ≈ 淡淡一圈；0.50 ≈ 明显泛白；0.7+ 中心会过曝成纯白")]
    [Range(0.02f, 1f)] public float flashAlpha = 0.35f;

    [Tooltip("**点光源峰值强度**（0 = 不加点光源）。\n" +
             "它和贴地光片职责不同：光片负责「**范围可读**」（有明确边界），点光源负责「**真的照亮**」\n" +
             "（地面/箱子/敌人被照白，有明暗和体积感）。两者叠加才是完整效果。\n" +
             "⚠️ 阴影**强制关闭**（点光阴影是 6 面 cubemap，非常贵）\n" +
             "📌 建议 4~10；本体偏亮的场景取小值")]
    [Range(0f, 20f)] public float flashLightIntensity = 6f;

    [Header("【音效】（可空）")]
    public AudioClip flashSound;

    public override float DangerRadius => radius;

    public override void Apply(Vector3 point)
    {
        if (GameState.GameOver) return;

        float rSq = radius * radius;
        float slowSec = slowDuration > 0.01f ? slowDuration : blindDuration;

        // ---- ① 圈内敌人：致盲 + 减速（隔墙不闪）----
        // 先快照：致盲/减速本身不会销毁敌人，但 `TakeDamage` 之后的连锁（死亡销毁）可能在遍历中改列表
        var snap = new List<EnemyAI>(EnemyAI.All.Count);
        for (int i = 0; i < EnemyAI.All.Count; i++)
            if (EnemyAI.All[i] != null) snap.Add(EnemyAI.All[i]);

        for (int i = 0; i < snap.Count; i++)
        {
            var e = snap[i];
            if (e == null) continue;

            Vector3 ep = e.transform.position;
            float dx = ep.x - point.x, dz = ep.z - point.z;
            if (dx * dx + dz * dz > rSq) continue;
            if (!Combat.HasClearPath(point, ep, e.transform)) continue;   // 隔墙不闪

            e.ApplyFlash(blindDuration, slowPercent, slowSec);
        }

        // ---- ② 玩家：闪白 + 减速（参数独立）----
        var pc = PlayerController.Instance;
        if (pc != null)
        {
            Vector3 pp = pc.transform.position;
            float dx = pp.x - point.x, dz = pp.z - point.z;
            bool inRange = dx * dx + dz * dz <= rSq;
            if (inRange && Combat.HasClearPath(point, pp, pc.transform))
            {
                if (playerBlindDuration > 0.01f) ScreenFlash.Play(new Color(1f, 1f, 1f, 1f), playerBlindDuration);
                if (playerSlowPercent > 0.001f) pc.ApplySlow(playerSlowPercent, playerBlindDuration);
            }
        }

        // ---- ③ 爆点白光 + 音效 ----
        // ---- ③ 爆点表现：**两个独立部件**（爆开特效 + 范围光）+ 音效 ----
        SpawnFlash(point);
        if (flashSound != null) Sfx.Play(flashSound);
    }

    /// <summary>爆点表现（**两个独立部件**）：
    ///   ① 爆开特效 `flashPrefab`（你已有的资源）—— 配了才放；
    ///   ② 范围光 `flashAreaPrefab`（美术接管）/ 程序占位（柔边噪声图 + 面向相机 + 加色）。
    /// ⚠️ 两者**互不取代**：都配了就都放（这就是用户要的"两个构成"）。</summary>
    void SpawnFlash(Vector3 point)
    {
        // ① 爆开特效
        if (flashPrefab != null)
        {
            var burst = Instantiate(flashPrefab);   // ⚠️ 保留 prefab 自带旋转（项目统一约定）
            burst.transform.position = point;
            Object.Destroy(burst, Effects.PrefabFxLife(flashPrefab) + 0.3f);
        }

        // ② 范围光：**直径 = 生效范围**（`radius × 2`）⇒ 覆盖的圆正好等于真正生效的圆
        float size = Mathf.Max(0.5f, radius * 2f);

        if (flashAreaPrefab != null)
        {
            var fx = Instantiate(flashAreaPrefab);
            fx.transform.position = point;
            fx.transform.localScale = Vector3.one * size;    // 美术按"直径 1"做即可
            Object.Destroy(fx, Effects.PrefabFxLife(flashAreaPrefab) + 0.3f);
            return;
        }

        // ⚠️ 没贴图就**不放**：加色混合会把一块纯白四边形加成一个大白方块 ✗（宁可不放）
        if (flashAreaTexture == null) return;

        // ⭐ 2026-10-18（用户：世界坐标系下的「圆形薄片」，再给**一点点厚度**且要渐变）：
        //    3 层**平躺地面**的面片叠起来，越往上越小、越淡 ⇒ 像一小块有厚度的光；
        //    层间亮度按权重 **0.68/0.24/0.08** 分配（和为 1）⇒ **中心总亮度与单层一致**，不会因为多叠两层而过曝。
        //    📌 具体叠法在 `FlashAreaFx.Init` 里（它自己建子层），这里只负责起一个根节点。
        var root = new GameObject("范围光(占位)");
        root.transform.position = point;
        var anim = root.AddComponent<FlashAreaFx>();
        // 📌 直接把卡传进去（光片强度 / 点光强度 / 颜色 / 寿命都在卡上，少传 5 个参数）
        anim.Init(this, size);
    }

    /// <summary>编辑器兜底。⚠️ `ThrowableEffectData` **没有** `OnValidate` 可重写 ⇒ 这是普通方法
    /// （Unity 按名字调用），别加 `override`。</summary>
    void OnValidate()
    {
        if (radius < 0.5f) radius = 0.5f;
        if (blindDuration < 0f) blindDuration = 0f;
        if (slowDuration < 0f) slowDuration = 0f;
        if (playerBlindDuration < 0f) playerBlindDuration = 0f;
        if (flashLife < 0.05f) flashLife = 0.05f;
        if (flashAlpha > 0f && flashAlpha < 0.02f) flashAlpha = 0.02f;
    }
}

/// <summary>「**范围光**」占位的动画：**快速撑开到生效范围 → 加色淡出**，然后自毁。
///
/// ⚠️ 它只是**范围光**（第二部分）；爆开特效由卡上 `flashPrefab` 单独负责，两者**互不取代**。
/// 📌 形态：**世界坐标系下的圆形薄片**（平躺地面，**不面向相机**）—— 随视角有正确透视，读起来是"地上的一圈光"。
/// 📌 **"一点点厚度"**：不是一张纸，而是 **3 层**叠起来（越高越小、越淡）⇒ 边缘是"逐层收窄 + 逐层变淡"，
///    不会出现一圈硬边；层间亮度按权重 **0.68 / 0.24 / 0.08** 分配（**和为 1**）⇒ 中心总亮度与单层一致，
///    所以"加厚度"**不会顺带把中心变亮/变白**。
/// 📌 材质用**加色混合**（`Effects.AdditiveMaterial`）：颜色越亮越白。
/// 📌 `endSize` 是**直径** ⇒ `SpawnFlash` 传 `radius × 2`，光片的半径才正好等于生效半径。</summary>
public class FlashAreaFx : MonoBehaviour
{
    /// <summary>撑开占寿命的比例（剩下 65% 用来淡出）。</summary>
    const float ExpandPart = 0.35f;

    /// <summary>层数（"一点点厚度" ⇒ 3 层够了；再多纯浪费 draw call）。</summary>
    const int Layers = 3;

    // 每层：抬高（米）/ 相对直径 / 亮度权重（权重**和为 1** ⇒ 中心总亮度不因叠层而过曝）
    // 📌 纯常量查找表，不持有场景对象，不参与 `StaticReset`（项目约定里"纯资源/模板"那类）。
    static readonly float[] LayerY = { 0.02f, 0.18f, 0.36f };
    static readonly float[] LayerScale = { 1.00f, 0.86f, 0.66f };
    static readonly float[] LayerWeight = { 0.68f, 0.24f, 0.08f };

    float _t0, _life, _endSize, _startSize, _alpha0;
    Color _rgb;
    readonly Transform[] _tr = new Transform[Layers];
    readonly Material[] _mat = new Material[Layers];

    Light _light;          // 点光源（负责"真的照亮"）
    float _lightPeak;

    /// <summary><paramref name="endSize"/> = 最终**直径**。</summary>
    public void Init(FlashbangEffectData cfg, float endSize)
    {
        _life = Mathf.Max(cfg.flashLife, 0.03f);
        _endSize = Mathf.Max(endSize, 0.1f);
        _startSize = _endSize * 0.6f;      // 从 60% 起撑：更像"炸开"，不是"长出来"
        _rgb = cfg.flashColor;
        _alpha0 = Mathf.Clamp01(cfg.flashAlpha);
        _t0 = Time.time;

        // ---- ① 贴地光片（3 层）：负责「**范围可读**」—— 有明确边界，玩家能判断站这里安不安全 ----
        for (int i = 0; i < Layers; i++)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "范围光层" + i;
            var col = quad.GetComponent<Collider>();
            // ⚠️ `Destroy` 帧末才生效：这一帧还是实体碰撞体 ⇒ 先禁用再排队销毁
            if (col != null) { col.enabled = false; Destroy(col); }

            var tr = quad.transform;
            tr.SetParent(transform, false);
            tr.localPosition = new Vector3(0f, LayerY[i], 0f);
            tr.localRotation = Quaternion.Euler(90f, 0f, 0f);   // 平躺地面（世界坐标，不做 billboard）
            tr.localScale = Vector3.one * (_startSize * LayerScale[i]);

            // ⚠️ 初始 alpha 必须**当场**就是 `峰值 × 权重`：否则材质会带着不透明的默认色建出来，
            //    第一帧（Update 还没跑）会闪一下纯白（2026-10-18 实测发现）。
            var tint = new Color(_rgb.r, _rgb.g, _rgb.b, _alpha0 * LayerWeight[i]);
            _mat[i] = Effects.AdditiveMaterial(cfg.flashAreaTexture, tint, "范围光层" + i);
            var r = quad.GetComponent<MeshRenderer>();
            r.sharedMaterial = _mat[i];
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _tr[i] = tr;
        }

        // ---- ② 点光源：负责「**真的照亮**」（地面 / 箱子 / 敌人被照白，有明暗与体积感）----
        //    ⚠️ 贴片式的加色光**不会照亮任何东西**，只是盖了一层白光；点光才让场景"被照着"。
        if (cfg.flashLightIntensity > 0.01f)
        {
            _lightPeak = cfg.flashLightIntensity;

            var lgo = new GameObject("范围光-点光源");
            lgo.transform.SetParent(transform, false);
            // ⚠️ 抬高到 1.0m：贴地 0.6m 时地面会出现一个"热点"，比光片还抢眼、把范围边界盖掉 ✗
            lgo.transform.localPosition = new Vector3(0f, 1.0f, 0f);

            _light = lgo.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = _rgb;
            // range = 直径 × 1.05 ⇒ 照亮范围**略大于**光片，衰减拉平（不会形成一个小亮点）
            _light.range = Mathf.Max(2f, _endSize * 1.05f);
            _light.intensity = _lightPeak;
            _light.shadows = LightShadows.None;               // ⚠️ 点光阴影 = 6 面 cubemap，非常贵 ⇒ 强制关
            _light.bounceIntensity = 0f;                      // 闪一下不需要参与 GI
            _light.renderMode = LightRenderMode.Auto;         // 由引擎按屏幕尺寸决定 pixel/vertex
        }
    }

    void Update()
    {
        float t = (Time.time - _t0) / _life;
        if (t >= 1f) { Destroy(gameObject); return; }

        // ① 撑开：前 ExpandPart 完成（smoothstep 收尾，不会"啪到最大"再抖）
        float e = Mathf.Clamp01(t / ExpandPart);
        e = e * e * (3f - 2f * e);
        float size = Mathf.Lerp(_startSize, _endSize, e);

        // ② 强度衰减系数：撑开期保持峰值，之后线性衰减到 0（光片与点光**共用同一条曲线**，节奏才一致）
        float k = 1f - Mathf.Clamp01((t - ExpandPart) / (1f - ExpandPart));
        float fade = _alpha0 * k;

        if (_light != null) _light.intensity = _lightPeak * k;

        for (int i = 0; i < Layers; i++)
        {
            var tr = _tr[i];
            if (tr != null) tr.localScale = Vector3.one * (size * LayerScale[i]);

            var m = _mat[i];
            if (m == null) continue;
            var c = new Color(_rgb.r, _rgb.g, _rgb.b, fade * LayerWeight[i]);
            // 加色 shader 的主色属性名不统一（Legacy 用 _TintColor、Sprites 用 _Color）⇒ 两个都探
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c);
            if (m.HasProperty("_Color")) m.color = c;
        }
    }

    /// <summary>销毁自建的材质实例。
    /// ⚠️ Unity 销毁 GameObject 时**不会**连带销毁材质 ⇒ 不手动销毁就**每投一次漏 3 份**。</summary>
    void OnDestroy()
    {
        for (int i = 0; i < Layers; i++)
            if (_mat[i] != null) Destroy(_mat[i]);
    }
}
