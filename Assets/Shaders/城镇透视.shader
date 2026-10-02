// 城镇透视：Standard 光照外观 + 屏幕空间圆形抖动裁剪
//
// 用途：俯视角第三人称里"遮挡物让开玩家"的效果 —— 在屏幕上以玩家为圆心的一个圆内，
//       把遮挡物的像素按 4x4 Bayer 抖动丢弃（视觉上是"网点半透明"），圆外完全不变。
//
// 为什么这样做（而不是把材质透明化）：
//   · 抖动丢弃跑在**不透明队列**里 → 不写透明排序、不吃混合带宽、也不引入"先透后不透"的排序错乱；
//   · 圆内只是"像素级挖洞"，物体轮廓仍在，比整块消失自然得多。
//
// 参数分两类：
//   · 逐物体的：_Color / _Glossiness / _Metallic（由 CameraOcclusion 从原材质复制过来，保证圆外颜色一致）；
//   · 全局的（每帧由 CameraOcclusion 用 Shader.SetGlobalXXX 写入，所有物体共用一份）：
//       _SeeCenter  = (圆心屏幕X像素, 圆心屏幕Y像素, 半径像素, 柔化宽度像素)
//       _SeeKeep    = 圆内保留密度（0 = 完全挖空；0.5 = 半透网点；1 = 完全不挖）
//     ⚠️ 把圆心/半径做成全局量是刻意的：几百个物体共享同一份数据，不需要任何逐物体更新。
//
// 已知限制：本 Shader 只影响"颜色通道"，**阴影投射（ShadowCaster）不参与抖动** ——
//           被透视的物体会留下完整投影。要一并处理需要额外写阴影通道（当前不做）。
Shader "Town/SeeThrough"
{
    Properties
    {
        _Color ("颜色", Color) = (1,1,1,1)
        _Glossiness ("平滑度", Range(0,1)) = 0.2
        _Metallic ("金属度", Range(0,1)) = 0.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        CGPROGRAM
        // Standard 光照模型：与项目其余材质外观一致（含阴影、光照探针、环境光）
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        fixed4 _Color;
        half _Glossiness;
        half _Metallic;

        // ---- 全局量（每帧由 CameraOcclusion 写入）----
        float4 _SeeCenter;   // xy = 圆心屏幕像素坐标；z = 半径（像素）；w = 柔化宽度（像素）
        float _SeeKeep;      // 圆内保留密度

        struct Input
        {
            float4 screenPos;   // Surface Shader 自动填充（投影空间，需自行除以 w）
        };

        // 4x4 Bayer 抖动阈值（返回值 0~15/16，按屏幕像素固定 → 不随相机晃动闪烁）
        // 这里写成 if 链而不是数组下标：HLSL 对"动态下标访问常量数组"在旧编译器上不保证展开
        float Bayer4(float2 sp)
        {
            int x = (int)fmod(sp.x, 4.0);
            int y = (int)fmod(sp.y, 4.0);
            float v;
            if (y == 0)      { v = (x == 0) ? 0.0  : (x == 1) ? 8.0  : (x == 2) ? 2.0  : 10.0; }
            else if (y == 1) { v = (x == 0) ? 12.0 : (x == 1) ? 4.0  : (x == 2) ? 14.0 : 6.0;  }
            else if (y == 2) { v = (x == 0) ? 3.0  : (x == 1) ? 11.0 : (x == 2) ? 1.0  : 9.0;  }
            else             { v = (x == 0) ? 15.0 : (x == 1) ? 7.0  : (x == 2) ? 13.0 : 5.0;  }
            return v / 16.0;
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            o.Albedo = _Color.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;

            // 半径或柔化宽度为 0 ⇒ 本 Shader 不做任何裁剪（等价于普通 Standard 材质）
            float r = _SeeCenter.z;
            if (r <= 0.0) return;

            // 屏幕像素坐标（与 Camera.WorldToScreenPoint 同一坐标系：左下原点、y 向上）
            float2 sp = IN.screenPos.xy / max(IN.screenPos.w, 1e-5) * _ScreenParams.xy;

            float d    = distance(sp, _SeeCenter.xy);
            float soft = max(_SeeCenter.w, 0.001);
            // t：圆心 1 → 圆外 0（边缘 soft 像素内平滑过渡）
            float t = saturate((r - d) / soft);

            // 保留密度：圆外 1（全保留）→ 圆内 _SeeKeep
            float keep = lerp(1.0, _SeeKeep, t);

            // 抖动丢弃：阈值大于保留量的像素被丢弃（keep=1 时全部保留）
            if (Bayer4(sp) >= keep) clip(-1);
        }
        ENDCG
    }

    FallBack "Diffuse"
}
