// 视野暗幕：一个贴在相机前的全屏四边形，只在"**没有**被视野遮罩标记（stencil != 1）"的像素上
// 盖一层半透明黑 —— 效果就是"视野内正常、视野外压暗"。
//
// 为什么是"半透明黑"而不是全黑：还能看见地形/建筑的轮廓（用户 2026-10-04 选定），
// 压暗程度由 `_Alpha` 控制（0 = 无效果；1 = 全黑）。
//
// ⚠️ 队列 Transparent+100：必须**晚于**所有 3D 透明物（特效 3000、X 光幽灵 3050），
//    这样"视野外的特效/幽灵"也一样被压暗（一致）；但 UI 是 Screen Space Overlay，永远在最上面，不受影响。
// ⚠️ ZTest Always + ZWrite Off：它是一层"贴在相机前"的屏幕遮罩，不参与深度。
// ⚠️ 不写 stencil（Comp NotEqual 只读不写）—— 免得影响同一帧里其它用 stencil 的东西。
Shader "Town/VisionFog"
{
    Properties
    {
        _Color ("暗幕颜色", Color) = (0, 0, 0, 1)
        _Alpha ("不透明度", Range(0,1)) = 0.72
    }

    SubShader
    {
        Tags { "Queue"="Transparent+100" "RenderType"="Transparent" "IgnoreProjector"="True" }

        ZTest Always
        ZWrite Off
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha
        Stencil
        {
            Ref 1
            Comp NotEqual    // 只在"没被视野遮罩标记"的像素上画
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Alpha;

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
                return fixed4(_Color.rgb, _Alpha);
            }
            ENDCG
        }
    }

    FallBack Off
}
