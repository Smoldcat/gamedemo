using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 极简对象池：按字符串键分类复用 GameObject，减少高频物体的即时创建/销毁（子弹、特效等）。
/// 用法：
///   var go = ObjectPool.Get("Bullet");          // 池里没有就返回 null，调用方负责创建
///   ObjectPool.Release("Bullet", go);            // 归还（自动 SetActive(false)）
/// 池只在运行时存在，场景重载时池里的旧物体引用已失效，调用方按 null 处理即可。
/// </summary>
public static class ObjectPool
{
    static readonly Dictionary<string, Stack<GameObject>> _pools = new Dictionary<string, Stack<GameObject>>();

    /// <summary>从池里取一个物体；没有则返回 null（调用方创建新物体）</summary>
    public static GameObject Get(string key)
    {
        if (_pools.TryGetValue(key, out var st) && st.Count > 0)
        {
            var go = st.Pop();
            if (go != null)
            {
                go.SetActive(true);
                return go;
            }
        }
        return null;
    }

    /// <summary>归还物体（自动 SetActive(false)，等待复用）</summary>
    public static void Release(string key, GameObject go)
    {
        if (go == null) return;
        // 已失活 = 已经归还过（归还时第一件事就是 SetActive(false)）→ 忽略这次重复归还。
        // 没有这道门：同一对象会在池里留下两个引用，之后两次 Get 拿到同一个实例
        //（两个特效/两颗子弹共用同一 GameObject，症状是「特效提前消失 / 两个特效叠在同一处」）。
        // 用 activeSelf 判断是 O(1) 且零维护；不要改成 Stack.Contains（那是每帧 O(n)）。
        if (!go.activeSelf) return;
        go.SetActive(false);

        if (!_pools.TryGetValue(key, out var st))
        {
            st = new Stack<GameObject>();
            _pools[key] = st;
        }
        st.Push(go);
    }

    /// <summary>清空所有池。场景重载后池里缓存的旧物体引用已失效，必须每局开始清一次，
    /// 否则 Get 会反复弹出「已销毁引用」并返回 null，对象池退化成每次新建。</summary>
    public static void Clear()
    {
        _pools.Clear();
    }
}