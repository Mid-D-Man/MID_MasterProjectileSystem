


Shader "MidMan/InstancedProjectile_URP"
{
    Properties
    {
        _MainTex ("Sprite Atlas", 2D)                    = "white" {}
        // These now show in the inspector AND serve as fallback
        // for the combined-mesh (non-instanced) path
        _UVRect  ("UV Rect (xy = offset, zw = scale)", Vector) = (0, 0, 1, 1)
        _Color   ("Tint Color", Color)                   = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Transparent"
            "RenderType"      = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType"     = "Plane"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Lighting Off

        Pass
        {
            Name "Unlit"

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            // ── Per-instance data (GPU instancing path) ────────────────────
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _UVRect)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                // Vertex color carries tint in the combined-mesh path
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float4 col        : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                // ── Instanced path: remap UVs via per-instance _UVRect ─────
                // ── Combined path: UVs already baked into the vertex buffer,
                //    so rect = (0,0,1,1) → this line is a no-op ────────────
                float4 rect = UNITY_ACCESS_INSTANCED_PROP(Props, _UVRect);
                output.uv   = input.uv * rect.zw + rect.xy;

                // ── Instanced path: per-instance _Color overrides ──────────
                // ── Combined path: vertex color carries the tint ───────────
                float4 instanceColor = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                output.col = instanceColor * input.color;

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 texCol = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                texCol.rgb  *= input.col.rgb;
                texCol.a    *= input.col.a;
                return texCol;
            }
            ENDHLSL
        }
    }

    Fallback Off
}