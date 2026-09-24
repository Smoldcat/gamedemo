using UnityEngine;

/// <summary>
/// 子弹：**只负责「运动 + 打一条射线」**。
/// 命中之后发生什么（打墙出火花 / 打中敌人扣血 / 爆头 / 爆炸 AOE / 命中反馈）全部交给
/// `Combat.ResolveHit` —— 加新弹种（穿透 / 弹片 / 燃烧）不需要改本类，见 `Combat` 的注释。
///
/// 代码生成的弹体走对象池（高频复用，避免每次 Instantiate/CreatePrimitive）；
/// 配置了 `bulletPrefab` 的子弹保持 Instantiate/Destroy（带自定义外观，数量少）。
/// </summary>
public class Bullet : MonoBehaviour
{
    [Header("运动")]
    public Vector3 dir;              // 归一化方向
    public float speed = 30f;
    public float maxLife = 1.5f;

    /// <summary>本颗子弹携带的伤害参数（由 `Spawn` 按 WeaponData 填好，命中时交给 `Combat` 结算）</summary>
    public ShotProfile shot;

    bool _pooled;   // 是否来自对象池（prefab 子弹不走池）

    /// <summary>出膛位置（「有效射程 → 伤害衰减」要用）：命中那一帧量一次「出膛点 → 命中点」的真实距离。
    /// ⚠️ 每次 `Spawn` 都会重设（对象池复用同一个 Bullet 组件），所以不会把上一发的起点带过来；
    ///    也刻意**不做每帧累计** —— 只在命中时算一次距离，常态零开销。</summary>
    Vector3 _spawnPos;

    /// <summary>
    /// 生成一颗子弹。headshotTarget / headshotMultiplier 由玩家开火时传入（敌人子弹留空）。
    /// </summary>
    public static Bullet Spawn(Vector3 pos, Vector3 dir, WeaponData w, Health.Team team,
                               EnemyAI headshotTarget = null, float headshotMultiplier = 1f)
    {
        GameObject go;
        if (w.bulletPrefab != null)
        {
            // 用在 WeaponData 里配置的子弹 Prefab（自带外观，颜色不再跟随 tracerColor）
            go = Object.Instantiate(w.bulletPrefab, pos, Quaternion.LookRotation(dir));
            go.name = "Bullet";
            // 子弹靠射线检测，残留碰撞体会产生物理推挤
            foreach (var col in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(col);
        }
        else
        {
            // 代码生成弹体：优先从对象池复用
            go = ObjectPool.Get("Bullet");
            if (go == null) go = CreateCubeBullet();

            go.transform.position = pos;
            go.transform.localScale = new Vector3(0.06f, 0.06f, 0.38f * Mathf.Clamp(w.bulletSpeed / 30f, 0.8f, 1.8f)); // 弹速越快曳光越长（狙击最醒目）
            go.transform.rotation = Quaternion.LookRotation(dir);
            go.GetComponent<MeshRenderer>().sharedMaterial = Effects.Shared(w.tracerColor);
        }

        var b = go.GetComponent<Bullet>();
        if (b == null) b = go.AddComponent<Bullet>();
        b._pooled = w.bulletPrefab == null;
        b.dir = dir.normalized;
        b.speed = w.bulletSpeed;
        b.maxLife = w.bulletLife > 0f ? w.bulletLife : 1.5f;   // 射程按武器配置；没配则回落旧硬编码 1.5
        b._spawnPos = pos;                                     // 「有效射程」的起点（命中时量距离用）
        b.shot = new ShotProfile
        {
            mode = w.explosionRadius > 0f ? HitMode.Explode : HitMode.Direct,
            damage = w.damage,
            // 有效射程衰减：**默认 0 / 1 = 完全不衰减**，与旧行为逐位一致（8 把武器资源没配这项）
            effectiveRange = w.effectiveRange,
            beyondRangeDamage = w.beyondRangeDamage,
            explosionRadius = w.explosionRadius,
            explosionDamage = w.explosionDamage,
            team = team,
            color = w.tracerColor,
            headshotTarget = headshotTarget,
            headshotMultiplier = headshotMultiplier,
        };
        return b;
    }

    /// <summary>创建代码生成弹体的模板：共享网格（子弹靠射线检测，不需要碰撞体）
    /// ⚠️ 共享网格统一从 `Effects.SharedMesh` 取 —— 原来这里自己又造了一份 Cube 网格缓存
    ///    （与 `Effects` 那份重复，2026-09-22 收敛）。</summary>
    static GameObject CreateCubeBullet()
    {
        var go = new GameObject("Bullet");
        go.AddComponent<MeshFilter>().sharedMesh = Effects.SharedMesh(PrimitiveType.Cube);
        go.AddComponent<MeshRenderer>();
        return go;
    }

    /// <summary>子弹结束：池化子弹回池，prefab 子弹销毁</summary>
    void Release()
    {
        shot.headshotTarget = null;   // 别让池里的子弹长期握着一个（可能已销毁的）敌人引用
        if (_pooled) ObjectPool.Release("Bullet", gameObject);
        else Destroy(gameObject);
    }

    void Update()
    {
        // 暂停（背包打开）时子弹完全冻结：不推进、不做射线、也不扣寿命。
        // timeScale=0 时 deltaTime 本来就是 0，这里是显式兜底。
        if (GameState.Paused) return;

        Vector3 step = dir * speed * Time.deltaTime;
        Vector3 target = transform.position + step;

        float dist = step.magnitude;
        if (dist > 0f)
        {
            Ray ray = new Ray(transform.position, dir);
            // 只打「阻挡物（墙/箱子/地面）+ 战斗单位（玩家/敌人）」；显式忽略触发器
            // （Physics.queriesHitTriggers 默认 true → 将来加触发器会被子弹当墙吸收，见 Layers 注释）
            if (Physics.Raycast(ray, out RaycastHit hit, dist, Layers.BulletMask, QueryTriggerInteraction.Ignore))
            {
                // 「有效射程 → 伤害衰减」：量一次「出膛点 → 命中点」的真实飞行距离（仅命中这一帧）
                float traveled = Vector3.Distance(_spawnPos, hit.point);
                if (Combat.ResolveHit(hit, shot, dir, traveled) == HitResult.Consumed)
                {
                    Release();
                    return;
                }
                // PassThrough（友军 / 尸体）：穿过继续飞
            }
        }

        transform.position = target;

        maxLife -= Time.deltaTime;
        if (maxLife <= 0f)
        {
            // 爆炸弹飞到头（没命中）也在前方爆开
            if (shot.mode == HitMode.Explode && shot.explosionRadius > 0f) Combat.Explode(target, shot);
            Release();
        }
    }
}
