// InstancedProjectile.shader
// Required for ProjectileRenderer2D.cs — DrawMeshInstanced passes _UVRect
// and _Color as per-instance arrays via MaterialPropertyBlock.SetVectorArray.
//
// Place this file anywhere in your Assets folder (e.g. Assets/Shaders/).
// Create a Material using this shader, assign your sprite atlas as _MainTex,
// then drag that Material into ProjectileRenderer2D._atlasMaterial.
//
// Atlas setup:
//   - Pack your projectile sprites with Unity's Sprite Atlas (Window > 2D > Sprite Atlas)
//     or any external packer (TexturePacker etc.).
//   - Set the atlas texture's Filter Mode to "Point" for pixel-art / crisp sprites,
//     or "Bilinear" for smooth sprites.  The shader is agnostic.
//   - ProjectileRegistry.GetUVRect() returns (x, y, width, height) in 0-1 atlas space.
//     That maps directly to the _UVRect float4 consumed here.
//
// URP note:
//   This shader targets the Built-in Render Pipeline.
//   For URP, replace the Pass contents with a HLSLPROGRAM block using
//   "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
//   and CBUFFER_START(UnityPerMaterial) in place of UNITY_INSTANCING_BUFFER_START.
//   The instancing macros (UNITY_SETUP_INSTANCE_ID etc.) remain identical.

Shader "MidMan/InstancedProjectile"
{
    Properties
    {
        _MainTex ("Sprite Atlas", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
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
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            // This single pragma enables GPU instancing AND the
            // UNITY_INSTANCING_BUFFER / UNITY_ACCESS_INSTANCED_PROP macros.
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            sampler2D _MainTex;

            // Per-instance properties — one entry per projectile in the batch.
            // Unity reads array[unity_InstanceID] automatically when instancing is on.
            UNITY_INSTANCING_BUFFER_START(Props)
                // xy = atlas UV offset, zw = atlas UV size (from ProjectileRegistry.GetUVRect)
                UNITY_DEFINE_INSTANCED_PROP(float4, _UVRect)
                // rgba — currently (1,1,1,alpha) where alpha encodes lifetime fade
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float4 col : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            v2f vert(appdata v)
            {
                v2f o;

                // Required before any UNITY_ACCESS_INSTANCED_PROP call.
                UNITY_SETUP_INSTANCE_ID(v);
                // Transfers instance ID to the fragment stage (needed if frag
                // also accesses instanced props — it does for _Color).
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                o.pos = UnityObjectToClipPos(v.vertex);

                // Remap the quad's [0,1] UVs into the sprite's atlas rect.
                // rect.xy = bottom-left corner of the sprite in atlas UV space.
                // rect.zw = width and height of the sprite in atlas UV space.
                float4 rect = UNITY_ACCESS_INSTANCED_PROP(Props, _UVRect);
                o.uv = v.uv * rect.zw + rect.xy;

                o.col = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                fixed4 texCol = tex2D(_MainTex, i.uv);

                // Premultiply the sampled alpha by the per-instance alpha so
                // lifetime fade works correctly with sprite transparency regions.
                texCol.a *= i.col.a;

                // Tint RGB (currently 1,1,1 but ready for team colours / hit flash).
                texCol.rgb *= i.col.rgb;

                return texCol;
            }
            ENDCG
        }
    }

    // Fallback for hardware that doesn't support instancing — renders nothing
    // rather than falling back to a slow non-instanced path that looks wrong.
    Fallback Off
}
