using UnityEngine;

/// <summary>
/// 敌人头部：爆头判定的锚点 + 面部朝向指示的挂点。
///
/// 由 WaveManager.SpawnEnemy 在运行时创建。**故意不带 Collider**：
///   - 不加碰撞体 → 完全不参与物理与射线，不影响刷怪空位检测（SpawnPosition.CheckSphere）、
///     子弹命中、墙体遮挡、以及"子弹打墙"的判定，零副作用。
///   - 爆头判定在**屏幕空间**做（准星屏幕坐标 vs 本物体投影到屏幕的位置），
///     判定圆半径按屏幕高度比例（与距离/分辨率无关），见 PlayerController.Fire。
///
/// 外观（偏移 / 半径 / 颜色）全部来自 EnemyConfig，可在 Inspector 里按怪物单独调，
/// 因此以后导入真实模型、头的位置和大小不一样时，不需要改代码。
/// </summary>
public class EnemyHead : MonoBehaviour
{
    /// <summary>头部中心的世界坐标（爆头判定锚点）</summary>
    public Vector3 WorldPosition => transform.position;
}
