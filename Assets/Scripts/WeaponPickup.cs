using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地上的武器拾取物：上下浮动旋转，靠近时提示，按 F 拾取。
/// ⚠️ 交互键是 **F**（`InteractKey => "F"`）—— E 已于 2026-09-28 让位给魔法释放，别再写成 E。
///
/// 2026-09-24 起交互改成走 <see cref="InteractFocus"/>（"单一交互焦点"）：
///   本类只负责"我是什么、我在哪、按键时做什么"，**不再自己每帧扫描 + 自己检测按键** ——
///   否则它和血包 / 箱子会互相抢那唯一一个气泡（详见 <see cref="IInteractable"/> 的注释）。
/// </summary>
public class WeaponPickup : MonoBehaviour, IInteractable
{
    /// <summary>全场武器拾取物注册表。用列表代替每帧 FindObjectsOfType 全场景搜索。</summary>
    public static readonly List<WeaponPickup> All = new List<WeaponPickup>();

    /// <summary>这把拾取物对应的武器数据</summary>
    public WeaponData data;

    /// <summary>它的弹匣余量（**-1 = 满弹**）。丢在地上的枪会带着真实余量 → 捡回来不会白送满弹。
    /// （新枪 / 敌人掉落 / 开局铺的枪都是 -1。）</summary>
    public int magAmmo = -1;

    /// <summary>弹匣里装的**哪一张弹药卡**（= 等级；`null` = 未知 → 装填时自动挑该口径有货的最低等级）。
    /// 与 `magAmmo` 一起随枪流转 —— 漏了它，丢地上再捡回来的枪会"等级重置"，打完退回的弹就串级了。</summary>
    public AmmoData ammoId;

    /// <summary>拾取距离（米）。与"交互焦点"用同一个值。</summary>
    public const float PickupRange = 1.6f;

    float _baseY, _t;

    void Awake()
    {
        All.Add(this);
        InteractFocus.Register(this);
    }

    void OnDestroy()
    {
        All.Remove(this);
        InteractFocus.Unregister(this);   // 注册配注销（场景重载后焦点表里不会留死引用）
    }

    /// <summary>在指定位置创建一个武器拾取物（优先用武器资源里配的模型，否则代码生成默认外观）。
    /// `magAmmo` 负值 = 满弹（新枪）；主动丢在地上的枪会传真实余量。</summary>
    public static WeaponPickup Create(WeaponData w, Vector3 pos, int magAmmo = -1, AmmoData ammoId = null)
    {
        var root = new GameObject("WeaponPickup_" + w.weaponName);
        root.transform.position = pos;

        if (w.pickupModelPrefab != null)
        {
            var model = Instantiate(w.pickupModelPrefab, root.transform);
            model.name = "Model";
        }
        else
        {
            // 代码生成的默认外观：枪身 + 枪管 + 颜色标识条（统一用 Effects.CreateMeshObject：共享网格、无碰撞体）
            var body = Effects.CreateMeshObject(PrimitiveType.Cube, "Body", root.transform,
                                                Effects.Shared(new Color(0.25f, 0.25f, 0.28f)));
            body.transform.localPosition = Vector3.zero;
            body.transform.localScale = new Vector3(0.22f, 0.12f, 0.62f);

            // 枪管（长度随武器变化：狙击枪明显更长）
            var barrel = Effects.CreateMeshObject(PrimitiveType.Cube, "Barrel", root.transform,
                                                  Effects.Shared(new Color(0.15f, 0.15f, 0.17f)));
            float barrelLen = w.gunLength * 0.55f;
            barrel.transform.localPosition = new Vector3(0f, 0.04f, 0.26f + barrelLen * 0.5f);
            barrel.transform.localScale = new Vector3(0.08f, 0.08f, barrelLen);

            // 颜色标识条
            var stripe = Effects.CreateMeshObject(PrimitiveType.Cube, "Stripe", root.transform,
                                                  Effects.Shared(w.pickupColor));
            stripe.transform.localPosition = new Vector3(0f, 0.09f, 0f);
            stripe.transform.localScale = new Vector3(0.24f, 0.03f, 0.5f);
        }

        var p = root.AddComponent<WeaponPickup>();
        p.data = w;
        p.magAmmo = magAmmo;
        p.ammoId = ammoId;
        p._baseY = pos.y;
        p._t = Random.Range(0f, 6f);

        // 拾取物不阻挡子弹和玩家移动（立即移除，不留一帧物理碰撞）
        foreach (var col in root.GetComponentsInChildren<Collider>()) DestroyImmediate(col);

        // 归入 Pickup 层：**语义明确**（子弹/视线掩码里都不含它），将来若加碰撞体也不会被误伤
        if (Layers.Pickup >= 0)
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layers.Pickup;
        return p;
    }

    void Update()
    {
        _t += Time.deltaTime;
        transform.position = new Vector3(transform.position.x, _baseY + Mathf.Sin(_t * 2.5f) * 0.08f, transform.position.z);
        transform.Rotate(Vector3.up, 60f * Time.deltaTime);
    }

    // ==================== IInteractable ====================

    public Transform InteractAnchor => transform;
    public string InteractLabel => data != null ? data.DisplayName : "武器";
    public string InteractKey => "F";
    public float InteractRange => PickupRange;

    /// <summary>按键拾取（由交互焦点调用）。满了就把枪留在原地并提示"背包已满"。</summary>
    public void Interact(PlayerController player)
    {
        if (player == null || data == null) return;

        var bp = player.backpack;
        if (bp != null && !bp.CanAddWeapon(data))
        {
            HUD.AnnounceBackpackFull();
            return;
        }

        bool intoSlot = bp != null && bp.HasEmptySlot();
        // ⚠️ 检查返回值：万一真收不下（`CanAddWeapon` 漏判 / 没挂背包），**枪必须留在原地** ——
        //    下面那句 `Destroy(gameObject)` 是无条件的，不检查就会把枪销毁掉（凭空消失）。
        // `magAmmo` / `ammoId` 原样带过去：地上的枪保留自己的弹匣余量与等级（-1 = 满弹、null = 未知）。
        if (!player.Equip(data, magAmmo, ammoId))
        {
            HUD.AnnounceBackpackFull();
            return;
        }
        HUD.Announce(intoSlot ? "拾取  " + data.DisplayName : data.DisplayName + " 收入背包",
                     1.0f, 28, data.pickupColor);
        Sfx.Pickup();
        Destroy(gameObject);
    }
}
