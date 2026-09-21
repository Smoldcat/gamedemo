using UnityEngine;

/// <summary>
/// 波次配置（配置用）：把原本写死在 WaveManager 里的波次难度数值抽成可编辑的资源。
/// - 推荐做法：Project 右键「Create > 游戏 > 波次配置」创建 .asset，拖进 WaveManager 的 waveConfig 槽
/// - 代码兜底：Default() 提供默认值，资源缺失时项目照常跑
/// </summary>
[CreateAssetMenu(fileName = "Wave_新配置", menuName = "游戏/波次配置", order = 0)]
public class WaveConfig : ScriptableObject
{
    [Tooltip("开局第一波生成的敌人数")]
    public int initialEnemies = 6;

    [Header("波次人数")]
    [Tooltip("波次人数基数：每波人数 = 基数 + 波次 × 每波增量")]
    public int baseEnemies = 4;
    [Tooltip("每波人数增量")]
    public int enemiesPerWave = 1;
    [Tooltip("单波敌人数量上限")]
    public int maxEnemies = 14;

    [Header("节奏")]
    [Tooltip("刷怪间隔（秒）：每波内两个敌人之间的生成间隔")]
    public float spawnInterval = 0.25f;
    [Tooltip("清场倒计时（秒）：清完一波到下一波开始的等待")]
    public float clearingDelay = 3f;
    [Tooltip("僵局兜底时间（秒）：敌人长时间没交战会被传送到玩家附近")]
    public float idleTimeout = 18f;

    /// <summary>按当前波次算出这一波要刷的敌人数</summary>
    public int EnemiesForWave(int wave) => Mathf.Min(baseEnemies + wave * enemiesPerWave, maxEnemies);

    /// <summary>
    /// 配置数值合法性兜底（仅编辑器生效：Inspector 改完立即夹回合法范围）。
    /// 最危险的两个：`spawnInterval = 0` → 每帧尝试刷怪；`maxEnemies` 填 0/负数 → `EnemiesForWave` 恒取它、波次失去成长。
    /// ⚠️ 只用 if 判断、不无条件赋值，避免把合法值也标成「已修改」而脏化资源（与 `WeaponData` / `EnemyConfig` 同一写法）。
    /// ⚠️ **刻意不强制** `maxEnemies &gt;= baseEnemies` —— "全程固定人数"是个合法的玩法选择，这里只挡"会算崩的数值"。
    /// </summary>
    void OnValidate()
    {
        if (initialEnemies < 0) initialEnemies = 0;
        if (baseEnemies < 0) baseEnemies = 0;
        if (enemiesPerWave < 0) enemiesPerWave = 0;
        if (maxEnemies < 1) maxEnemies = 1;
        if (spawnInterval < 0.01f) spawnInterval = 0.01f;
        if (clearingDelay < 0f) clearingDelay = 0f;
        if (idleTimeout < 1f) idleTimeout = 1f;
    }

    /// <summary>内置默认值（代码兜底，缺资源时使用）</summary>
    public static WaveConfig Default()
    {
        var c = CreateInstance<WaveConfig>();
        c.initialEnemies = 6;
        c.baseEnemies = 4;
        c.enemiesPerWave = 1;
        c.maxEnemies = 14;
        c.spawnInterval = 0.25f;
        c.clearingDelay = 3f;
        c.idleTimeout = 18f;
        return c;
    }
}