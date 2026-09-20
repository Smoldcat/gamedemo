/// <summary>
/// 全局数值常量：集中管理散落在各脚本里的「场景高度」等硬编码数值。
/// 改这里就能统一调整，避免同一个概念在多个文件里各写一份、彼此还不一致。
/// </summary>
public static class GameConst
{
    /// <summary>
    /// 鼠标射线映射到的高度平面：准星指向、朝向计算都用它。
    /// </summary>
    public const float AimPlaneY = 0.5f;

    /// <summary>
    /// 实体所在的高度平面：物体生成位置、子弹飞行高度都用它。
    /// 注意：它比 AimPlaneY 高 0.05，两者并不是同一个平面。
    /// </summary>
    public const float EntityPlaneY = 0.55f;

    /// <summary>
    /// 障碍检测球的中心高度：用于「这个位置能不能放东西」的判断。
    /// 必须高于地面顶面（y=0）才不会把地面本身当成障碍物。
    /// </summary>
    public const float ObstacleProbeY = 0.75f;

    /// <summary>
    /// 玩家的 Tag。集中在这里，避免各脚本写死 "Player" 字符串——
    /// Tag 一旦改名，写死的字符串不会报编译错误，敌人会静默失效（看不见玩家）。
    /// </summary>
    public const string TagPlayer = "Player";
}
