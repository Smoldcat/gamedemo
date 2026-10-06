// 视野遮罩（stencil 写入层）：把"可见性多边形"的覆盖范围写进模板缓冲（stencil = 1）。
//
// 它**不出任何颜色**（ColorMask 0）—— 唯一作用是给后面的「视野暗幕」打标记：
//   被标记的像素 = 视野内 → 暗幕不画；没被标记 = 视野外 → 暗幕压暗。
//
// 为什么用模板缓冲而不是"直接画一个半透明形状"：
//   ① 视野多边形是"地面上的形状"，而我们要压暗的是"屏幕上'视野外'的全部像素"（含高处的建筑/树），
//      两者靠 stencil 对齐最干净、零排序问题；
//   ② 暗幕始终是"全屏一层"，不受 3D 深度/透明排序影响。
//
// ⚠️ 队列必须**早于**「视野暗幕」（暗幕是 Transparent+100）；本 shader 用 Transparent。
// ⚠️ ZTest Always：可见性多边形是贴地画的，不能让它被地面/建筑用深度挡掉。
Shader "Town/VisionMask"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }

        ColorMask 0          // 不输出颜色
        ZTest Always
        ZWrite Off
        Cull Off
        Stencil
        {
            Ref 1
            Comp Always
            Pass Replace     // 画到哪，哪里就标 1
        }

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
                return fixed4(0, 0, 0, 0);   // 颜色被 ColorMask 屏蔽，这里返回什么都无所谓
            }
            ENDCG
        }
    }

    FallBack Off
}
