using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 进屋隐藏屋顶（挂在 Main Camera 上）：
/// 玩家进入某栋建筑的外墙范围（缓冲 `roofBuffer`）→ 隐藏该栋屋顶类节点（`屋顶*` / `女儿墙*`）的**渲染**，
/// 走出即恢复。判定用"外墙部件的世界包围盒"，纯几何、零碰撞体开销、不碰场景结构。
///
/// ⚠️ 约定（见 §4 #42，都别随手改）：
///   · **只关 `Renderer.enabled`，绝不 `SetActive(false)`** —— 屋顶节点带 MeshCollider，
///     SetActive 会把碰撞一起消掉（子弹 / 敌人视线 / 魔法投射物会穿屋顶）；
///   · 记录每个渲染器在场景里的**原始开关状态**，恢复时按原状还原（别假设"原本一定是开的"）；
///   · 层 / 相机此类"收敛点"分别走 `Layers` 与 `CameraCache`；**零 static 状态**（不涉及 StaticReset）。
///
/// 📌 2026-10-03：原先本类还负责"遮挡圆形透视（屏幕空间挖洞）"，已按用户要求**整段删除**，
///    改为 `PlayerXRay.cs`（把玩家轮廓叠加在遮挡物上，逐像素判定）——这样遮挡物 100% 完整、不再有"感知不到障碍物"的问题。
/// </summary>
[DefaultExecutionOrder(100)]   // 排在 CameraRig(默认 0) 之后：读到的是本帧最终的相机位置（虽然本类只用玩家位置）
public class CameraOcclusion : MonoBehaviour
{
    [Header("进屋隐藏屋顶")]
    [Tooltip("总开关。关闭时：屋顶不会被隐藏（也不会有任何状态被改写）。")]
    public bool hideRoofs = true;
    [Tooltip("建筑根节点的名字（场景里装 10 栋建筑的那个父物体）。")]
    public string buildingsRootName = "建筑";
    [Tooltip("外墙范围外扩多少米仍算「在屋内」（防止站在门口时屋顶反复闪）。")]
    public float roofBuffer = 0.3f;

    /// <summary>屋顶渲染器条目：记录组件 + 它在场景里原本的开关状态（恢复时按原状还原）。</summary>
    class RoofRenderer
    {
        public Renderer renderer;
        public bool enabledAtStart;
    }

    class Building
    {
        public Vector4 rect;                              // 外墙范围 (minX, maxX, minZ, maxZ)
        public readonly List<RoofRenderer> roofs = new List<RoofRenderer>();
        public bool hidden;                               // 屋顶当前是否已被隐藏
        public bool inside;                               // 玩家当前是否在其中
    }

    Transform _player;
    readonly List<Building> _buildings = new List<Building>();

    void Awake()
    {
        var pc = PlayerController.Instance;
        if (pc == null) pc = FindObjectOfType<PlayerController>();
        _player = pc != null ? pc.transform : null;
        if (_player == null)
            Debug.LogWarning("[CameraOcclusion] 场景里没有 PlayerController → 室内判定不会生效。", this);

        CollectBuildings();
    }

    /// <summary>扫描建筑：记录每栋的「屋顶渲染器（含原始开关状态）」与「外墙包围盒」。
    /// ⚠️ 提到 `internal`（2026-10-26）：训练图脚本会在自己的 `Start` 里补调一次 ——
    ///    因为它自己的 `Awake` 跑在场景对象就绪前（实测 `_buildings.Count = 0`）。</summary>
    internal void CollectBuildings()
    {
        _buildings.Clear();
        var root = GameObject.Find(buildingsRootName);
        if (root == null)
        {
            Debug.LogWarning("[CameraOcclusion] 找不到名为「" + buildingsRootName + "」的建筑根节点 → 屋顶隐藏不会生效。", this);
            return;
        }

        foreach (Transform bld in root.transform)
        {
            var b = new Building();

            // 屋顶类节点：屋顶_南坡 / 屋顶_北坡 / 屋顶_脊瓦 / 屋顶（民居B）/ 屋顶板 + 女儿墙_*（商铺）
            foreach (Transform c in bld)
            {
                if (!c.name.StartsWith("屋顶") && !c.name.StartsWith("女儿墙")) continue;
                var rs = c.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++)
                {
                    var item = new RoofRenderer();
                    item.renderer = rs[i];
                    item.enabledAtStart = rs[i].enabled;
                    b.roofs.Add(item);
                }
            }

            // 外墙范围（只统计 外墙_ / 山墙_ 部件 → 不含月台、雨棚、门廊、铺装等附属物）
            bool has = false;
            Bounds bb = new Bounds();
            foreach (Transform c in bld)
            {
                if (!c.name.StartsWith("外墙_") && !c.name.StartsWith("山墙_")) continue;
                var rs = c.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++)
                {
                    if (!has) { bb = rs[i].bounds; has = true; }
                    else bb.Encapsulate(rs[i].bounds);
                }
            }
            if (!has || b.roofs.Count == 0) continue;   // 不是建筑（或没有屋顶）→ 跳过

            b.rect = new Vector4(bb.min.x, bb.max.x, bb.min.z, bb.max.z);
            _buildings.Add(b);
        }
    }

    void LateUpdate()
    {
        UpdateInsideFlags();
        if (hideRoofs) UpdateRoofVisibility();
    }

    /// <summary>玩家是否在某栋建筑的外墙范围内（带缓冲）。</summary>
    void UpdateInsideFlags()
    {
        if (_player == null) return;
        Vector3 p = _player.position;
        for (int i = 0; i < _buildings.Count; i++)
        {
            var b = _buildings[i];
            b.inside = p.x >= b.rect.x - roofBuffer && p.x <= b.rect.y + roofBuffer
                    && p.z >= b.rect.z - roofBuffer && p.z <= b.rect.w + roofBuffer;
        }
    }

    /// <summary>只在状态变化时写 Renderer.enabled（避免每帧无谓调用）。</summary>
    void UpdateRoofVisibility()
    {
        for (int i = 0; i < _buildings.Count; i++)
        {
            var b = _buildings[i];
            if (b.inside == b.hidden) continue;   // hidden = "屋顶当前是否已被藏起来"
            b.hidden = b.inside;
            for (int j = 0; j < b.roofs.Count; j++)
            {
                var item = b.roofs[j];
                if (item.renderer == null) continue;
                // 隐藏时关渲染；恢复时还原"场景原本的开关状态"（不假设它原本一定是开的）
                item.renderer.enabled = b.inside ? false : item.enabledAtStart;
            }
        }
    }

    /// <summary>组件禁用 / 销毁时把屋顶全部还原（不留任何被改过的状态）。</summary>
    void OnDisable()
    {
        for (int i = 0; i < _buildings.Count; i++)
        {
            var b = _buildings[i];
            b.inside = false;
            if (!b.hidden) continue;
            b.hidden = false;
            for (int j = 0; j < b.roofs.Count; j++)
            {
                var item = b.roofs[j];
                if (item.renderer != null) item.renderer.enabled = item.enabledAtStart;
            }
        }
    }
}
