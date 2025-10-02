//=========================================================================================================================
// VertexLitGeneric - S&box Implementation
// A comprehensive recreation of Source Engine's VertexLitGeneric shader
// Supports: PBR materials, detail textures, transparency, rim lighting, self-illumination, environmental reflections
//=========================================================================================================================

FEATURES
{
    // Core features matching original VertexLitGeneric functionality
    Feature( F_DETAIL_TEXTURE, 0..1, "Detail Mapping" );
    Feature( F_SELF_ILLUMINATION, 0..1, "Self Illumination" );
    Feature( F_RIM_LIGHTING, 0..1, "Rim Lighting" );
    Feature( F_TRANSPARENCY, 0..1, "Transparency" );
    Feature( F_ALPHA_TEST, 0..1, "Alpha Test" );
    Feature( F_ENVIRONMENTAL_REFLECTIONS, 0..1, "Environmental Reflections" );
    //Feature( F_PHONG_EXPONENT_TEXTURE, 0..1, "Phong Exponent Texture" );
    //Feature( F_LIGHT_WARP_TEXTURE, 0..1, "Light Warp Texture" );
    Feature( F_PHONG_WARP_TEXTURE, 0..1, "Phong Warp Texture" );
    Feature( F_VERTEX_COLORS, 0..1, "Vertex Colors" );
    Feature( F_BACKFACE_CULLING, 0..1, "Backface Culling" );
    
    // Feature rules to manage combinations
    FeatureRule( Requires1( F_ALPHA_TEST, F_TRANSPARENCY ), "Alpha test requires transparency to be enabled!" );
    
    #include "common/features.hlsl"
}

MODES
{
    Forward();
    Depth();
}

COMMON
{
    #include "common/shared.hlsl"
}

// Enhanced VertexInput to support vertex colors (matching VertexLitGeneric)
struct VertexInput
{
    #include "common/vertexinput.hlsl"
    
    #if ( F_VERTEX_COLORS )
        float4 vColor : COLOR0 < Semantic( Color ); >;
    #endif
};

struct PixelInput
{
    #include "common/pixelinput.hlsl"
};

//=========================================================================================================================
// VERTEX SHADER
//=========================================================================================================================
VS
{
    #include "common/vertex.hlsl"
    
    PixelInput MainVs( VertexInput i )
    {
        PixelInput o = ProcessVertex( i );
        
        #if ( F_VERTEX_COLORS )
            // Store vertex colors for fragment shader
            o.vVertexColor.rgb = i.vColor.rgb;
            o.vVertexColor.a = i.vColor.a;
        #endif
        
        return FinalizeVertex( o );
    }
}

//=========================================================================================================================
// PIXEL SHADER  
//=========================================================================================================================
PS
{
    #define CUSTOM_MATERIAL_INPUTS
    #include "common/pixel.hlsl"
    // Light interfaces for iterating dynamic/static lights
    #include "common/classes/Light.hlsl"
    
    // Static combos for feature variants
    StaticCombo( S_DETAIL_TEXTURE, F_DETAIL_TEXTURE, Sys( ALL ) );
    StaticCombo( S_SELF_ILLUMINATION, F_SELF_ILLUMINATION, Sys( ALL ) );
    StaticCombo( S_RIM_LIGHTING, F_RIM_LIGHTING, Sys( ALL ) );
    StaticCombo( S_TRANSPARENCY, F_TRANSPARENCY, Sys( ALL ) );
    StaticCombo( S_ALPHA_TEST, F_ALPHA_TEST, Sys( ALL ) );
    StaticCombo( S_ENVIRONMENTAL_REFLECTIONS, F_ENVIRONMENTAL_REFLECTIONS, Sys( ALL ) );

    StaticCombo( S_PHONG_WARP_TEXTURE, F_PHONG_WARP_TEXTURE, Sys( ALL ) );
    StaticCombo( S_VERTEX_COLORS, F_VERTEX_COLORS, Sys( ALL ) );
    StaticCombo( S_BACKFACE_CULLING, F_BACKFACE_CULLING, Sys( ALL ) );
    
    //=================================================================================================================
    // RENDER STATES
    //=================================================================================================================
    
    #if ( S_TRANSPARENCY )
        BoolAttribute( translucent, true );
        RenderState( BlendEnable, true );
        RenderState( AlphaToCoverageEnable, F_ALPHA_TEST );
    #endif
    
    #if ( !S_BACKFACE_CULLING )
        RenderState( CullMode, NONE );
    #endif
    
    //=================================================================================================================
    // TEXTURE INPUTS - Core VertexLitGeneric materials
    //=================================================================================================================
    
    // Base texture ($basetexture equivalent)
    CreateInputTexture2D( BaseTexture, Srgb, 8, "", "_color", "Material,10/10", Default3( 1.0, 1.0, 1.0 ) );
    CreateInputTexture2D( BaseTextureMask, Linear, 8, "", "_mask", "Material,10/20", Default( 1 ) );
    
    // Normal map ($bumpmap equivalent)  
    CreateInputTexture2D( NormalMap, Linear, 8, "NormalizeNormals", "_normal", "Material,10/30", Default3( 0.5, 0.5, 1.0 ) );
    
    // PBR material properties
    CreateInputTexture2D( Roughness, Linear, 8, "", "_rough", "Material,10/40", Default( 1 ) );
    CreateInputTexture2D( Metalness, Linear, 8, "", "_metal", "Material,10/50", Default( 0 ) );
    CreateInputTexture2D( AmbientOcclusion, Linear, 8, "", "_ao", "Material,10/60", Default( 1 ) );
    
    // Detail texture ($detail equivalent)
    #if ( S_DETAIL_TEXTURE )
        CreateInputTexture2D( DetailTexture, Linear, 8, "", "_detail", "Detail,20/10", Default3( 0.5, 0.5, 0.5 ) );
        CreateInputTexture2D( DetailMask, Linear, 8, "", "_detailmask", "Detail,20/20", Default( 1 ) );
    #endif
    
    // Self-illumination ($selfillum equivalent)
    #if ( S_SELF_ILLUMINATION )
        CreateInputTexture2D( EmissionMap, Srgb, 8, "", "_illum", "Emission,30/10", Default3( 0, 0, 0 ) );
    #endif
    
    // Transparency
    #if ( S_TRANSPARENCY )
        CreateInputTexture2D( OpacityMap, Linear, 8, "", "_alpha", "Transparency,40/10", Default( 1 ) );
    #endif
    
    // Environmental reflections ($envmap equivalent)
    #if ( S_ENVIRONMENTAL_REFLECTIONS )
        CreateInputTextureCube( EnvironmentMap, Linear, 8, "", "_env", "Reflections,50/10", Default3( 0, 0, 0 ) );
    #endif
    
    // Phong exponent texture ($phongexponenttexture equivalent)
    // ALWAYS AVAILABLE - No combo dependency for runtime material creation
    CreateInputTexture2D( PhongExponentTexture, Linear, 8, "", "_phongexp", "Phong,55/10", Default( 20 ) );
    
    // Light warp texture ($lightwarptexture equivalent) 
    // ALWAYS AVAILABLE - No combo dependency for runtime material creation
    CreateInputTexture2D( LightWarpTexture, Linear, 8, "", "_lightwarp", "Lighting,65/10", Default( 0.5 ) );
    
    // Phong warp texture ($phongwarptexture equivalent)
    #if ( S_PHONG_WARP_TEXTURE )
        CreateInputTexture2D( PhongWarpTexture, Linear, 8, "", "_phongwarp", "Phong,55/70", Default3( 1.0, 1.0, 1.0 ) );
    #endif
    
    //=================================================================================================================
    // TEXTURE2D DECLARATIONS WITH PACKING
    //=================================================================================================================
    
    // Main material textures - pack base texture + mask
    Texture2D g_tBaseTexture < Channel( RGB, Box( BaseTexture ), Srgb ); Channel( A, Box( BaseTextureMask ), Linear ); OutputFormat( BC7 ); SrgbRead( true ); >;
    
    // Normal map
    // Always keep normal alpha from normal texture for spec mask; Opacity is sampled separately
    Texture2D g_tNormalMap < Channel( RGBA, Box( NormalMap ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;
    
    // PBR properties packed (Roughness/Metalness/AO)
    Texture2D g_tRMA < Channel( R, Box( Roughness ), Linear ); Channel( G, Box( Metalness ), Linear ); Channel( B, Box( AmbientOcclusion ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
    
    // Detail texture
    #if ( S_DETAIL_TEXTURE )
        Texture2D g_tDetailTexture < Channel( RGB, Box( DetailTexture ), Linear ); Channel( A, Box( DetailMask ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
    #endif
    
    // Self-illumination
    #if ( S_SELF_ILLUMINATION )
        Texture2D g_tEmissionMap < Channel( RGB, Box( EmissionMap ), Srgb ); OutputFormat( BC7 ); SrgbRead( true ); >;
    #endif
    
    // Environmental reflections
    #if ( S_ENVIRONMENTAL_REFLECTIONS )
        TextureCube g_tEnvironmentMap < Channel( RGB, Box( EnvironmentMap ), Linear ); OutputFormat( BC6H ); SrgbRead( false ); >;
    #endif
    
    // Phong exponent texture - ALWAYS AVAILABLE
    Texture2D g_tPhongExponentTexture < Channel( R, Box( PhongExponentTexture ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
    
    // Light warp texture - ALWAYS AVAILABLE
    Texture2D g_tLightWarpTexture < Channel( R, Box( LightWarpTexture ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
    
    // Phong warp texture
    #if ( S_PHONG_WARP_TEXTURE )
        Texture2D g_tPhongWarpTexture < Channel( RGB, Box( PhongWarpTexture ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
    #endif
    
    //=================================================================================================================
    // MATERIAL PROPERTIES - VertexLitGeneric style controls
    //=================================================================================================================
    
    // Color tinting ($color2 equivalent) - manual numeric input like [0.85 0.85 0.85]
    float3 g_vColorTint < UiType( Slider ); Range3( 0, 0, 0, 2, 2, 2 ); Default3( 1, 1, 1 ); UiGroup( "Material,10/70" ); >;
    
    // Blend tint by base alpha ($blendTintByBaseAlpha equivalent)
    bool g_bBlendTintByBaseAlpha < UiType( CheckBox ); Default( 0 ); UiGroup( "Material,10/71" ); >;
    
    // Texture transform controls ($basetexturetransform equivalent)
    float2 g_vTextureScale < UiType( Slider ); Range2( 0.1, 0.1, 10, 10 ); Default2( 1, 1 ); UiGroup( "Transform,15/10" ); >;
    float2 g_vTextureOffset < UiType( Slider ); Range2( -2, -2, 2, 2 ); Default2( 0, 0 ); UiGroup( "Transform,15/20" ); >;
    float g_flTextureRotation < UiType( Slider ); Range( -180, 180 ); Default( 0 ); UiGroup( "Transform,15/30" ); >;
    
    // Normal map intensity
    float g_flNormalIntensity < UiType( Slider ); Range( 0, 3 ); Default( 1 ); UiGroup( "Material,10/80" ); >;
    
    // PBR value overrides (when no texture is provided)
    float g_flMetalnessValue < UiType( Slider ); Range( 0, 1 ); Default( 0 ); UiGroup( "Material,10/85" ); >;
    float g_flRoughnessValue < UiType( Slider ); Range( 0, 1 ); Default( 1 ); UiGroup( "Material,10/86" ); >;
    bool g_bUseMetalnessValue < UiType( CheckBox ); Default( 0 ); UiGroup( "Material,10/87" ); >;
    bool g_bUseRoughnessValue < UiType( CheckBox ); Default( 0 ); UiGroup( "Material,10/88" ); >;
    
    // Detail texture controls
    #if ( S_DETAIL_TEXTURE )
        float2 g_vDetailScale < UiType( Slider ); Range2( 1, 1, 32, 32 ); Default2( 8, 8 ); UiGroup( "Detail,20/30" ); >;
        float g_flDetailStrength < UiType( Slider ); Range( 0, 2 ); Default( 1 ); UiGroup( "Detail,20/40" ); >;
    #endif
    
    // Self-illumination controls
    #if ( S_SELF_ILLUMINATION )
        float g_flEmissionStrength < UiType( Slider ); Range( 0, 8 ); Default( 1 ); UiGroup( "Emission,30/20" ); >;
    #endif
    
    // Rim lighting controls ($rimlight equivalent)
    #if ( S_RIM_LIGHTING )
        float3 g_vRimLightColor < UiType( Color ); Default3( 1, 1, 1 ); UiGroup( "Rim Lighting,60/10" ); >;
        float g_flRimLightStrength < UiType( Slider ); Range( 0, 5 ); Default( 1 ); UiGroup( "Rim Lighting,60/20" ); >;
        float g_flRimLightExponent < UiType( Slider ); Range( 0.1, 8 ); Default( 2 ); UiGroup( "Rim Lighting,60/30" ); >;
    #endif
    
    // Environmental reflection controls
    #if ( S_ENVIRONMENTAL_REFLECTIONS )
        float g_flReflectionStrength < UiType( Slider ); Range( 0, 2 ); Default( 1 ); UiGroup( "Reflections,50/20" ); >;
        
        // Environment map tinting and fresnel ($envmaptint, $envmapfresnel equivalent)
        float3 g_vEnvmapTint < UiType( Slider ); Range3( 0, 0, 0, 2, 2, 2 ); Default3( 1, 1, 1 ); UiGroup( "Reflections,50/30" ); >;
        float3 g_vEnvmapFresnel < UiType( Slider ); Range3( 0, 0, 0, 2, 2, 2 ); Default3( 0, 0.5, 1 ); UiGroup( "Reflections,50/40" ); >;
    #endif
    
    // Phong exponent controls ($phongexponent, $phongboost, $phongfresnelranges equivalent)
    // ALWAYS AVAILABLE - No combo dependency for runtime material creation
    float g_flPhongExponent < UiType( Slider ); Range( 1, 150 ); Default( 20 ); UiGroup( "Phong,55/20" ); >;
    // Use base texture alpha as the Phong/specular mask ($basemapalphaphongmask)
    bool g_bBaseMapAlphaPhongMask < UiType( CheckBox ); Default( 0 ); UiGroup( "Phong,55/25" ); >;
    // Legacy phong enable flag (from $phong)
    bool g_bLegacyPhongEnabled < UiType( CheckBox ); Default( 0 ); UiGroup( "Phong,55/29" ); >;
    float g_flPhongBoost < UiType( Slider ); Range( 0, 10 ); Default( 1 ); UiGroup( "Phong,55/30" ); >;
    float3 g_vPhongFresnelRanges < UiType( Slider ); Range3( 0, 0, 0, 20, 20, 20 ); Default3( 0, 0.5, 1 ); UiGroup( "Phong,55/40" ); >;
    bool g_bPhongOverrideRoughness < UiType( CheckBox ); Default( 1 ); UiGroup( "Phong,55/50" ); >;
    bool g_bPhongDisableHalfLambert < UiType( CheckBox ); Default( 0 ); UiGroup( "Phong,55/60" ); >;
    
    // Additional phong parameters ($phongalbedotint, $phongtint equivalent)
    bool g_bPhongAlbedoTint < UiType( CheckBox ); Default( 0 ); UiGroup( "Phong,55/65" ); >;
    float3 g_vPhongTint < UiType( Slider ); Range3( 0, 0, 0, 2, 2, 2 ); Default3( 1, 1, 1 ); UiGroup( "Phong,55/66" ); >;
    
    // Global phong exponent override ($phongexponent without texture)
    float g_flGlobalPhongExponent < UiType( Slider ); Range( 1, 150 ); Default( 0 ); UiGroup( "Phong,55/67" ); >;
    
    // Vertex color modulation strength
    #if ( S_VERTEX_COLORS )
        float g_flVertexColorStrength < UiType( Slider ); Range( 0, 2 ); Default( 1 ); UiGroup( "Vertex Colors,70/10" ); >;
    #endif
    
    //=================================================================================================================
    // HELPER FUNCTIONS
    //=================================================================================================================
    
    // Calculate rim lighting effect
    #if ( S_RIM_LIGHTING )
    float3 CalculateRimLighting( float3 vNormalWs, float3 vViewDirWs, float3 vRimColor, float flRimStrength, float flRimExponent )
    {
        float flRimDot = 1.0 - saturate( dot( vNormalWs, vViewDirWs ) );
        float flRimPower = pow( flRimDot, flRimExponent );
        return vRimColor * flRimPower * flRimStrength;
    }
    #endif
    
    // Convert phong exponent to roughness (PBR equivalent) - ALWAYS AVAILABLE
    float PhongExponentToRoughness( float flPhongExponent )
    {
        // Convert phong exponent (1-150) to roughness (0-1)
        // Higher phong exponent = sharper highlights = lower roughness
        return saturate( sqrt( 2.0 / ( flPhongExponent + 2.0 ) ) );
    }
    
    float CalculatePhongFresnel( float3 vNormalWs, float3 vViewDirWs, float3 vFresnelRanges )
    {
        float flNdotV = saturate( dot( vNormalWs, vViewDirWs ) );
        return smoothstep( vFresnelRanges.x, vFresnelRanges.y, 1.0 - flNdotV ) * vFresnelRanges.z;
    }
    
    // Light warp function ($lightwarptexture equivalent) - ALWAYS AVAILABLE
    float WarpLighting( float flNdotL )
    {
        // Remap lighting value through light warp texture (classic Source engine behavior)
        // Input: flNdotL (normal dot light, -1 to 1)
        // Output: Remapped lighting value based on texture
        float flLightWarpUV = flNdotL * 0.5 + 0.5; // Convert from [-1,1] to [0,1]
        return g_tLightWarpTexture.Sample( g_sAniso, float2( flLightWarpUV, 0.5 ) ).r;
    }
    
    // Phong warp function ($phongwarptexture equivalent) 
    #if ( S_PHONG_WARP_TEXTURE )
    float3 SamplePhongWarp( float3 vNormalWs, float3 vViewDirWs, float3 vLightDirWs, float3 vFresnelRanges )
    {
        // Calculate phong highlight intensity
        float3 vReflectionDir = reflect( -vLightDirWs, vNormalWs );
        float flSpecularTerm = saturate( dot( vViewDirWs, vReflectionDir ) );
        
        // Calculate distance to center of highlight (0 = center, 1 = edge)
        float flDistanceToCenter = 1.0 - flSpecularTerm;
        
        // Calculate fresnel component using phong fresnel ranges
        float flNdotV = saturate( dot( vNormalWs, vViewDirWs ) );
        float flFresnelComponent = 1.0 - smoothstep( vFresnelRanges.x, vFresnelRanges.y, 1.0 - flNdotV );
        
        // Sample phong warp texture with computed coordinates
        // x: 1 - distance to center, y: 1 - fresnel component
        float2 vPhongWarpUV = float2( 1.0 - flDistanceToCenter, 1.0 - flFresnelComponent );
        return g_tPhongWarpTexture.Sample( g_sAniso, vPhongWarpUV ).rgb;
    }
    #endif
    
    // Apply texture transform (rotation, scale, offset)
    float2 TransformUV( float2 vBaseUV, float2 vScale, float2 vOffset, float flRotation )
    {
        // Apply scale and offset
        float2 vUV = vBaseUV * vScale + vOffset;
        
        // Apply rotation if needed
        if ( abs( flRotation ) > 0.001 )
        {
            float flRadians = radians( flRotation );
            float flCos = cos( flRadians );
            float flSin = sin( flRadians );
            
            // Rotate around center (0.5, 0.5)
            vUV -= 0.5;
            float2 vRotatedUV = float2(
                vUV.x * flCos - vUV.y * flSin,
                vUV.x * flSin + vUV.y * flCos
            );
            vUV = vRotatedUV + 0.5;
        }
        
        return vUV;
    }
    
    //=================================================================================================================
    // MAIN PIXEL SHADER
    //=================================================================================================================
    
    float4 MainPs( PixelInput i ) : SV_Target0
    {
        // Calculate transformed UVs
        float2 vBaseUV = TransformUV( i.vTextureCoords.xy, g_vTextureScale, g_vTextureOffset, g_flTextureRotation );
        
        //=============================================================================================================
        // SAMPLE TEXTURES
        //=============================================================================================================
        
        // Sample base texture
        float4 vBaseTexture = g_tBaseTexture.Sample( g_sAniso, vBaseUV );
        
        // Sample normal map (preserve alpha for spec mask); opacity sampled separately
        float4 vNormalSample = g_tNormalMap.Sample( g_sAniso, vBaseUV );
        float3 vNormalTs = DecodeNormal( vNormalSample.rgb );
        // Source 1 convention: invert green (Y) channel
        vNormalTs.g = -vNormalTs.g;
        float flNormalAlpha = vNormalSample.a;
        
        // Opacity from dedicated texture if enabled
        #if ( S_TRANSPARENCY )
            Texture2D g_tOpacityMap < Channel( R, Box( OpacityMap ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
            float flOpacity = g_tOpacityMap.Sample( g_sAniso, vBaseUV ).r;
        #else
            float flOpacity = 1.0;
        #endif
        
        // Apply normal intensity
        vNormalTs.rg *= g_flNormalIntensity;
        
        // Sample PBR properties
        float3 vRMA = g_tRMA.Sample( g_sAniso, vBaseUV ).rgb;
        
        // Sample phong exponent texture - ALWAYS AVAILABLE
        float4 vPhongExponentSample = g_tPhongExponentTexture.Sample( g_sAniso, vBaseUV );
        float flPhongExpTex = vPhongExponentSample.r;       // R: exponent map (0..1)
        float flPhongAlbedoMask = vPhongExponentSample.g;   // G: albedo tint mask
        
        // Source1 mapping: if constant override present use it, else map texture R -> [1..150]
        float flSpecExpTex = 1.0 + 149.0 * flPhongExpTex;
        float flCombinedPhongExponent = ( g_flGlobalPhongExponent > 0.0 ) ? g_flGlobalPhongExponent : flSpecExpTex;
        
        // Sample detail texture
        #if ( S_DETAIL_TEXTURE )
            float2 vDetailUV = vBaseUV * g_vDetailScale;
            float4 vDetailTexture = g_tDetailTexture.Sample( g_sAniso, vDetailUV );
            float3 vDetailColor = vDetailTexture.rgb;
            float flDetailMask = vDetailTexture.a;
        #endif
        
        //=============================================================================================================
        // INITIALIZE MATERIAL
        //=============================================================================================================
        
        Material mat = Material::Init();
        
        // Set up albedo with color tinting (equivalent to $color2)
        // Apply $blendTintByBaseAlpha logic
        if ( g_bBlendTintByBaseAlpha )
        {
            // Blend tint based on base texture alpha (classic VertexLitGeneric behavior)
            mat.Albedo = lerp( vBaseTexture.rgb, vBaseTexture.rgb * g_vColorTint, vBaseTexture.a );
        }
        else
        {
            // Simple color multiplication
            mat.Albedo = vBaseTexture.rgb * g_vColorTint;
        }
        
        // Apply vertex color modulation if enabled
        #if ( S_VERTEX_COLORS )
            mat.Albedo = lerp( mat.Albedo, mat.Albedo * i.vVertexColor.rgb, g_flVertexColorStrength );
        #endif
        
        // Apply detail texture blending
        #if ( S_DETAIL_TEXTURE )
            // Use overlay blending for detail texture
            float3 vDetailBlended = lerp( mat.Albedo, 
                mat.Albedo < 0.5 ? 2.0 * mat.Albedo * vDetailColor : 1.0 - 2.0 * (1.0 - mat.Albedo) * (1.0 - vDetailColor),
                flDetailMask * g_flDetailStrength );
            mat.Albedo = vDetailBlended;
        #endif
        
        // Transform normal to world space
        mat.Normal = TransformNormal( vNormalTs, i.vNormalWs, i.vTangentUWs, i.vTangentVWs );
        
        // Set PBR properties with value overrides
        mat.Roughness = g_bUseRoughnessValue ? g_flRoughnessValue : vRMA.r;
        mat.Metalness = g_bUseMetalnessValue ? g_flMetalnessValue : vRMA.g;
        mat.AmbientOcclusion = vRMA.b;
        mat.TintMask = vBaseTexture.a;
        
        // Apply phong exponent texture to roughness - ALWAYS AVAILABLE (controlled by g_flPhongBoost)
        if ( g_flPhongBoost > 0.0 )
        {
            float flPhongRoughness = PhongExponentToRoughness( flCombinedPhongExponent );
            // Option to either override roughness completely or blend with existing roughness
            if ( g_bPhongOverrideRoughness )
            {
                mat.Roughness = flPhongRoughness;
            }
            else
            {
                mat.Roughness = lerp( mat.Roughness, flPhongRoughness, 0.5 ); // Blend 50/50
            }
        }
        
        // Set opacity
        #if ( S_TRANSPARENCY )
            mat.Opacity = flOpacity;
        #endif
        
        //=============================================================================================================
        // APPLY LIGHTING EFFECTS
        //=============================================================================================================
        
        // Self-illumination ($selfillum equivalent)
        #if ( S_SELF_ILLUMINATION )
            mat.Emission += g_tEmissionMap.Sample( g_sAniso, vBaseUV ).rgb * g_flEmissionStrength;
        #endif
        
        // Calculate world-space view direction for rim lighting and reflections
        float3 vPositionWs = i.vPositionWithOffsetWs + g_vCameraPositionWs;
        float3 vViewDirWs = normalize( g_vCameraPositionWs - vPositionWs );
        
        // Rim lighting effect ($rimlight equivalent)
        #if ( S_RIM_LIGHTING )
            mat.Emission += CalculateRimLighting( mat.Normal, vViewDirWs, g_vRimLightColor, g_flRimLightStrength, g_flRimLightExponent );
        #endif
        
        // Environmental reflections ($envmap equivalent)
        #if ( S_ENVIRONMENTAL_REFLECTIONS )
            float3 vEnvReflectionDir = reflect( -vViewDirWs, mat.Normal );
            float3 vEnvironmentColor = g_tEnvironmentMap.Sample( g_sAniso, vEnvReflectionDir ).rgb;
            
            // Apply environment map tinting ($envmaptint equivalent)
            vEnvironmentColor *= g_vEnvmapTint;
            
            // Calculate fresnel using custom envmap fresnel ranges ($envmapfresnel equivalent)
            float flNdotV = saturate( dot( mat.Normal, vViewDirWs ) );
            float flEnvmapFresnel = smoothstep( g_vEnvmapFresnel.x, g_vEnvmapFresnel.y, 1.0 - flNdotV ) * g_vEnvmapFresnel.z;
            
            // Base reflection mask 
            float flReflectionMask = lerp( mat.Metalness, 1.0, flEnvmapFresnel ) * (1.0 - mat.Roughness);
            
            // Apply phong boost and fresnel ranges - ALWAYS AVAILABLE (controlled by g_flPhongBoost)
            if ( g_flPhongBoost > 0.0 )
            {
                float flEnvPhongFresnel = CalculatePhongFresnel( mat.Normal, vViewDirWs, g_vPhongFresnelRanges );
                flReflectionMask *= g_flPhongBoost * flEnvPhongFresnel;
            }
            
            mat.Emission += vEnvironmentColor * flReflectionMask * g_flReflectionStrength;
        #endif
        
        // Additional phong specular highlights (classic VertexLitGeneric behavior) - ALWAYS AVAILABLE
        if ( g_flPhongBoost > 0.0 )
        {
            // Specular mask source: normal.a (when available) or base.a if $basemapalphaphongmask
            float flSpecMask = g_bBaseMapAlphaPhongMask ? vBaseTexture.a : ((flNormalAlpha > 0.01 && flNormalAlpha < 0.99) ? flNormalAlpha : vBaseTexture.a);

            // Phong specular term (view-reflection) using exponent mapping above
            // Note: Without direct light vectors here, approximate highlight using view reflection
            float3 vApproxLightDir = normalize( g_vCameraPositionWs - vPositionWs );
            float3 vPhongReflectionDir = reflect( -vApproxLightDir, mat.Normal );
            float flSpecularTerm = pow( saturate( dot( vViewDirWs, vPhongReflectionDir ) ), flCombinedPhongExponent );
            float flPhongHighlightFresnel = CalculatePhongFresnel( mat.Normal, vViewDirWs, g_vPhongFresnelRanges );
            
            // Calculate base phong color
            float3 vPhongColor = float3( 1, 1, 1 ); // Default white highlight
            
            // Apply phong tint ($phongtint) - overrides albedo tint if enabled
            bool bUsePhongTint = any( g_vPhongTint != float3( 1, 1, 1 ) );
            if ( bUsePhongTint )
            {
                vPhongColor = g_vPhongTint;
            }
            // Apply phong albedo tint ($phongalbedotint) if enabled and not overridden by phong tint
            else if ( g_bPhongAlbedoTint )
            {
                // Tint phong by base texture color, masked by green channel of phong exponent texture
                float3 vAlbedoTint = lerp( float3( 1, 1, 1 ), mat.Albedo, flPhongAlbedoMask );
                vPhongColor = vAlbedoTint;
            }
            
            // Apply phong warp texture if enabled ($phongwarptexture)
            #if ( S_PHONG_WARP_TEXTURE )
                float3 vPhongWarpColor = SamplePhongWarp( mat.Normal, vViewDirWs, vApproxLightDir, g_vPhongFresnelRanges );
                vPhongColor *= vPhongWarpColor;
            #endif
            
            // Start with camera-approx contribution
            float3 vPhongContribution = vPhongColor * flSpecularTerm * g_flPhongBoost * flPhongHighlightFresnel * flSpecMask;

            // Accumulate per-light Phong like Source/Soft Lamps
            uint lightCount = DynamicLight::Count( i.vPositionSs.xy );
            float3 vLegacyDiffuse = float3(0.0, 0.0, 0.0);
            [loop]
            for ( uint li = 0; li < lightCount; li++ )
            {
                Light L = DynamicLight::From( i.vPositionSs.xy, vPositionWs, li );
                float3 Ldir = L.Direction;
                float Latt = L.Attenuation * L.Visibility;
                float3 R = reflect( -Ldir, mat.Normal );
                float s = pow( saturate( dot( vViewDirWs, R ) ), flCombinedPhongExponent );
                // Mask specular by N.L like Source
                s *= saturate( dot( mat.Normal, Ldir ) );
                float3 c = L.Color * Latt;
                vPhongContribution += vPhongColor * s * flSpecMask * c * g_flPhongBoost;

                // Per-light diffuse with optional Half-Lambert and light warp (Source-like)
                float lambert = saturate( dot( mat.Normal, Ldir ) );
                float halfLambert = saturate( lambert * 0.5 + 0.5 );
                float diffTerm = g_bPhongDisableHalfLambert ? lambert : (halfLambert * halfLambert);
                float warpSample = g_tLightWarpTexture.Sample( g_sAniso, float2( diffTerm, 0.5 ) ).r;
                float applyWarp = abs( warpSample - 0.5 ) > 0.01 ? 1.0 : 0.0;
                float diffWarped = lerp( diffTerm, 2.0 * warpSample, applyWarp );
                vLegacyDiffuse += mat.Albedo * diffWarped * c;
            }

            // Add phong specular to emission (classic additive path)
            // Do NOT attenuate by roughness when legacy phong is enabled (we already disabled PBR spec)
            float flPhongAtten = g_bLegacyPhongEnabled ? 1.0 : (1.0 - mat.Roughness);
            mat.Emission += vPhongContribution * flPhongAtten;

            // Add legacy diffuse contribution
            mat.Emission += vLegacyDiffuse;
        }

        // Removed previous camera-based half-lambert lift; per-light diffuse is handled above
        
        //=============================================================================================================
        // LIGHT WARP TEXTURE SUPPORT
        //=============================================================================================================
        
        // Light warp texture support - simulate Source engine behavior - ALWAYS AVAILABLE
        // Light warp is applied when texture is not a neutral gray (0.5) value
        float flLightWarpSample = g_tLightWarpTexture.Sample( g_sAniso, float2( 0.5, 0.5 ) ).r;
        if ( abs( flLightWarpSample - 0.5 ) > 0.01 ) // Only apply if not neutral gray
        {
            // Calculate surface lighting for light warp lookup
            float3 vLightWarpLightDir = normalize( float3( 0.5, 0.8, 0.3 ) ); // Simulated main light direction
            float flLightWarpNdotL = dot( mat.Normal, vLightWarpLightDir );
            float flLightWarpValue = WarpLighting( flLightWarpNdotL );
            
            // Apply light warp as an additive emission term (not subtractive)
            // This prevents darkening and matches Source engine behavior better
            float3 vLightWarpContribution = mat.Albedo * flLightWarpValue * 0.3; // 30% contribution
            mat.Emission += vLightWarpContribution;
        }
        
        //=============================================================================================================
        // FINAL SHADING
        //=============================================================================================================
        
        return ShadingModelStandard::Shade( i, mat );
    }
}
