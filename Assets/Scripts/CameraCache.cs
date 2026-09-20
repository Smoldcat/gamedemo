using UnityEngine;

/// <summary>
/// 主相机的静态缓存。
/// 为什么需要：Camera.main 内部是按 "MainCamera" Tag 查找（FindGameObjectWithTag），开销远大于读一个字段。
/// 而它在高频路径上被反复调用——敌人血条朝相机（每个敌人每物理帧一次，14 个敌人 × 50Hz ≈ 700 次/秒）、
/// 伤害飘字（每次伤害）、准星（每帧两次）。缓存后这些调用退化成一次字段读取。
///
/// 场景重载后旧相机引用失效：Unity 对已销毁对象重载了 == 运算符，会判定为 null，
/// 因此这里判空后会自动重新查找（自愈）。WaveManager.BeginRound 也会显式调用 Clear()。
/// </summary>
public static class CameraCache
{
    static Camera _main;

    /// <summary>主相机（缓存）。找不到返回 null，调用方需自行判空。</summary>
    public static Camera Main
    {
        get
        {
            if (_main == null) _main = Camera.main;
            return _main;
        }
    }

    /// <summary>清空缓存（场景重载/开新一局时调用，强制下次访问重新查找）</summary>
    public static void Clear()
    {
        _main = null;
    }
}
