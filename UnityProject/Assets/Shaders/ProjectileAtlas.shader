// Assets/Shaders/ProjectileAtlas.shader
Shader "MidMan/ProjectileAtlas"
{
    Properties
    {
        _MainTex    ("Atlas Texture", 2D) = "white" {}
        // Used in instanced path (per-instance _UVRect overrides this).
        // In combined-mesh path, UVs are baked per-vertex so this is ignored.
        // Useful as a fallback when testing with a single sprite (set to 0,0,1,1).
        _SpriteRect ("Sprite UV Rect  (xy=offset  zw=size)", Vector) = (0,0,1,1)
        _Tint       ("Global Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent"
            "RenderType"      = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "UniversalForward"
            // If using URP 2D Renderer, change to: "Universal2D"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _SpriteRect;   // xy = atlas offset, zw = atlas size
                float4 _Tint;
            CBUFFER_END

            // Per-instance UV rect + tint — set via MaterialPropertyBlock.SetVectorArray
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _UVRect)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;       // vertex color from combined mesh path
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float4 color       : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);

#ifdef UNITY_INSTANCING_ENABLED
                // Instanced: remap unit quad UV [0,1] into atlas rect via per-instance _UVRect
                float4 rect = UNITY_ACCESS_INSTANCED_PROP(Props, _UVRect);
                OUT.uv    = IN.uv * rect.zw + rect.xy;
                OUT.color = UNITY_ACCESS_INSTANCED_PROP(Props, _Color) * _Tint;
#else
                // Combined mesh: atlas UV already baked per-vertex.
                // _SpriteRect is NOT applied here — UVs come from the mesh.
                // _SpriteRect is only for manual single-sprite testing in instanced mode.
                OUT.uv    = IN.uv;
                OUT.color = IN.color * _Tint;
#endif
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * IN.color;
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/InternalErrorShader"
}