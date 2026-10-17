using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// **全屏闪白**（闪光弹把玩家自己闪到时用）：一个自建的全屏白色遮罩，在 `duration` 内从全白渐隐到透明，然后自毁。
///
/// ⚠️ 为什么不在 `HUD` 里做：HUD 的 Canvas 是**场景预置**的，往它里面加节点要和预置布局对齐；
///    而这是一个**用完即弃**的一次性覆盖层 ⇒ 自己带一个独立 Canvas（`sortingOrder` 很高）最省事，
///    也**不会**影响 HUD / 准星 / 面板的层级。
/// ⚠️ 刻意 `raycastTarget = false`：闪白期间玩家还能正常点 UI（背包/商店），只是看不见 ✗
///    —— 这是"致盲"的本意；如果哪天想连点击都挡掉，把这一行改掉即可。
/// </summary>
public class ScreenFlash : MonoBehaviour
{
    Image _img;
    float _t0;
    float _dur;
    Color _color;

    /// <summary>播一次全屏闪。<paramref name="duration"/> ≤ 0 时用 0.5 秒兜底。</summary>
    public static ScreenFlash Play(Color color, float duration)
    {
        var go = new GameObject("屏幕闪光(运行时)");
        var f = go.AddComponent<ScreenFlash>();
        f.Init(color, duration);
        return f;
    }

    void Init(Color color, float duration)
    {
        _color = color;
        _dur = Mathf.Max(duration, 0.05f);
        _t0 = Time.unscaledTime;

        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 9000;          // 压在所有 UI 之上（调试面板是 260 / 伤害字 300）

        var imgGo = new GameObject("White", typeof(RectTransform));
        imgGo.transform.SetParent(transform, false);
        var rt = (RectTransform)imgGo.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        _img = imgGo.AddComponent<Image>();
        _img.color = color;
        _img.raycastTarget = false;
    }

    void Update()
    {
        // 用 unscaledTime：暂停（timeScale=0）时闪白也照常消失，不会冻在屏幕上
        float t = (Time.unscaledTime - _t0) / _dur;
        if (t >= 1f) { Destroy(gameObject); return; }

        // 前半段保持较实（致盲感），后半段快速淡出 —— 线性会显得"一直在灰"
        float a = t < 0.5f ? Mathf.Lerp(1f, 0.75f, t * 2f) : Mathf.Lerp(0.75f, 0f, (t - 0.5f) * 2f);
        var c = _color;
        c.a *= a;
        _img.color = c;
    }
}
