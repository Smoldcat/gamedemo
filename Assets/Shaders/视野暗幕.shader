// 视野暗幕（软边版）：一个贴在相机前的全屏四边形，"视野外"压暗成半透明黑。
//
// 数据流：
//   ① `视野遮罩.shader` 用**相机 VP 矩阵**把「可见性多边形」画进一张**低分辨率 RT**（默认 1/4 屏）
//      → 可见处 = 白(1)、其余 = 黑(0)；RT 即"屏幕的低分辨率版"，与屏幕 UV 一一对应；
//   ② 本 shader 采样那张 RT（屏幕 UV 直接对应），做 3×3 模糊 → 边缘变成软过渡；
//   ③ 再用 `_Curve` 把曲线往内收（`saturate((1-v) * _Curve)`）→ 盲区依然"够黑"，不会因为模糊而发灰。
//
// ⚠️ 模糊偏移必须 ≤ 3 个 RT 纹素：3×3 是"稀疏采样"，偏移一大，采样点之间就会露出阶梯
//    → 屏幕上是边缘外侧的多条平行条带（实测 7.5 纹素 → 9 条 30px 宽的带）。
//    想要更宽的柔化 → 调大 `rtScale`（低分辨率放大本身就是柔化），而不是加大偏移。
//
// ⚠️ 队列 Transparent+100：必须晚于所有 3D 透明物（特效 3000、X 光幽灵 3050），
//    这样"视野外的特效/幽灵"也一样被压暗；UI 是 Screen Space Overlay，不受影响。
// ⚠️ ZTest Always + ZWrite Off：它是一层"贴在相机前"的屏幕遮罩，不参与深度。
Shader "Town/VisionFog"
{
    Properties
    {
        _Color ("暗幕颜色", Color) = (0, 0, 0, 1)
        _Alpha ("不透明度", Range(0,1)) = 0.72
        _SoftTexels ("边缘柔和度（RT 纹素，由 C# 按屏幕像素换算并限幅）", Range(0, 3)) = 3
        _Curve ("边缘曲线（越大暗区越往内收）", Range(0.5, 8)) = 3.0
    }

    SubShader
    {
        Tags { "Queue"="Transparent+100" "RenderType"="Transparent" "IgnoreProjector"="True" }

        ZTest Always
        ZWrite Off
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Alpha;
            float _SoftTexels;
            float _Curve;
            sampler2D _VisionRT;
            float4 _VisionRTSize;   // x=宽 y=高 z=1/宽 w=1/高（C# 每帧给，不依赖引擎对纹理 _TexelSize 的自动注入）

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 屏幕 UV（SV_POSITION.xy 是像素坐标，左下原点）—— 面片铺满全屏，天然一一对应
                float2 uv = i.pos.xy / _ScreenParams.xy;

                // 3×3 模糊（廉价近似）：偏移量 = _SoftTexels 个 RT 纹素（C# 已限幅到 ≤3）
                float2 o = _SoftTexels * _VisionRTSize.zw;
                float v = 0.0;
                v += tex2D(_VisionRT, uv + float2(-o.x, -o.y)).r;
                v += tex2D(_VisionRT, uv + float2( 0.0, -o.y)).r;
                v += tex2D(_VisionRT, uv + float2( o.x, -o.y)).r;
                v += tex2D(_VisionRT, uv + float2(-o.x,  0.0)).r;
                v += tex2D(_VisionRT, uv).r;
                v += tex2D(_VisionRT, uv + float2( o.x,  0.0)).r;
                v += tex2D(_VisionRT, uv + float2(-o.x,  o.y)).r;
                v += tex2D(_VisionRT, uv + float2( 0.0,  o.y)).r;
                v += tex2D(_VisionRT, uv + float2( o.x,  o.y)).r;
                v *= (1.0 / 9.0);

                // 曲线：让"暗区"从更靠内的位置才开始 → 模糊不会让盲区发灰
                float a = _Alpha * saturate((1.0 - v) * _Curve);
                return fixed4(_Color.rgb, a);
            }
            ENDCG
        }
    }

    FallBack Off
}
