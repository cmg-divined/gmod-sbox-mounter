using System;
using IO = System.IO;
using Sandbox.Mounting;

internal sealed class GModVmtMaterialLoader : ResourceLoader<GModMount>
{
	public string RelativePath { get; }

	public GModVmtMaterialLoader( string relativePath )
	{
		RelativePath = relativePath;
	}

	protected override object Load()
	{
		Log.Info($"[gmod vmt] Loading '{RelativePath}' as '{Path}'");
		using var stream = ((GModMount)Host).GetFileStreamForVmt( RelativePath );
		if ( stream == IO.Stream.Null )
		{
			Log.Warning($"[gmod vmt] stream null for '{RelativePath}'");
			return Material.Create(Path, "shaders/VertexLitGeneric2.shader");
		}

		var data = GModVmt.Parse( stream );
		data ??= new GModVmt.Data();
		data.Shader ??= "VertexLitGeneric";
		Log.Info($"[gmod vmt] shader='{data.Shader}' keys={data.Kv.Count}");

		var mat = Material.Create(Path, "shaders/VertexLitGeneric2.shader");
		Log.Info($"[gmod vmt] compiled material path likely: mount://gmod/{RelativePath}.vmat (resource Path='{Path}')");
		// Set VertexLitGeneric2 defaults
		mat.Set("g_vColorTint", new System.Numerics.Vector3(1f,1f,1f));
		mat.Set("g_bConstantSpecularExponent", true);
		mat.Set("g_bConstantSpecularTint", true);
		mat.Set("g_vTextureScale", new System.Numerics.Vector2(1f,1f));
		mat.Set("g_vTextureOffset", new System.Numerics.Vector2(0f,0f));
		mat.Set("g_flTextureRotation", 0.0f);
		
		// Set default textures that the shader expects
		var white = Texture.White;
		var gray = Texture.Load("materials/default/default_normal.vtex") ?? white; // neutral normal
		mat.Set("g_tNormal", gray);
		mat.Set("g_tAmbientOcclusionTexture", white);
		mat.Set("g_tSpecularExponentTexture", white);
		mat.Set("g_tDetailTexture", white);
		mat.Set("g_tSelfIllumMaskTexture", white);

		if ( data.Kv.TryGetValue("$basetexture", out var baseTex) )
		{
			Log.Info($"[gmod vmt] $basetexture='{baseTex}'");
			if ( ((GModMount)Host).TryResolveVtfByBasetexture(baseTex, out var vtfRel) )
			{
				Log.Info($"[gmod vmt] basetexture '{baseTex}' -> '{vtfRel}'");
				var tex = GModVtfTextureLoader.LoadTextureFromMount((GModMount)Host, vtfRel);
				mat.Set("BaseTexture", tex);
				mat.Set("g_tColor", tex);
			}
			else
			{
				Log.Warning($"[gmod vmt] basetexture resolve failed for '{baseTex}'");
			}
		}

		// Normal map ($bumpmap)
		if ( data.Kv.TryGetValue("$bumpmap", out var bump) || data.Kv.TryGetValue("$normalmap", out bump) )
		{
			Log.Info($"[gmod vmt] $bumpmap='{bump}'");
			if ( ((GModMount)Host).TryResolveVtfByBasetexture(bump, out var vtfB) )
			{
				var n = GModVtfTextureLoader.LoadTextureFromMount((GModMount)Host, vtfB);
				mat.Set("g_tNormal", n);
				Log.Info($"[gmod vmt] bumpmap '{bump}' -> '{vtfB}' (assigned to NormalMap/Normal/g_tNormal/g_tNormalMap/BumpMap/g_tBumpMap)");
			}
			else
			{
				Log.Warning($"[gmod vmt] bumpmap resolve failed for '{bump}'");
			}
		}

		// Color tint ($color2) -> Color Tint in VertexLitGeneric2
		if ( data.Kv.TryGetValue("$color2", out var color2) )
		{
			if ( TryParseVec3(color2, out var v) )
			{
				mat.Set("g_vColorTint", new System.Numerics.Vector3(v.X, v.Y, v.Z));
				Log.Info($"[gmod vmt] $color2 -> g_vColorTint = [{v.X:0.###},{v.Y:0.###},{v.Z:0.###}]");
			}
		}

		// Blend tint by base alpha (flag + dynamic combo later)
		bool blendTint = false;
		if ( data.Kv.TryGetValue("$blendTintByBaseAlpha", out var blend) )
		{
			blendTint = IsTrue(blend);
			if ( blendTint ) { mat.Set("g_bBlendTintByBaseAlpha", true); Log.Info("[gmod vmt] enabled g_bBlendTintByBaseAlpha"); }
		}

		// Light warp -> texture + dynamic combo later
		bool hasLightwarp = false;
		if ( data.Kv.TryGetValue("$lightwarptexture", out var lightwarp) )
		{
			Log.Info($"[gmod vmt] $lightwarptexture='{lightwarp}'");
			if ( ((GModMount)Host).TryResolveVtfByBasetexture(lightwarp, out var vtfL) )
			{
				var lw = GModVtfTextureLoader.LoadTextureFromMount((GModMount)Host, vtfL);
				mat.Set("LightWarpTexture", lw);
				mat.Set("g_tLightWarpTexture", lw);
				hasLightwarp = true;
				Log.Info($"[gmod vmt] lightwarp '{lightwarp}' -> '{vtfL}'");
			}
			else
			{
				Log.Warning($"[gmod vmt] lightwarp resolve failed for '{lightwarp}'");
			}
		}

		// Phong enable + exponent (constant and texture)
		bool phongEnabled = false;
		if ( data.Kv.TryGetValue("$phong", out var phong) )
		{
			if ( IsTrue(phong) ) { phongEnabled = true; }
		}

		if ( data.Kv.TryGetValue("$phongexponent", out var pexpConst) )
		{
			if ( float.TryParse(pexpConst, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) )
			{
				mat.Set("g_flPhongExponent", f);
				phongEnabled = true;
			}
		}

		// Phong exponent texture -> SpecularExponentTexture vs Constant
		bool hasSpecExpTex = false;
		if ( data.Kv.TryGetValue("$phongexponenttexture", out var pexp) )
		{
			Log.Info($"[gmod vmt] $phongexponenttexture='{pexp}'");
			if ( ((GModMount)Host).TryResolveVtfByBasetexture(pexp, out var vtfP) )
			{
				var pt = GModVtfTextureLoader.LoadTextureFromMount((GModMount)Host, vtfP);
				mat.Set("g_tSpecularExponentTexture", pt);
				mat.Set("g_bConstantSpecularExponent", false);
				hasSpecExpTex = true;
				phongEnabled = true;
				Log.Info($"[gmod vmt] phongexptex '{pexp}' -> '{vtfP}'");
			}
			else
			{
				Log.Warning($"[gmod vmt] phongexponenttexture resolve failed for '{pexp}'");
			}
		}
		if ( !hasSpecExpTex )
		{
			mat.Set("g_bConstantSpecularExponent", true);
		}

		// Phong fresnel ranges
		if ( data.Kv.TryGetValue("$phongfresnelranges", out var pf) )
		{
			if ( TryParseVec3(pf, out var v) )
			{
				mat.Set("g_vSourceFresnelRanges", new System.Numerics.Vector3(v.X, v.Y, v.Z));
				Log.Info($"[gmod vmt] $phongfresnelranges -> [{v.X:0.###},{v.Y:0.###},{v.Z:0.###}]");
			}
		}

		// Phong disable Half-Lambert
		if ( data.Kv.TryGetValue("$PhongDisableHalfLambert", out var pdhl) || data.Kv.TryGetValue("$phongdisablehalflambert", out pdhl) )
		{
			if ( IsTrue(pdhl) ) { mat.Set("g_bPhongDisableHalfLambert", true); Log.Info("[gmod vmt] $PhongDisableHalfLambert -> g_bPhongDisableHalfLambert=1"); }
		}

		// Phong boost
		if ( data.Kv.TryGetValue("$phongboost", out var pboost) )
		{
			if ( float.TryParse(pboost, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) )
			{
				mat.Set("g_flSpecularBoost", f);
				Log.Info($"[gmod vmt] $phongboost -> {f}");
			}
		}

		// Phong warp texture -> Specular warp + dynamic combo later
		bool hasPhongWarp = false;
		if ( data.Kv.TryGetValue("$phongwarptexture", out var pwarp) )
		{
			if ( ((GModMount)Host).TryResolveVtfByBasetexture(pwarp, out var vtfW) )
			{
				var wt = GModVtfTextureLoader.LoadTextureFromMount((GModMount)Host, vtfW);
				mat.Set("g_tSpecularWarpTexture", wt);
				hasPhongWarp = true;
				Log.Info($"[gmod vmt] phongwarp '{pwarp}' -> '{vtfW}'");
			}
		}

		// Base map alpha as Phong mask
		if ( data.Kv.TryGetValue("$basemapalphaphongmask", out var bmapMask) )
		{
			if ( IsTrue(bmapMask) ) { mat.Set("g_bBaseMapAlphaPhongMask", true); }
		}

		// Optional tints (Specular Tint vs Constant)
		if ( data.Kv.TryGetValue("$phongtint", out var ptint) && TryParseVec3(ptint, out var vt) )
		{
			mat.Set("g_bConstantSpecularTint", false);
			mat.Set("g_vSpecularTint", new System.Numerics.Vector3(vt.X, vt.Y, vt.Z));
			Log.Info($"[gmod vmt] $phongtint -> g_vSpecularTint = [{vt.X:0.###},{vt.Y:0.###},{vt.Z:0.###}], g_bConstantSpecularTint=false");
		}
		else
		{
			mat.Set("g_bConstantSpecularTint", true);
		}
		// Rimlight dynamic combo + params
		bool rimEnable = false;
		if ( data.Kv.TryGetValue("$rimlight", out var rim) && IsTrue(rim) ) rimEnable = true;
		if ( data.Kv.TryGetValue("$rimmask", out var rmask) && IsTrue(rmask) ) { mat.Set("g_flRimMask", 1.0f); rimEnable = true; }
		if ( data.Kv.TryGetValue("$rimlightboost", out var rboost) && float.TryParse(rboost, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fboost) ) { mat.Set("g_flRimBoost", fboost); rimEnable = true; }
		if ( data.Kv.TryGetValue("$rimexponent", out var rexp) && float.TryParse(rexp, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fexp) ) { mat.Set("g_flRimExponent", fexp); rimEnable = true; }

		if ( data.Kv.TryGetValue("$phongalbedotint", out var palt) )
		{
			if ( IsTrue(palt) ) { mat.Set("g_bPhongAlbedoTint", true); }
		}

		// Phong is handled in VertexLitGeneric2 via specular exponent texture and parameters
		if ( phongEnabled )
		{
			Log.Info($"[gmod vmt] Phong enabled in VMT, handled via g_flPhongExponent and related parameters");
		}

		// Set dynamic combos via material attributes
		try
		{
			var attr = mat.Attributes;
			if ( attr != null )
			{
				attr.SetCombo( "D_LIGHTWARPTEXTURE", hasLightwarp ? 1 : 0 );
				attr.SetCombo( "D_BLENDTINTBYBASEALPHA", blendTint ? 1 : 0 );
				attr.SetCombo( "D_PHONGWARPTEXTURE", hasPhongWarp ? 1 : 0 );
				attr.SetCombo( "D_RIMLIGHT", rimEnable ? 1 : 0 );
			}
		}
		catch (Exception ex)
		{
			Log.Warning($"[gmod vmt] Failed to set dynamic combos: {ex.Message}");
		}

		return mat;
	}

	private static bool IsTrue( string s )
	{
		if (string.IsNullOrWhiteSpace(s)) return false;
		s = s.Trim();
		return string.Equals(s, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryParseVec3( string s, out (float X, float Y, float Z) v )
	{
		v = (0,0,0);
		if (string.IsNullOrWhiteSpace(s)) return false;
		s = s.Trim();
		if (s.StartsWith("[")) s = s.Trim('[',']');
		s = s.Replace(",", " ");
		var parts = s.Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 3) return false;
		if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)) return false;
		if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)) return false;
		if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z)) return false;
		v = (x,y,z);
		return true;
	}
}


