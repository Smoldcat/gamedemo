using UnityEngine;

/// <summary>
/// 全局游戏状态。用静态字段是为了让各脚本无需互相持有引用就能读写同一份数据。
/// 注意：静态数据不会随场景重载自动清空，所以每局开始必须调用 Reset()。
/// </summary>
public static class GameState
{
    public static int Kills;          // 累计击杀数
    public static int AliveEnemies;   // 场上存活敌人数
    public static int Wave;           // 当前波次
    public static bool GameOver;      // 玩家是否已死亡
    public static int Gold;           // 金币（商店买卖用；每局由 WaveManager.BeginRound 按「开局资金」写入）

    /// <summary>是否处于暂停（只读：只能通过 SetPaused / ClearPause 修改）。</summary>
    public static bool Paused { get; private set; }

    /// <summary>暂停请求计数：多个来源（背包 / 将来的 ESC 暂停菜单 / 结算页）可以叠加，
    /// 全部释放后才真正恢复时间。</summary>
    static int _pauseCount;

    /// <summary>E 键被拾取逻辑消费的帧号（-1 = 本帧未用）。武器和血包共用，防止同帧按 E 双拾取。</summary>
    public static int PickupEConsumedFrame = -1;

    /// <summary>
    /// 统一暂停入口（替代「直接写 Paused + Time.timeScale」的旧写法）。
    /// pause=true：申请一次暂停；pause=false：释放自己那一次请求。
    /// 返回当前是否暂停。
    /// 为什么用引用计数：旧写法里 BackpackPanel.Close() 无条件 `Time.timeScale = 1f`，
    /// 一旦以后加 ESC 暂停菜单 / 结算页，关闭背包会把别人的暂停一起解掉（时间突然跑起来）。
    /// </summary>
    public static bool SetPaused(bool pause)
    {
        if (pause) _pauseCount++;
        else if (_pauseCount > 0) _pauseCount--;
        ApplyPause(_pauseCount > 0);
        return Paused;
    }

    /// <summary>强制解除所有暂停（死亡重开 / 场景重载时用，避免请求计数残留导致一进游戏就是暂停）。</summary>
    public static void ClearPause()
    {
        _pauseCount = 0;
        ApplyPause(false);
    }

    static void ApplyPause(bool paused)
    {
        Paused = paused;
        Time.timeScale = paused ? 0f : 1f;
    }

    /// <summary>开新一局时调用，把各项数据恢复到初始状态</summary>
    public static void Reset()
    {
        Kills = 0;
        AliveEnemies = 0;
        Wave = 1;
        GameOver = false;
        Gold = 0;          // 每局从 0 开始（开局资金由 WaveManager.BeginRound 在 Reset 之后写入）
        ClearPause();
        PickupEConsumedFrame = -1;
    }
}
