using System.Collections.Generic;
using UnityEngine;

/// 地上的武器拾取物：上下浮动旋转，靠近时提示，按 E 拾取。
public class WeaponPickup : MonoBehaviour
{
    /// <summary>全场武器拾取物注册表。用列表代替每帧 FindObjectsOfType 全场景搜索。</summary>
    public static readonly List<WeaponPickup> All = new List<WeaponPickup>();

    /// <summary>这把拾取物对应的武器数据</summary>
    public WeaponData data;

    float _baseY, _t;

    void Awake() { All.Add(this); }
    void OnDestroy() { All.Remove(this); }

    /// <summary>在指定位置创建一个武器拾取物（优先用武器资源里配的模型，否则代码生成默认外观）</summary>
    public static WeaponPickup Create(WeaponData w, Vector3 pos)
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

    /// 由玩家每帧调用：找最近拾取物，按 E 拾取（每帧全扫；用平方距离避免开方）
    /// 注意：不要加"扫描降频"，否则按 E 的按键检测会被锁在扫描帧里漏掉（GetKeyDown 只在按下帧有效）。
    public static void TryPickup(PlayerController player)
    {
        if (GameState.GameOver || GameState.Paused) return;

        WeaponPickup nearest = null;
        float bestSq = 1.6f * 1.6f;
        foreach (var p in All)
        {
            if (p == null) continue;
            float dSq = (p.transform.position - player.transform.position).sqrMagnitude;
            if (dSq < bestSq) { bestSq = dSq; nearest = p; }
        }

        if (nearest != null)
        {
            // 物体旁边的气泡（名称 + 按键圆标）：比原来屏幕上固定的一行字更直观，
            // 而且能一眼看出"气泡指着的是哪把枪"。
            HUD.ShowPickupBubble(nearest.transform, nearest.data != null ? nearest.data.weaponName : "武器");
            if (Input.GetKeyDown(KeyCode.E) && GameState.PickupEConsumedFrame != Time.frameCount)
            {
                GameState.PickupEConsumedFrame = Time.frameCount; // 占掉本帧 E 键，防止与血包同帧双拾取

                var bp = player.backpack;
                if (bp != null && !bp.CanAddWeapon(nearest.data))
                {
                    // 快捷槽满 + 库存满且无同类 → 捡不了，枪留在原地（"背包已满"）
                    HUD.AnnounceBackpackFull();
                    return;
                }

                bool intoSlot = bp != null && bp.HasEmptySlot();
                player.Equip(nearest.data);
                HUD.Announce(intoSlot ? "拾取  " + nearest.data.weaponName : nearest.data.weaponName + " 收入背包", 1.0f, 28, nearest.data.pickupColor);
                Sfx.Pickup();
                Destroy(nearest.gameObject);
            }
        }
    }
}
