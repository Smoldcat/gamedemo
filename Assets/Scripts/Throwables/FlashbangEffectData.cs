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

    [Header("【表现】（可空）")]
    [Tooltip("爆点白色闪光 prefab。留空 = 代码生成的白色光球（点一炮，0.2 秒消散）")]
    public GameObject flashPrefab;
    [Tooltip("闪光颜色（也用于代码生成的光球）")]
    public Color flashColor = new Color(1f, 0.98f, 0.90f);

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
        SpawnFlash(point);
        if (flashSound != null) Sfx.Play(flashSound);
    }

    /// <summary>爆点白色闪光：优先用卡上的 prefab；留空则生成一个膨胀消散的白色光球。</summary>
    void SpawnFlash(Vector3 point)
    {
        if (flashPrefab != null)
        {
            var go = Instantiate(flashPrefab);      // ⚠️ 保留 prefab 自带旋转（项目统一约定）
            go.transform.position = point;
            var life = Effects.PrefabFxLife(flashPrefab) + 0.3f;
            Object.Destroy(go, life);
            return;
        }

        var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "闪光(占位)";
        var col = ball.GetComponent<Collider>();
        // ⚠️ `Destroy` 要到**帧末**才生效：这一帧它还是个**实体碰撞球**，会短暂挡住子弹 / 角色。
        //    先 `enabled = false`（立即生效）再排队销毁（2026-10-18 全面检查）。
        if (col != null) { col.enabled = false; Destroy(col); }
        ball.transform.position = point;
        ball.transform.localScale = Vector3.one * Mathf.Max(0.4f, radius * 0.35f);

        var mat = new Material(Shader.Find("Sprites/Default"));
        mat.color = flashColor;
        var r = ball.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var anim = ball.AddComponent<FlashBall>();
        anim.Init(0.25f, radius * 1.1f);
    }

    /// <summary>编辑器兜底。⚠️ `ThrowableEffectData` **没有** `OnValidate` 可重写 ⇒ 这是普通方法
    /// （Unity 按名字调用），别加 `override`。</summary>
    void OnValidate()
    {
        if (radius < 0.5f) radius = 0.5f;
        if (blindDuration < 0f) blindDuration = 0f;
        if (slowDuration < 0f) slowDuration = 0f;
        if (playerBlindDuration < 0f) playerBlindDuration = 0f;
    }
}

/// <summary>占位白光的动画：在 `life` 秒内膨胀到 `endSize` 并淡出，然后自毁。</summary>
public class FlashBall : MonoBehaviour
{
    float _t0, _life, _endSize;
    Material _mat;
    Color _c0;

    public void Init(float life, float endSize)
    {
        _life = Mathf.Max(life, 0.05f);
        _endSize = Mathf.Max(endSize, 0.1f);
        _t0 = Time.time;
        _mat = GetComponent<MeshRenderer>().sharedMaterial;
        _c0 = _mat != null ? _mat.color : Color.white;
    }

    void Update()
    {
        float t = (Time.time - _t0) / _life;
        if (t >= 1f) { Destroy(gameObject); return; }

        transform.localScale = Vector3.one * Mathf.Lerp(_endSize * 0.35f, _endSize, t);
        if (_mat != null)
        {
            var c = _c0;
            c.a = 1f - t;
            _mat.color = c;
        }
    }

    /// <summary>销毁自建的材质实例。
    /// ⚠️ 这个材质是 `SpawnFlash` 里 `new Material(...)` 出来的**独占实例**，而 Unity 销毁 GameObject 时
    ///    **不会**连带销毁材质 ⇒ 不手动销毁就**每投一次漏一份**（2026-10-18 全面检查发现）。</summary>
    void OnDestroy()
    {
        if (_mat != null) Destroy(_mat);
    }
}
