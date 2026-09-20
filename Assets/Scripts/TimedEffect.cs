using UnityEngine;

/// <summary>
/// 计时自毁特效：在 lifetime 内做缩放 / 淡出 / 灯光衰减，到时销毁自己。
/// 由 Effects 生成特效时自动挂上，不需要手动添加。
/// </summary>
public class TimedEffect : MonoBehaviour
{
    public float lifetime = 0.2f;
    public float startScale = 1f;
    public float endScale = 0f;
    public bool fadeOut;    // 透明度 1 → 0
    public bool fadeLight;  // 灯光强度 → 0
    public string poolKey;  // 非空 → 结束后回池复用（而不是销毁），由 Effects 池化时设置

    float _t;
    MeshRenderer _renderer;
    MaterialPropertyBlock _mpb;
    Light _light;
    float _lightIntensity;

    void OnEnable()
    {
        // 用 OnEnable 而非 Start：从对象池重新激活时计时必须重置（Start 只执行一次）
        _t = 0f;
        // 必须用 GetComponentInChildren：特效 Prefab 的 Renderer/Light 通常挂在子物体（名为 "Mesh"/"Light"）上，
        // 只查根节点会取不到 → FadeAlpha 第一行就 return，淡出静默失效（Decal 弹孔淡出就是被这个坑掉的）。
        if (_renderer == null) _renderer = GetComponentInChildren<MeshRenderer>();
        if (_light == null) _light = GetComponentInChildren<Light>();
        if (_light != null) _lightIntensity = _light.intensity;
    }

    void OnDisable()
    {
        // 回池前清掉透明度覆盖，避免下次复用残留半透明
        if (!string.IsNullOrEmpty(poolKey) && _renderer != null) Effects.ClearColor(_renderer);
        // 灯光强度也要还原：OnEnable 是把「当前强度」当基准值记下来的，而淡出结束时强度已被压到 0，
        // 不还原的话「池化 + fadeLight」的对象第二次复用会读到基准 0 → 灯光永久不亮。
        if (_light != null) _light.intensity = _lightIntensity;
    }

    void Update()
    {
        _t += Time.deltaTime;
        float k = Mathf.Clamp01(_t / lifetime);

        if (!fadeLight)
            transform.localScale = Vector3.one * Mathf.Lerp(startScale, endScale, k);

        if (fadeOut) FadeAlpha(1f - k);

        if (fadeLight && _light != null)
            _light.intensity = Mathf.Lerp(_lightIntensity, 0f, k);

        if (_t >= lifetime)
        {
            if (!string.IsNullOrEmpty(poolKey)) ObjectPool.Release(poolKey, gameObject);
            else Destroy(gameObject);
        }
    }

    /// <summary>
    /// 按 MaterialPropertyBlock 改透明度。
    /// 不用 renderer.material：那会复制一份材质，特效一多就会持续泄漏材质实例。
    /// </summary>
    void FadeAlpha(float alpha)
    {
        if (_renderer == null || _renderer.sharedMaterial == null) return;
        if (!_renderer.sharedMaterial.HasProperty(Effects.ColorProp)) return;

        if (_mpb == null) _mpb = new MaterialPropertyBlock();
        Color c = _renderer.sharedMaterial.color;
        c.a = alpha;
        Effects.SetColor(_renderer, _mpb, c);
    }
}
