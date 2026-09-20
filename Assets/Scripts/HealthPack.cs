using System.Collections.Generic;
using UnityEngine;

/// 血包：白色药箱 + 红十字，漂浮旋转。靠近按 E 拾取（**占背包 1 格**，按 Q 使用回血）。
public class HealthPack : MonoBehaviour
{
    /// <summary>全场血包注册表。用列表代替全场景搜索，WaveManager 靠它统计场上数量。</summary>
    public static readonly List<HealthPack> All = new List<HealthPack>();

    /// <summary>血包被玩家拾取时触发（WaveManager 用来安排 20 秒后在地图随机位置重生）</summary>
    public static System.Action AnyConsumed;

    /// <summary>静态状态清理（供 StaticReset 调用）：清掉静态事件订阅链。
    /// 关闭 Reload Domain 时，上一局的旧 WaveManager 实例虽已销毁，但委托链还挂在这里
    /// → 每次拾取血包会同时通知「新实例 + 死实例」（死实例还会因此报 MissingReference）。</summary>
    internal static void ResetStatics()
    {
        AnyConsumed = null;
    }

    float _baseY, _t;
    Health _playerHealth;   // 玩家生命组件缓存（避免每帧 GetComponent）

    /// <summary>归入 Pickup 层。⚠️ 血包当前**没有碰撞体**，所以分层不影响物理；
    /// 分层的意义是"语义明确"——将来若给它加碰撞体/触发器，子弹和视线不会误伤它。</summary>
    static void SetPickupLayer(GameObject root)
    {
        if (root == null || Layers.Pickup < 0) return;
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layers.Pickup;
    }

    void Awake() { All.Add(this); }
    void OnDestroy() { All.Remove(this); }

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
        _t += Time.deltaTime;
        transform.position = new Vector3(transform.position.x, _baseY + Mathf.Sin(_t * 2.2f) * 0.07f, transform.position.z);
        transform.Rotate(Vector3.up, 50f * Time.deltaTime);

        if (GameState.GameOver || GameState.Paused) return;
        var pc = PlayerController.Instance;
        if (pc == null) return;
        var hp = _playerHealth != null ? _playerHealth : (_playerHealth = pc.GetComponent<Health>());
        if (hp == null || !hp.alive) return; // 死亡玩家不拾取

        if (Vector3.SqrMagnitude(transform.position - pc.transform.position) < 1.2f * 1.2f)
        {
            HUD.ShowPickupBubble(transform, "血包");   // 物体旁边的气泡（名称 + 按键圆标）
            if (Input.GetKeyDown(KeyCode.E) && GameState.PickupEConsumedFrame != Time.frameCount)
            {
                GameState.PickupEConsumedFrame = Time.frameCount; // 占掉本帧 E 键，防止与武器同帧双拾取
                var bp = pc.backpack;
                if (bp != null && !bp.AddMedkit(1))   // 血包也是占格物品：背包满了就装不下
                {
                    HUD.Announce("背包已满", 1.2f, 28, new Color(1f, 0.62f, 0.4f));
                    return;   // 药箱留在原地（和武器一样，等玩家腾出格子）
                }
                Sfx.Heal();
                HUD.Announce("拾取血包  按 Q 使用", 1.1f, 28, new Color(0.4f, 1f, 0.5f));
                Effects.Impact(transform.position + Vector3.up * 0.3f, new Color(0.4f, 1f, 0.45f), 1.1f);
                AnyConsumed?.Invoke();   // 通知 WaveManager：20 秒后在随机位置重生一个
                Destroy(gameObject);
            }
        }
    }
}
