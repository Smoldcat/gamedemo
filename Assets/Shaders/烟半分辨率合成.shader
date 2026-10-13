// 烟半分辨率合成（**可丢弃实验件**，2026-10-13 建）
//
// 用途：把「半分辨率烟 RT」贴回主画面（挂在主相机 OnRenderImage 里的 Graphics.Blit 用）。
//
// ⚠️ 为什么是 `Blend One OneMinusSrcAlpha`（而不是常规的 SrcAlpha）：
//    RT 里存的**已经是预乘 alpha 的颜色** —— 烟粒子在"透明黑底"上按 SrcAlpha/OneMinusSrcAlpha 混合，
//    累积出来的 rgb 天然就是 `颜色 × 覆盖度`。所以合成时：
//      · 源因子必须用 One（rgb 已经是预乘的，再乘一次 alpha 会淡成 a²）
//      · 目标因子用 OneMinusSrcAlpha（RT 的 alpha 当覆盖度用）
//
// ⚠️ 已知近似：RT 的 alpha 是"逐次 alpha 混合"累出来的，与真实覆盖度略有偏差（重叠处偏薄一点点）。
//    本实验为此**接受**这个误差；产品化若在意，可在烟相机里改用 ColorMask 只管 RGB 的材质。
Shader "Hidden/SmokeHalfResComposite"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            // ⚠️ 用独立属性 `_SmokeTex`（由 C# 端 `SetTexture` 传入），**不能叫 `_MainTex`**：
            //    `Graphics.Blit(src, dst, mat)` 会把 `_MainTex` 自动设成**场景源图**，
            //    再叠加一次就等于"把画面和自己加了一遍"（实测：整屏泛灰、HUD 被洗白）。
            sampler2D _SmokeTex;

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 c = tex2D(_SmokeTex, i.uv);
                // ⚠️ **覆盖度必须从颜色反推**：`Particles/Standard Unlit` 的透明模式**不写 alpha 通道**
                //    （RT 的 a 一直是清屏值 0）→ 直接用它当覆盖度会退化成"纯加色"（实测：烟白到发光、
                //    敌人血条被完全吃掉）。烟团是**近白色**（贴图白 × 材质近白）→ 取 RGB 最大值 ≈ 覆盖度。
                //    于是结果 = 预乘色 + 场景×(1−覆盖度) = 与 A 模式的常规 alpha 混合**等价**。
                c.a = max(c.r, max(c.g, c.b));
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
