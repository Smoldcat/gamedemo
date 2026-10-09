// 视野遮罩（写 RT 用）：把"可见性多边形"覆盖的范围**写成白色**。
//
// 数据流（2026-10-09 起）：
//   PlayerVision 每帧把「可见性多边形」用一个"俯视正交矩阵"画进一张**低分辨率 RT**
//   （之前它写的是模板缓冲 stencil，已改为 RT 方案 —— 见「视野暗幕」的说明）。
//   本 shader 只负责"多边形区域 = 白色(1)、其余 = 黑(0)"，黑底由 GL.Clear 负责。
//
// ⚠️ ZTest Always：多边形是"贴地画的"（y=0.05），不能被地面/建筑用深度挡掉。
// ⚠️ 不需要 Blend：直接覆盖成白色（同一像素被多个三角形覆盖也无所谓）。
Shader "Town/VisionMask"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }

        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

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
                return fixed4(1, 1, 1, 1);   // 可见 = 白
            }
            ENDCG
        }
    }

    FallBack Off
}
