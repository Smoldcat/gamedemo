using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 伤害飘字：从命中点向上飘并淡出。
/// 由 DamageText 创建/复用；自定义 Prefab 里也可以自带本组件（样式全在 Prefab 里调）。
/// 若 Prefab 里不挂本组件，DamageText 会自动补挂。
/// </summary>
public class DamagePopup : MonoBehaviour
{
    [Tooltip("非空时到时回对象池（键名），否则销毁。\n" +
             "⚠️ 走 `DamageText.Spawn` 创建的飘字，这个值**每次都会被代码覆盖**成池键；\n" +
             "只有在你自己手动实例化 `DamagePopup`（不经过 DamageText）时它才生效。")]
    public string poolKey;
    [Tooltip("飘字总时长（秒）")]
    public float duration = 0.7f;
    [Tooltip("向上飘的屏幕像素距离")]
    public float risePx = 55f;

    Text _text;
    RectTransform _rt;
    Canvas _canvas;
    Vector3 _worldPos;
    float _t;

    /// <summary>显示一次飘字。worldPos 是命中点的世界坐标（生成时抬高到头顶由调用方负责）。</summary>
    public void Show(Vector3 worldPos, string value, Color color, int fontSize)
    {
        if (_text == null) _text = GetComponent<Text>();
        if (_rt == null) _rt = GetComponent<RectTransform>();
        if (_canvas == null) _canvas = GetComponentInParent<Canvas>();

        _worldPos = worldPos;
        _t = 0f;
        _text.text = value;
        _text.color = color;
        _text.fontSize = fontSize;
        gameObject.SetActive(true);
    }

    void Update()
    {
        _t += Time.deltaTime;
        float k = Mathf.Clamp01(_t / Mathf.Max(duration, 0.01f));

        // 世界坐标 → 屏幕坐标 → Canvas 局部坐标（Overlay 锚点在屏幕中心，要减半屏再除缩放系数）
        Vector2 sp;
        var cam = CameraCache.Main;
        if (cam != null) sp = cam.WorldToScreenPoint(_worldPos);
        else sp = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        float sf = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
        var pos = new Vector2(sp.x - Screen.width * 0.5f, sp.y - Screen.height * 0.5f) / sf;
        _rt.anchoredPosition = pos + new Vector2(0f, risePx * k);

        var c = _text.color;
        c.a = Mathf.Clamp01(1f - k * k);   // 加速淡出（先慢后快）
        _text.color = c;

        if (k >= 1f)
        {
            if (!string.IsNullOrEmpty(poolKey)) ObjectPool.Release(poolKey, gameObject);
            else Destroy(gameObject);
        }
    }
}
