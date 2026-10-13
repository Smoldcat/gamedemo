// 玩家 X 光「自遮挡豁免」掩码：玩家自己的装备（枪 / 手 / 背包…，Body 以外）在正常渲染时
// 往模板缓冲**盖章**，X 光幽灵（Town/PlayerXRay，Stencil Comp NotEqual 1）跳过这些像素
// → 自己的东西不再被当成"遮挡物"（2026-10-13 修：枪盖住身体的地方会浮出幽灵点阵）。
//
// 与 PlayerXRay.cs 配套：LateUpdate 里用本材质把 Body 以外的网格再走一遍 Graphics.DrawMesh。
//
// 关键渲染状态（都别随手改）：
//   ColorMask 0        —— 一个颜色像素都不写（画面完全不变），只往模板写标记；
//   ZTest LEqual       —— 只有该像素**真的可见**（没被墙挡）才盖章；装备被墙挡住时不盖章，
//                         墙后面的身体照常显示幽灵（正确行为）；
//   ZWrite Off         —— 不污染深度（否则会反向破坏 X 光的深度判定）；
//   Queue Geometry+10  —— 排在不透明物体（枪 = 2000）之后、透明幽灵（Transparent+50）之前，
//                         保证"先盖章、后查章"；
//   Stencil Ref 1      —— 标记位。工程内无其他模板用户（视野遮罩已改 RT 方案），1 是干净的。
//
// ⚠️ 前提：相机渲染目标是默认 24 位深度 + 8 位模板（Unity 默认即如此）。
Shader "Hidden/PlayerXRayMask"
{
    SubShader
    {
        Tags { "Queue"="Geometry+10" "RenderType"="Opaque" "IgnoreProjector"="True" }

        ZTest LEqual
        ZWrite Off
        Cull Back
        ColorMask 0

        Stencil
        {
            Ref 1
            Comp Always
            Pass Replace
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            float4 vert(float4 pos : POSITION) : SV_POSITION
            {
                return UnityObjectToClipPos(pos);
            }

            fixed4 frag() : SV_Target { return 0; }   // ColorMask 0 → 写什么都不落到颜色上
            ENDCG
        }
    }
    FallBack Off
}
