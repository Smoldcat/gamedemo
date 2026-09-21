using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地图上的「找空地」工具：随机落点（避开障碍物）、玩家附近落点、巡逻点生成。
/// 所有单位（敌人/武器/血包）生成前都用这里找一个不卡墙、不重叠的位置。
/// 纯工具类，不依赖任何 MonoBehaviour 实例。
/// </summary>
public static class SpawnPosition
{
    /// <summary>单个物体占位的方形边长</summary>
    public const float SpotSize = 1.2f;
    /// <summary>障碍检测球半径</summary>
    public const float ProbeRadius = 0.6f;
    /// <summary>同一批物体之间的最小间距</summary>
    public const float MinSpacing = 1.5f;

    /// <summary>默认地图半宽（对应 30×30 地图）。没拖地面引用时用它，也是 ResetStatics 的复位值</summary>
    public const float DefaultMapHalfSize = 15f;

    /// <summary>地图 XZ 半宽（每局从地面读取；没拖地面引用则保持默认 15）</summary>
    public static Vector2 MapHalfSize = new Vector2(DefaultMapHalfSize, DefaultMapHalfSize);

    /// <summary>地图可用的"生成半径"（米）= 地图 XZ 短边的一半（由地面尺寸算出，见 <see cref="BindMapBounds"/>）。
    /// ⚠️ 它比**围墙内侧**还大一点点（`MapHalfSize` 是"地面半宽 − 1"，而墙内侧约"地面半宽 − 2.5"），
    ///    所以下面的生成半径都**乘了 &lt;1 的比例**，保证敌人不会刷到墙外。</summary>
    public static float Radius => Mathf.Min(MapHalfSize.x, MapHalfSize.y);

    /// <summary>敌人生成半径（米）= 地图半径 × 0.85（最大的一档，但仍在围墙内侧）</summary>
    public static float RangeEnemy => Radius * 0.85f;
    /// <summary>血包生成半径（米）= 地图半径 × 0.75</summary>
    public static float RangeMedkit => Radius * 0.75f;
    /// <summary>武器生成半径（米）= 地图半径 × 0.65</summary>
    public static float RangeWeapon => Radius * 0.65f;

    /// <summary>静态状态清理（供 StaticReset 调用）：把地图边界复位为默认值。
    /// 否则关闭 Reload Domain 时，「先拖了地面、后又取消引用」会让上一局按地面算出的边界残留到下一局
    ///（表现为刷怪范围突然变得和当前地图不匹配，且没有任何报错）。</summary>
    internal static void ResetStatics()
    {
        MapHalfSize = new Vector2(DefaultMapHalfSize, DefaultMapHalfSize);
    }

    /// <summary>
    /// 从地面物体读取地图边界（WaveManager 每局调用）。
    /// 地面带 Collider 时按其尺寸自动适配；留空就保持默认 30×30。
    /// </summary>
    public static void BindMapBounds(Transform ground)
    {
        if (ground == null) return;
        var col = ground.GetComponent<Collider>();
        if (col == null) return;

        var b = col.bounds;
        // 留 1 米边距（敌人别刷到地图最边缘），并设下限避免极端小的地面
        MapHalfSize = new Vector2(Mathf.Max(b.extents.x - 1f, 5f), Mathf.Max(b.extents.z - 1f, 5f));
    }

    /// <summary>
    /// 在地图上随机找一个空位：随机 100 次，要求离开中心至少 minDistFromCenter，且不和障碍物重叠。
    /// occupied 不为空时，还会与已占用的位置保持 MinSpacing 间距（防止同一批物体叠在一起）。
    /// </summary>
    public static Vector3 FindOpenSpot(float minDistFromCenter, float maxRange, List<Vector3> occupied = null)
    {
        for (int i = 0; i < 100; i++)
        {
            Vector2 p = new Vector2(Random.Range(-maxRange, maxRange), Random.Range(-maxRange, maxRange));
            if (p.magnitude < minDistFromCenter) continue;

            Vector3 spot = new Vector3(p.x, GameConst.EntityPlaneY, p.y);
            if (OverlapsAny(SpotRect(spot))) continue;

            if (occupied != null)
            {
                bool tooClose = false;
                foreach (var o in occupied)
                    if (Vector3.Distance(o, spot) < MinSpacing) { tooClose = true; break; }
                if (tooClose) continue;
                occupied.Add(spot);
            }
            return spot;
        }

        // 100 次都没找到（极端情况）：返回一个保底位置，保证调用方永远拿得到有效坐标
        return new Vector3(minDistFromCenter, GameConst.EntityPlaneY, 0f);
    }

    /// <summary>在某个玩家周围 10~13 米处找一个空位（僵局兜底把敌人传送过来用）</summary>
    public static Vector3 NearPlayer(Transform playerTransform)
    {
        if (playerTransform == null) return FindOpenSpot(8f, RangeMedkit);

        for (int i = 0; i < 40; i++)
        {
            Vector2 d = Random.insideUnitCircle.normalized * Random.Range(10f, 13f);
            Vector3 p = new Vector3(
                Mathf.Clamp(playerTransform.position.x + d.x, -MapHalfSize.x - 0.5f, MapHalfSize.x + 0.5f), GameConst.EntityPlaneY,
                Mathf.Clamp(playerTransform.position.z + d.y, -MapHalfSize.y - 0.5f, MapHalfSize.y + 0.5f));

            if (!OverlapsPoint(p, ProbeRadius)) return p;
        }
        return FindOpenSpot(8f, RangeMedkit);
    }

    /// <summary>给敌人生成 4 个巡逻点：优先在出生点附近随机取，被挡住则改用全图随机点</summary>
    public static Vector3[] MakeWaypoints(Vector3 around)
    {
        var list = new List<Vector3>();
        for (int i = 0; i < 4; i++)
        {
            Vector3 fallback = FindOpenSpot(0f, RangeEnemy);
            Vector2 offset = Random.insideUnitCircle * 6f;
            Vector3 candidate = new Vector3(
                Mathf.Clamp(around.x + offset.x, -MapHalfSize.x, MapHalfSize.x), GameConst.EntityPlaneY,
                Mathf.Clamp(around.z + offset.y, -MapHalfSize.y, MapHalfSize.y));

            list.Add(OverlapsAny(SpotRect(candidate)) ? fallback : candidate);
        }
        return list.ToArray();
    }

    /// <summary>以某点为中心、边长 SpotSize 的占位方框（XZ 平面）</summary>
    static Rect SpotRect(Vector3 center)
    {
        var half = Vector2.one * (SpotSize * 0.5f);
        return new Rect(new Vector2(center.x, center.z) - half, half * 2f);
    }

    /// <summary>方框范围内是否碰到障碍物</summary>
    static bool OverlapsAny(Rect r)
    {
        return OverlapsPoint(new Vector3(r.center.x, 0f, r.center.y), ProbeRadius);
    }

    /// <summary>
    /// 以某点为中心、radius 为半径的球形检测。
    /// 检测球中心抬高到 ObstacleProbeY：必须高于地面顶面（y=0），否则会把地面本身当成障碍物，
    /// 导致所有位置都判定为「被占用」。
    /// </summary>
    static bool OverlapsPoint(Vector3 p, float radius)
    {
        return Physics.CheckSphere(new Vector3(p.x, GameConst.ObstacleProbeY, p.z), radius);
    }
}