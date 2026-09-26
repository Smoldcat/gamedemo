using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 敌人池里的一项：一个敌人配置 + 它的出场权重和解锁波次。
/// 权重/解锁波次在这里配置（WaveManager 面板），不再写在敌人配置资源里。
/// </summary>
[System.Serializable]
public class EnemyPoolEntry
{
    [Tooltip("敌人配置（从 Assets/Enemies 拖进来）")]
    public EnemyConfig config;
    [Tooltip("出现权重：越大越常刷。所有项按权重随机")]
    public float weight = 1f;
    [Tooltip("最早出现的波次（当前波次小于它的不会刷）")]
    public int minWave = 1;
}

/// <summary>
/// 波次管理器：整个游戏的调度中心。
/// 负责开局的动态内容生成、波次状态机（战斗 → 清场 → 刷怪）、僵局兜底、血包重生。
/// 场景里的地面/墙/玩家/相机/UI 都是手动摆好的静态物体，这里只负责运行时生成的部分。
/// </summary>
public class WaveManager : MonoBehaviour
{
    /// <summary>场景中手动放置的玩家（用来找出生点/传送锚点）</summary>
    public Transform playerTransform;

    [Header("武器池（每局全部铺在地上；推荐用这个）")]
    [Tooltip("把所有想出现的武器 .asset 拖进来：每局开局把池里的武器**全部**铺到地图上（落点随机）；留空则用代码内置 4 把默认枪")]
    public WeaponData[] weaponPool;

    [Header("初始弹药")]
    [Tooltip("开局每种弹药各铺几包（0 = 不铺）。\n弹药种类**自动从物品清单里找**（不用手拖）—— 加了新弹药后跑一次菜单 Tools/游戏/重建物品清单即可")]
    [Min(0)] public int initialAmmoPacksPerType = 2;

    [Header("敌人池（每波随机出怪；推荐用这个）")]
    [Tooltip("把敌人配置拖进每一项，权重/解锁波次直接在面板上调。有几项就会随机出几种敌人")]
    public EnemyPoolEntry[] enemyPool;

    [Header("敌人兜底槽位（敌人池为空时才用）")]
    [Tooltip("敌人池为空且配置了这里时，近战敌人的数值/外形配置")]
    public EnemyConfig meleeConfig;
    [Tooltip("敌人池为空且配置了这里时，远程敌人的数值/外形配置")]
    public EnemyConfig shooterConfig;

    [Header("波次配置（在 Asset 里拖入；留空则用代码内置默认值）")]
    [Tooltip("波次人数/刷怪间隔/清场倒计时等。右键 Create > 游戏 > 波次配置 创建")]
    public WaveConfig waveConfig;

    [Header("地图（可选）")]
    [Tooltip("地面物体（带 Collider，一般拖场景里的 Ground）。拖入后每局按它尺寸算出「地图边界」，\n" +
             "**刷怪范围 / 巡逻点 / 僵局传送落点**都按这个边界换算（2026-09-22 起刷怪范围也跟随它）。\n" +
             "⚠️ 留空 → 用默认边界 15（即 30×30），此时刷怪范围会比接上时小一圈")]
    public Transform mapGround;
    [Tooltip("地面物体的 Tag。每局按它重新查找地面（场景重载后旧引用会失效，必须重绑）。留空则不重新绑定")]
    public string mapGroundTag = "";

    [Header("开局资源")]
    [Tooltip("开局给玩家的金币（原型调试用）。\n" +
             "  · 设 0 = 从 0 开始，想买东西就先把商店配方里的「卖出价」调高（把身上的东西卖成钱）；\n" +
             "  · 设 >0 = 一进游戏就带这么多钱。\n" +
             "每局开始时写入 `GameState.Gold`（在 GameState.Reset 之后，不会被清零）")]
    [Min(0)] public int startGold = 0;

    // ---- 波次状态机 ----
    // 不用协程实现：协程内一旦抛异常会静默死亡，波次就永久卡住了。用状态机更可控。
    enum WavePhase { Fighting, Clearing, Spawning }
    WavePhase _phase = WavePhase.Fighting;
    float _phaseTimer;
    int _pendingSpawns;
    float _nextSpawnAt;
    float _idleTimer;    // 敌人长时间不交战计时（找敌僵局兜底）

    /// <summary>血包被拾取后待重生的时间点队列（拾取后 20 秒在地图随机位置再生成一个）</summary>
    readonly List<float> _medkitRespawnAt = new List<float>();

    /// <summary>唯一实例：DontDestroyOnLoad 常驻；死亡重开 LoadScene 会新建副本，直接销毁防止多个管理器重复刷怪</summary>
    static WaveManager _instance;

    /// <summary>当前这一局是否已完成初始化（Start 与 sceneLoaded 首帧都触发，必须防止重复刷 6 个敌人）</summary>
    bool _roundBuilt;

    /// <summary>内置波次配置的缓存（waveConfig 槽位留空时用它）。
    /// ⚠️ 绝不能在属性里直接写 `waveConfig != null ? waveConfig : WaveConfig.Default()`：
    /// WaveConfig.Default() 每次调用都会 CreateInstance 一个新的 ScriptableObject，
    /// 而 WC.idleTimeout 在「有敌人且无人交战」时是每帧求值的 → 会持续泄漏。
    /// 判空必须用 `!= null`（Unity 的 == 重载能识别已销毁对象），被销毁后会自动重建。</summary>
    static WaveConfig _defaultWC;

    /// <summary>当前生效的波次配置（没拖资源时用内置默认值，只会创建一次）</summary>
    WaveConfig WC => waveConfig != null
        ? waveConfig
        : (_defaultWC != null ? _defaultWC : (_defaultWC = WaveConfig.Default()));

    /// <summary>单例引用（DontDestroyOnLoad 常驻）。HUD 用它读清场倒计时。</summary>
    public static WaveManager Instance => _instance;

    /// <summary>静态状态清理（供 StaticReset 调用）：关闭 Reload Domain 时防止常驻引用残留到下一局</summary>
    internal static void ResetStatics()
    {
        _instance = null;
        _defaultWC = null;
    }

    /// <summary>是否处于「清完场 → 下一波」的倒计时阶段</summary>
    public bool IsClearing => _phase == WavePhase.Clearing;

    /// <summary>距离下一波的秒数（仅清场阶段有效，其余阶段返回 0）</summary>
    public float ClearingCountdown => _phase == WavePhase.Clearing ? Mathf.Max(_phaseTimer, 0f) : 0f;

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            enabled = false;       // 阻止副本的 Start/Update 在本帧执行
            Destroy(gameObject);   // 帧末销毁副本
            return;
        }
        _instance = this;

        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
        HealthPack.AnyConsumed += OnMedkitConsumed;
    }

    void Start()
    {
        // 首局初始化：若 sceneLoaded 事件已先于 Start 执行（Unity 场景加载顺序），这里直接跳过，避免重复刷怪
        if (_roundBuilt) return;
        BeginRound();
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        HealthPack.AnyConsumed -= OnMedkitConsumed;
    }

    void OnSceneLoaded(Scene s, LoadSceneMode m)
    {
        // 每次场景加载（含死亡后重开）都是一局新游戏：重新允许初始化并统一重建一轮内容
        _roundBuilt = false;
        BeginRound();
    }

    /// <summary>开一局新游戏：重置状态、清掉上一局残留动态物体并重建初始内容。同一局内只应执行一次。</summary>
    void BeginRound()
    {
        _roundBuilt = true;

        // 场景重载后旧引用会失效，需要重新查找玩家
        playerTransform = null;
        var pc = FindObjectOfType<PlayerController>();
        if (pc != null) playerTransform = pc.transform;

        // 地图地面同理：旧引用随场景销毁，按 Tag 重新绑定（与 playerTransform 的处理保持一致）
        if (!string.IsNullOrEmpty(mapGroundTag))
        {
            var g = GameObject.FindGameObjectWithTag(mapGroundTag);
            if (g != null) mapGround = g.transform;
        }

        Sfx.Init();
        GameState.Reset();
        GameState.Gold = Mathf.Max(0, startGold);   // ⚠️ 必须在 Reset **之后**：Reset 会把金币归零
        CameraCache.Clear();                      // 上一局的相机已随场景销毁，清掉缓存强制重新查找
        SpawnPosition.BindMapBounds(mapGround);   // 地图边界自适应：有地面引用就按它大小算

        // 重置波次状态（DontDestroyOnLoad 常驻，重开局不能残留上一局状态）
        _phase = WavePhase.Fighting;
        _pendingSpawns = 0;
        _idleTimer = 0f;
        _medkitRespawnAt.Clear();

        // 清掉上一局残留的动态物体（敌人、武器、血包、特效、子弹）
        ClearDynamicObjects();

        // 上一局池里的对象已随场景卸载变成死引用，清空后对象池从零开始正常复用
        ObjectPool.Clear();

        BuildInitialContent();

        Debug.Log("[WaveManager] 波次管理器启动，已动态生成初始敌人/武器/血包");
    }

    /// <summary>开局动态生成初始内容（武器、血包、敌人）。静态场景物体由场景本身提供。</summary>
    void BuildInitialContent()
    {
        // 用一个共享的已占用位置列表，保证开场这批 武器/血包/敌人 互相之间也有间距，不会叠在一起
        var spawned = new List<Vector3>();

        // 初始武器：从武器池随机挑几把铺在地上（彼此拉开距离）。池空则用代码内置 4 把默认枪兜底
        var weapons = PickInitialWeapons();
        foreach (var w in weapons)
            WeaponPickup.Create(w, SpawnPosition.FindOpenSpot(3f, SpawnPosition.RangeWeapon, spawned));

        // 初始血包：3个，随机落点
        for (int i = 0; i < 3; i++)
            HealthPack.Create(SpawnPosition.FindOpenSpot(4f, SpawnPosition.RangeMedkit, spawned));

        // 初始弹药：每种弹药各铺 N 包。弹药卡**自动从物品清单里找** → 以后加新弹药不用改这里
        if (initialAmmoPacksPerType > 0)
        {
            var db = ItemDatabase.I;
            if (db != null && db.all != null)
            {
                int kinds = 0;
                foreach (var def in db.all)
                {
                    var ammo = def as AmmoData;
                    if (ammo == null) continue;
                    kinds++;
                    for (int i = 0; i < initialAmmoPacksPerType; i++)
                        AmmoPickup.Create(ammo, SpawnPosition.FindOpenSpot(4f, SpawnPosition.RangeMedkit, spawned));
                }
                if (kinds == 0)
                    Debug.LogWarning("[WaveManager] 物品清单里没有任何弹药卡 → 开局不铺弹药。请在菜单跑一次 Tools/游戏/重建物品清单");
            }
            else
                Debug.LogWarning("[WaveManager] 物品清单缺失（Assets/Resources/ItemDatabase.asset）→ 开局不铺弹药");
        }

        // 初始敌人：从敌人池随机挑（池里几种就随机几种），数量按波次配置，随机落点且彼此拉开
        for (int i = 0; i < WC.initialEnemies; i++)
            SpawnEnemy(PickConfig(), spawned);
    }

    /// <summary>清掉上一局残留的动态物体（敌人、武器、血包、特效、子弹）</summary>
    void ClearDynamicObjects()
    {
        foreach (var ai in EnemyAI.All) if (ai != null) Destroy(ai.gameObject);
        foreach (var wp in WeaponPickup.All) if (wp != null) Destroy(wp.gameObject);
        foreach (var hp in HealthPack.All) if (hp != null) Destroy(hp.gameObject);
        foreach (var ap in AmmoPickup.All) if (ap != null) Destroy(ap.gameObject);
        // 特效和子弹
        foreach (var t in FindObjectsOfType<TimedEffect>()) Destroy(t.gameObject);
        foreach (var b in FindObjectsOfType<Bullet>()) Destroy(b.gameObject);
    }

    // ---- 波次状态机 Update ----
    void Update()
    {
        if (GameState.GameOver) return;
        // 暂停（背包打开）时：波次计时 / 血包重生 / 僵局计时全部冻结。
        // 目前 timeScale=0 已经能兜住，但那是"靠副作用"；这里显式写出来，
        // 将来若出现"暂停但时间不冻结"的实现（unscaledTime 计时器等），波次也不会偷偷推进。
        if (GameState.Paused) return;

        switch (_phase)
        {
            case WavePhase.Fighting:
                // 找敌僵局兜底：有敌人存活但 18 秒无人交战（看不见玩家也没挨打）
                if (GameState.AliveEnemies > 0)
                {
                    bool anyCombat = false;
                    foreach (var ai in EnemyAI.All)
                        if (ai.InCombat) { anyCombat = true; break; }

                    if (!anyCombat)
                    {
                        _idleTimer += Time.deltaTime;
                        if (_idleTimer >= WC.idleTimeout)   // 僵局兜底时间来自波次配置
                        {
                            _idleTimer = 0f;
                            int moved = 0;
                            foreach (var ai in EnemyAI.All)
                            {
                                var h = ai.GetComponent<Health>();
                                if (h != null && h.alive)
                                {
                                    ai.transform.position = SpawnPosition.NearPlayer(playerTransform);
                                    ai.Alert();
                                    moved++;
                                }
                            }
                            if (moved > 0)
                            {
                                HUD.Announce("敌人循着动静围了过来！", 2f, 30, new Color(1f, 0.5f, 0.4f));
                                Debug.Log($"[WaveManager] 僵局兜底：传送 {moved} 个敌人到玩家附近");
                            }
                        }
                    }
                    else _idleTimer = 0f;
                }
                else _idleTimer = 0f;

                // 敌人清空 → 进入下一波倒计时
                if (GameState.AliveEnemies <= 0)
                {
                    _phase = WavePhase.Clearing;
                    _phaseTimer = WC.clearingDelay;   // 清场倒计时来自波次配置
                    HUD.Announce("区域已清空 · 下一波即将来袭", 2.5f, 30, new Color(0.55f, 1f, 0.65f));
                }
                break;

            case WavePhase.Clearing:
                _phaseTimer -= Time.deltaTime;
                if (_phaseTimer <= 0f)
                {
                    GameState.Wave++;
                    _pendingSpawns = WC.EnemiesForWave(GameState.Wave);   // 人数公式来自波次配置
                    _phase = WavePhase.Spawning;
                    _nextSpawnAt = 0f;
                }
                break;

            case WavePhase.Spawning:
                if (Time.time >= _nextSpawnAt)
                {
                    _nextSpawnAt = Time.time + WC.spawnInterval;   // 刷怪间隔来自波次配置
                    try { SpawnEnemy(PickConfig()); }          // 从敌人池随机挑一种生成
                    catch (System.Exception ex) { Debug.LogError($"[WaveManager] 生成敌人失败: {ex}"); }
                    _pendingSpawns--;
                    if (_pendingSpawns <= 0) _phase = WavePhase.Fighting;
                }
                break;
        }

        // 血包重生：被拾取后 20 秒在地图随机位置再生成一个（上限 3 个，不会堆叠超量）
        if (_medkitRespawnAt.Count > 0)
            for (int i = _medkitRespawnAt.Count - 1; i >= 0; i--)
                if (Time.time >= _medkitRespawnAt[i])
                {
                    if (HealthPack.All.Count < 3)
                        HealthPack.Create(SpawnPosition.FindOpenSpot(4f, SpawnPosition.RangeMedkit));
                    _medkitRespawnAt.RemoveAt(i);
                }
    }

    /// <summary>
    /// 取本局要铺在地上的武器：**武器池里的每一把都会铺出来**（去重后全铺，不再随机挑几把）
    /// —— 于是每次开局，地图上的武器都成套刷新一遍。池空 / 全空时回落到代码内置 4 把默认枪。
    /// </summary>
    WeaponData[] PickInitialWeapons()
    {
        var result = new List<WeaponData>();

        if (weaponPool != null && weaponPool.Length > 0)
        {
            // 去重收集有效武器（同一把枪在池里拖了两次，不会铺两把；池里有几把就铺几把）
            var valid = new List<WeaponData>();
            foreach (var w in weaponPool)
                if (w != null && !valid.Contains(w)) valid.Add(w);

            // 洗牌只影响「摆放顺序」（落点本来就是随机的），保留是为了将来若改成"只铺一部分"仍保持随机
            for (int i = valid.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                var t = valid[i]; valid[i] = valid[j]; valid[j] = t;
            }
            result.AddRange(valid);
        }

        // 池空/全空 → 代码内置 4 把默认枪兜底（保证零资源也能跑）
        if (result.Count == 0)
        {
            result.Add(WeaponData.Pistol());
            result.Add(WeaponData.SMG());
            result.Add(WeaponData.Shotgun());
            result.Add(WeaponData.Sniper());
        }
        return result.ToArray();
    }

    // ---------- 动态刷敌人 ----------

    /// <summary>
    /// 从敌人池按权重随机挑一种配置（过滤掉「未到解锁波次」的项）。
    /// 兜底规则：如果当前波次所有项都被门槛挡住，就放宽到整个池随机
    /// —— 只会在池内选，绝不刷出「池里没有的敌人」（比如代码内置的近战兵）。
    /// 池完全为空时，才回落到下方两个旧兜底槽位。
    /// </summary>
    EnemyConfig PickConfig()
    {
        if (enemyPool != null && enemyPool.Length > 0)
        {
            // 第一次累计：只算当前波次已解锁的
            bool relaxed = false;
            float total = 0f;
            foreach (var e in enemyPool)
                if (e != null && e.config != null && e.minWave <= GameState.Wave)
                    total += Mathf.Max(e.weight, 0f);

            // 一个都没解锁 → 放宽门槛，在整个池里选（保证不刷池外敌人）
            if (total <= 0f)
            {
                relaxed = true;
                foreach (var e in enemyPool)
                    if (e != null && e.config != null)
                        total += Mathf.Max(e.weight, 0f);
            }

            if (total > 0f)
            {
                float roll = Random.value * total;
                foreach (var e in enemyPool)
                {
                    if (e == null || e.config == null) continue;
                    if (!relaxed && e.minWave > GameState.Wave) continue;
                    roll -= Mathf.Max(e.weight, 0f);
                    if (roll <= 0f) return e.config;
                }
                return enemyPool[enemyPool.Length - 1].config;   // 浮点误差兜底
            }
        }

        bool shooter = Random.value < 0.33f;
        return shooter ? (shooterConfig != null ? shooterConfig : EnemyConfig.Shooter())
                       : (meleeConfig != null ? meleeConfig : EnemyConfig.Melee());
    }

    /// <summary>
    /// 生成一个敌人（开局和每波刷新共用）。数值/外形/行为全部取自 EnemyConfig。
    /// 行为 = 配置里选好的 EnemyBehaviour 组件（近战、远程，或你以后新写的自爆/召唤等），
    /// 远程兵的枪与武器由 EnemyShooterBehaviour 自己准备，这里不再区分近战/远程。
    /// occupied：同一批生成的物体共用的「已占用位置」列表，用于互相拉开间距。
    /// </summary>
    void SpawnEnemy(EnemyConfig cfg, List<Vector3> occupied = null)
    {
        if (cfg == null) cfg = EnemyConfig.Melee();   // 极端兜底：保证永远能生成

        Vector3 pos = SpawnPosition.FindOpenSpot(6f, SpawnPosition.RangeEnemy, occupied);
        var e = new GameObject("Enemy_" + cfg.displayName + "_W" + GameState.Wave);
        e.transform.position = pos;
        // 统一进「敌人」层：供 EnemyAI 视线射线排除（否则敌人互相挡视线，后排会"站着不动"）。
        // 层未定义时保持默认层 —— 行为与改动前完全一致，不会静默出问题。
        if (Layers.Enemy >= 0) e.layer = Layers.Enemy;

        // 刚体与碰撞体
        var eRb = e.AddComponent<Rigidbody>();
        eRb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        eRb.constraints = RigidbodyConstraints.FreezeRotation | RigidbodyConstraints.FreezePositionY;

        var col = e.AddComponent<CapsuleCollider>();
        col.height = 1.24f;
        col.radius = 0.4f;

        // 身体模型：优先用敌人配置的 Prefab → 其次资源总表 → 代码生成胶囊体
        var assets = GameAssets.I;
        GameObject bodyPrefab = cfg.bodyPrefab != null ? cfg.bodyPrefab : (assets != null ? assets.enemyMeleePrefab : null);
        GameObject body;
        if (bodyPrefab != null)
        {
            body = Instantiate(bodyPrefab, e.transform);
            body.name = "Body";
        }
        else
        {
            // 用共享网格建（碰撞交给外层的胶囊碰撞体，模型本身不要参与物理）
            body = Effects.CreateMeshObject(PrimitiveType.Capsule, "Body", e.transform, Effects.Shared(cfg.bodyColor));
            body.transform.localPosition = new Vector3(0f, 0.07f, 0f);
            body.transform.localScale = Vector3.one * cfg.bodyScale;
        }

        // 头部（球体）+ 面部（前置薄片）：爆头判定锚点 + 「敌人面朝哪边」的视觉指示。
        // ⚠️ 必须在 AddComponent<Health>() **之前**创建：Health.Awake 会收集「全部子 Renderer」，
        // 受击白闪 / 死亡变色 / 自爆预警闪烁都要连头/脸一起变；晚于 Health 创建就收不到它们。
        var head = CreateHead(e.transform, cfg);

        // 血量：随波次按配置成长
        var hp = e.AddComponent<Health>();
        hp.team = Health.Team.Enemy;
        hp.maxHp = cfg.HpAtWave(GameState.Wave);
        hp.hp = hp.maxHp;

        // 敌人核心 + 行为参数（由配置统一赋值，调配置一处生效）
        var ai = e.AddComponent<EnemyAI>();
        ai.cfg = cfg;
        ai.head = head;
        ai.waypoints = SpawnPosition.MakeWaypoints(pos);
        ai.patrolSpeed = cfg.patrolSpeed;
        ai.sightRange = cfg.sightRange;
        ai.fov = cfg.fov;
        ai.loseSightTime = cfg.loseSightTime;
        ai.chaseSpeed = cfg.chaseSpeed;
        ai.reactionTime = cfg.reactionTime;
        ai.attackRange = cfg.attackRange;
        ai.meleeDamage = cfg.MeleeDamageAt(GameState.Wave);
        ai.meleeCooldown = cfg.meleeCooldown;
        ai.keepDistance = cfg.keepDistance;

        // 挂行为组件：配置下拉里选好的行为（解析失败回落近战）。枪/武器由行为自己准备
        var btype = (cfg.BehaviourType != null && typeof(EnemyBehaviour).IsAssignableFrom(cfg.BehaviourType))
            ? cfg.BehaviourType : typeof(EnemyMeleeBehaviour);
        var beh = (EnemyBehaviour)e.AddComponent(btype);
        beh.Init(ai);
        ai.behaviour = beh;

        HealthBar.Create(e.transform, 2.0f);   // 头顶在 ~1.78（头半径 0.3），血条再往上抬一点，避免压住头
        Effects.Impact(pos + Vector3.up * 0.3f, new Color(0.8f, 0.3f, 1f), 1.2f);  // 出场时的紫色闪光
        GameState.AliveEnemies++;
    }

    /// <summary>
    /// 创建敌人头部 + 面部（纯视觉，**不加 Collider**，见 EnemyHead 的注释）。
    /// 外观全部读 EnemyConfig：headOffset / headRadius / headColor / faceColor，
    /// 以后导入真实模型（不同怪物头的位置/大小不一样）时按怪物单独调，不用改代码。
    /// </summary>
    static EnemyHead CreateHead(Transform parent, EnemyConfig cfg)
    {
        float r = Mathf.Max(0.05f, cfg.headRadius);

        // 头容器：缩放保持 1，里面放「球体 + 面部薄片」两个子物体。
        // ⚠️ 为什么必须有这一层：如果把面部直接挂在「已经被缩放过的球体」下面，它会继承球的缩放
        //（2r = 0.6 倍），位置和尺寸全部缩水 → 面片被球体整个吞掉、肉眼完全看不见（实现时踩过，
        //  旧的错误写法：face 挂在球下面、localPosition.z = r → 实际只有 0.18，小于半径 0.3）。
        var headGo = new GameObject("Head");
        headGo.transform.SetParent(parent, false);
        headGo.transform.localPosition = cfg.headOffset;
        headGo.layer = parent.gameObject.layer;           // 与身体同层（敌人层）

        // 球体：内置球原始直径 1 → 缩放 2r 得到半径 r
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Sphere";
        var headCol = sphere.GetComponent<Collider>();
        if (headCol != null) DestroyImmediate(headCol);   // 不要碰撞体：不参与物理/射线，避免影响刷怪空位与命中判定
        sphere.transform.SetParent(headGo.transform, false);
        sphere.transform.localPosition = Vector3.zero;
        sphere.transform.localScale = Vector3.one * (r * 2f);
        sphere.layer = parent.gameObject.layer;
        sphere.GetComponent<MeshRenderer>().sharedMaterial = Effects.Shared(cfg.headColor);

        // 面部：贴在头正前方（本地 +Z 就是敌人的 forward）的一小块薄片，用高对比色一眼看出朝向。
        // 位置推到 0.95r + 自身半厚 0.15r → 整个面片都在球面之外，从正面一定看得见。
        var faceGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        faceGo.name = "Face";
        var faceCol = faceGo.GetComponent<Collider>();
        if (faceCol != null) DestroyImmediate(faceCol);
        faceGo.transform.SetParent(headGo.transform, false);
        faceGo.transform.localPosition = new Vector3(0f, 0f, r * 0.95f);
        faceGo.transform.localScale = new Vector3(r * 0.70f, r * 0.70f, r * 0.30f);
        faceGo.layer = parent.gameObject.layer;
        faceGo.GetComponent<MeshRenderer>().sharedMaterial = Effects.Shared(cfg.faceColor);

        var head = headGo.AddComponent<EnemyHead>();
        head.radius = r;   // 爆头判定圈按它投影到屏幕：与上面画出来的球同源（改 FOV/分辨率不影响爆头平衡）
        return head;
    }

    /// <summary>血包被拾取后的回调：登记一个 20 秒后的重生点</summary>
    void OnMedkitConsumed()
    {
        _medkitRespawnAt.Add(Time.time + 20f);
    }
}
