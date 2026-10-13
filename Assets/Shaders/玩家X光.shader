// 玩家 X 光：把"玩家的轮廓"叠加渲染在遮挡物之上（俯视角搜打撤的经典做法）。
//
// 与"挖洞"方案的区别：**遮挡物一个像素都不动**（空间感 100% 保留），玩家靠这个幽灵轮廓被看见。
//
// 逐像素遮挡判定（本 Shader 的核心）：
//   ① 用 `ZTest Always` + 透明队列 → 本网格永远参与绘制，且在不透明物体**之后**渲染，
//      因此能读到"已经画好的遮挡物深度"（`_CameraDepthTexture`）；
//   ② 逐像素比较：「场景在该像素的深度」 vs 「幽灵自己的深度（= 像素到相机的距离）」——
//      场景更近 ⇒ 这个像素**真的被挡住了** ⇒ 画出来；否则 discard（一个像素都不画）。
//      于是"只挡住左半边"时，只有左半边显示幽灵，右半边仍是真实的玩家 —— 两边自动拼成一个人，不会重影。
//
// 参数（由 PlayerXRay.cs 每帧写入；也可在材质上手动试）：
//   _Color     轮廓颜色
//   _Alpha     整体透明度
//   _Density   像素密度（Bayer 网点：0 = 空、0.45 ≈ 点阵质感、1 = 实心）
//   _DepthBias 深度容差（米）：调大更保守、调小更积极（防贴面闪烁）
//   _EdgeGlow  边缘提亮（菲涅尔，让轮廓更立体）
//
// ⚠️ 前置条件：相机必须开启深度纹理（PlayerXRay.Awake 会打开 DepthTextureMode.Depth）。
Shader "Town/PlayerXRay"
{
    Properties
    {
        _Color ("轮廓颜色", Color) = (0.45, 0.85, 1.0, 1.0)
        _Alpha ("整体透明度", Range(0,1)) = 0.55
        _Density ("像素密度（网点）", Range(0,1)) = 0.45
        _DepthBias ("深度容差（米）", Range(0,0.5)) = 0.08
        _EdgeGlow ("边缘提亮", Range(0,2)) = 0.35
    }

    SubShader
    {
        Tags { "Queue"="Transparent+50" "RenderType"="Transparent" "IgnoreProjector"="True" }

        ZTest Always
        ZWrite Off
        // ⚠️ 必须只渲染**正面**：幽灵与玩家本体是同一个网格、同一变换，
        //    正面的深度与玩家本体一致 → 「没被挡」判定才成立；
        //    若用 Cull Off，背面三角形的深度比本体更远，会被误判成"被挡"→ 没遮挡时也整块显示（2026-10-03 实测踩到）。
        Cull Back
        Blend SrcAlpha OneMinusSrcAlpha

        // 自遮挡豁免（2026-10-13）：玩家自己的装备（枪 / 手等，Body 以外）已由 玩家X光掩码.shader
        // 在**可见**像素上写了模板标记 1 → 这些像素跳过幽灵（否则枪盖住身体的地方会浮出点阵，
        // 无墙时也会）。装备被墙挡住时掩码 ZTest LEqual 失败、没盖章 → 墙后的身体照常幽灵，不受影响。
        // 工程内无其他模板用户（视野遮罩已改 RT 方案），Ref 1 专用。
        Stencil
        {
            Ref 1
            Comp NotEqual
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Alpha;
            float _Density;
            float _DepthBias;
            float _EdgeGlow;
            sampler2D _CameraDepthTexture;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 screenPos : TEXCOORD0;   // w = 视图空间深度（线性、单位米）
                float3 worldNormal : TEXCOORD1;
                float3 worldViewDir : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.screenPos = ComputeScreenPos(o.pos);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldViewDir = WorldSpaceViewDir(v.vertex);
                return o;
            }

            // 4x4 Bayer 抖动阈值（按屏幕像素取 → 图案钉在屏幕上、不随相机晃动闪烁）
            // 写成 if 链而不是数组下标：旧编译器对"动态下标访问常量数组"不保证展开
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

            fixed4 frag(v2f i) : SV_Target
            {
                // ① 逐像素遮挡判定
                float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screenPos)));
                float myDepth = i.screenPos.w;                        // 本像素（幽灵表面）到相机的距离
                float blocked = step(sceneDepth, myDepth - _DepthBias);   // 场景更近 → 被挡 → 1
                if (blocked <= 0.0) discard;                          // 没被挡 → 不画

                // ② 网点密度
                float2 sp = i.screenPos.xy / max(i.screenPos.w, 1e-5) * _ScreenParams.xy;
                if (Bayer4(sp) >= _Density) discard;

                // ③ 边缘提亮（菲涅尔）
                float fres = 1.0 - saturate(dot(normalize(i.worldNormal), normalize(i.worldViewDir)));
                fixed3 col = _Color.rgb * (1.0 + _EdgeGlow * fres);

                return fixed4(col, _Alpha);
            }
            ENDCG
        }
    }

    FallBack Off
}
