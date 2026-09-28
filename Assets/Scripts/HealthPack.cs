using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 血包（地图上的药箱）：白色药箱 + 红十字，漂浮旋转。靠近按 E 拾取（**占背包 1 格**，按 Q 使用回血）。
///
/// 2026-09-24 起"血包"是**物品定义卡**（`MedkitData`）而不是代码里写死的枚举：
///   · 回血量从定义卡读（**不再写死 50**）→ 想加"大血包 / 小血包"只需再建一张卡，零代码
///   · 卡从**物品清单**里按类型取（<see cref="PlayerBackpack.MedkitDef"/>）→ 这里不需要手动配引用；
///     想给某个药箱换别的卡，也可以在 Inspector 里直接拖
///
/// 交互走 <see cref="InteractFocus"/>（单一交互焦点）：本类**不再自己每帧扫描 + 检测按键**
/// —— 否则它会和地上的武器 / 箱子抢那唯一一个气泡（详见 <see cref="IInteractable"/> 的注释）。
/// </summary>
public class HealthPack : MonoBehaviour, IInteractable
{
    /// <summary>全场血包注册表。用列表代替全场景搜索，WaveManager 靠它统计场上数量。</summary>
    public static readonly List<HealthPack> All = new List<HealthPack>();

    /// <summary>血包被玩家拾取时触发（WaveManager 用来安排 20 秒后在地图随机位置重生）</summary>
    public static System.Action AnyConsumed;

    /// <summary>交互距离（米）—— 沿用旧值（比地上武器稍近）。</summary>
    public const float PickupRange = 1.2f;

    [Tooltip("本药箱给哪张物品定义卡。留空 → 自动从物品清单里取「血包」那张（Assets/Items/Consumables/Medkit.asset）")]
    public MedkitData item;

    /// <summary>静态状态清理（供 StaticReset 调用）：清掉静态事件订阅链。
    /// 关闭 Reload Domain 时，上一局的旧 WaveManager 实例虽已销毁，但委托链还挂在这里
    /// → 每次拾取血包会同时通知「新实例 + 死实例」（死实例还会因此报 MissingReference）。</summary>
    internal static void ResetStatics()
    {
        AnyConsumed = null;
    }

    float _baseY, _t;

    /// <summary>归入 Pickup 层。⚠️ 血包当前**没有碰撞体**，所以分层不影响物理；
    /// 分层的意义是"语义明确"——将来若给它加碰撞体/触发器，子弹和视线不会误伤它。</summary>
    static void SetPickupLayer(GameObject root)
    {
        if (root == null || Layers.Pickup < 0) return;
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layers.Pickup;
    }

    void Awake()
    {
        All.Add(this);
        InteractFocus.Register(this);
        if (item == null) item = PlayerBackpack.MedkitDef();
    }

    void OnDestroy()
    {
        All.Remove(this);
        InteractFocus.Unregister(this);   // 注册配注销
    }

    public static HealthPack Create(Vector3 pos)
    {
        var root = new GameObject("HealthPack");
        root.transform.position = pos;

        var assets = GameAssets.I;
        if (assets != null && assets.healthPackPrefab != null)
        {
            // 用资源表里配置的模型 Prefab
            var model = Object.Instantiate(assets.healthPackPrefab, root.transform);
            model.name = "Model";
        }
        else
        {
            // 代码生成的默认外观：白色药箱 + 红十字（统一用 Effects.CreateMeshObject：共享网格、无碰撞体）
            var box = Effects.CreateMeshObject(PrimitiveType.Cube, "Box", root.transform,
                                               Effects.Shared(new Color(0.92f, 0.92f, 0.92f)));
            box.transform.localScale = new Vector3(0.42f, 0.3f, 0.42f);

            var red = Effects.Shared(new Color(0.85f, 0.12f, 0.12f));

            // 红十字（两条）
            var c1 = Effects.CreateMeshObject(PrimitiveType.Cube, "Cross1", root.transform, red);
            c1.transform.localPosition = new Vector3(0f, 0.17f, 0f);
            c1.transform.localScale = new Vector3(0.3f, 0.04f, 0.11f);

            var c2 = Effects.CreateMeshObject(PrimitiveType.Cube, "Cross2", root.transform, red);
            c2.transform.localPosition = new Vector3(0f, 0.17f, 0f);
            c2.transform.localScale = new Vector3(0.11f, 0.04f, 0.3f);
        }

        var p = root.AddComponent<HealthPack>();
        p._baseY = pos.y;
        p._t = Random.Range(0f, 6f);
        SetPickupLayer(root);
        return p;
    }

    void Update()
    {
        // 只做"漂浮旋转"的表现；拾取完全交给交互焦点（不再在本类里扫描 + 检测按键）
        _t += Time.deltaTime;
        transform.position = new Vector3(transform.position.x, _baseY + Mathf.Sin(_t * 2.2f) * 0.07f, transform.position.z);
        transform.Rotate(Vector3.up, 50f * Time.deltaTime);
    }

    // ==================== IInteractable ====================

    public Transform InteractAnchor => transform;
    public string InteractKey => "F";
    public float InteractRange => PickupRange;

    /// <summary>气泡里的名字：用物品定义卡的显示名（所以"大血包"这类新卡会自动显示成自己的名字）。</summary>
    public string InteractLabel
    {
        get
        {
            if (item == null) item = PlayerBackpack.MedkitDef();   // 兜底：清单可能比本物体晚就绪
            return item != null ? item.DisplayName : "血包";
        }
    }

    /// <summary>按键拾取（由交互焦点调用）。血包是**占格物品**：背包满了就装不下、药箱留在原地。</summary>
    public void Interact(PlayerController player)
    {
        if (player == null || player.backpack == null) return;

        if (item == null) item = PlayerBackpack.MedkitDef();
        if (item == null)
        {
            Debug.LogWarning("[HealthPack] 找不到「血包定义卡」→ 无法拾取。" +
                             "请检查 Assets/Items/Consumables/Medkit.asset 是否存在，并跑一次菜单 Tools/游戏/重建物品清单。");
            return;
        }

        if (player.backpack.AddItems(item, 1) <= 0)
        {
            HUD.AnnounceBackpackFull();
            return;   // 药箱留在原地（和武器一样，等玩家腾出格子）
        }

        Sfx.Heal();
        HUD.Announce("拾取" + item.DisplayName + "  按 Q 使用", 1.1f, 28, new Color(0.4f, 1f, 0.5f));
        Effects.Impact(transform.position + Vector3.up * 0.3f, new Color(0.4f, 1f, 0.45f), 1.1f);
        AnyConsumed?.Invoke();   // 通知 WaveManager：20 秒后在随机位置重生一个
        Destroy(gameObject);
    }
}
