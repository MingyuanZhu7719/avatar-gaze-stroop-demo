Shader "AvatarStroop/ViveControllerModel"
{
    Properties
    {
        _MainTex ("Runtime controller texture", 2D) = "white" {}
        _SurfaceLighting ("Readable controller surface", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Back ZWrite On ZTest LEqual
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _SurfaceLighting;
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f,o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex=UnityObjectToClipPos(v.vertex);
                o.uv=v.uv;
                o.worldNormal=UnityObjectToWorldNormal(v.normal);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                fixed4 surface=tex2D(_MainTex,i.uv);
                // Command-buffer meshes do not receive Unity's scene-light
                // setup. A bounded ambient floor and fixed soft key preserve
                // the native texture while making dark plastic surfaces legible.
                float light=.82+.28*saturate(dot(normalize(i.worldNormal),normalize(float3(.3,.8,-.4))));
                float3 readable=max(surface.rgb,float3(.18,.18,.18))*light;
                return fixed4(lerp(surface.rgb,readable,_SurfaceLighting),1);
            }
            ENDCG
        }
    }
}
