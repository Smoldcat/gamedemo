using UnityEngine;

/// 敌人头顶血条：世界空间的双色块，始终面向摄像机。
public class HealthBar : MonoBehaviour
{
    const float Width = 0.9f;          // 血条总宽
    const float FillWidth = Width - 0.04f;  // 填充条宽度（两边各留一点黑边）
    const float Height = 0.09f;

    Transform _fill;
    Renderer _fillRenderer;
    MaterialPropertyBlock _mpb;

    /// <summary>创建一条血条并挂到指定父物体上，返回组件实例</summary>
    public static HealthBar Create(Transform parent, float yOffset)
    {
        var root = new GameObject("HealthBar");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = Vector3.up * yOffset;

        // 背景条（黑色半透明）。
        // ⚠️ 用 Effects.CreateMeshObject（共享网格 + **无碰撞体**）：原来用 GameObject.CreatePrimitive
        // 会给**每个敌人**多造一份网格、还要立刻删掉它的碰撞体（不然会和父物体刚体冲突）。
        var bg = Effects.CreateMeshObject(PrimitiveType.Quad, "BG", root.transform,
                                          Effects.SharedAlpha(new Color(0f, 0f, 0f, 0.75f)));   // 半透明黑底（Shared 不透明）
        bg.transform.localScale = new Vector3(Width, 0.12f, 1f);

        // 填充条：颜色随血量变化，用 MaterialPropertyBlock 改，所以材质本身可以共享
        var fill = Effects.CreateMeshObject(PrimitiveType.Quad, "Fill", root.transform,
                                            Effects.Shared(Color.green));
        fill.transform.localPosition = new Vector3(0f, 0f, -0.01f);  // 稍微靠前，避免和背景条 Z 冲突闪烁
        fill.transform.localScale = new Vector3(FillWidth, Height, 1f);

        var bar = root.AddComponent<HealthBar>();
        bar._fill = fill.transform;
        bar._fillRenderer = fill.GetComponent<MeshRenderer>();
        bar._mpb = new MaterialPropertyBlock();
        return bar;
    }

    /// <summary>按血量比例（0~1）刷新长度和颜色</summary>
    public void UpdateBar(float ratio)
    {
        ratio = Mathf.Clamp01(ratio);

        // 缩短填充条，并保持左端对齐（从右往左收缩）
        float w = FillWidth * Mathf.Max(ratio, 0.001f);
        _fill.localScale = new Vector3(w, Height, 1f);
        _fill.localPosition = new Vector3(-FillWidth * 0.5f + w * 0.5f, 0f, -0.01f);

        // 满血绿色 → 残血红渐变色
        Effects.SetColor(_fillRenderer, _mpb,
            Color.Lerp(new Color(0.9f, 0.15f, 0.1f), new Color(0.2f, 0.9f, 0.25f), ratio));

        // 始终正对摄像机
        var cam = CameraCache.Main;
        if (cam != null) transform.rotation = cam.transform.rotation;
    }
}
