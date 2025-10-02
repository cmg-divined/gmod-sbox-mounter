using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Sandbox;
using Sandbox.Mounting;

// Minimal Source MDL wrapper. For now we only parse headers and emit a placeholder Mesh.
// Later we can expand to full VTX/VVD parsing as in Crowbar.
internal sealed class GModSourceModel : ResourceLoader<GModMount>
{
    private static readonly bool FlipV = false; // debug toggle
    private static readonly bool ClampUVs = false; // leave unclamped unless diagnosing wrap
	public string FullPath { get; }

	public GModSourceModel(string fullPath) : base()
	{
		FullPath = fullPath;
	}

	protected override object Load()
	{
		try
		{
			Log.Info($"[gmod mdl] Loading: {System.IO.Path.GetFileName(FullPath)}");
			using var fs = File.OpenRead(FullPath);
			using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);

			var id = new string(br.ReadChars(4));
			int version = br.ReadInt32();
			int checksum = br.ReadInt32();

			if (id != "IDST" && id != "MDLZ")
			{
				throw new InvalidDataException($"Unexpected MDL id '{id}' in {FullPath}");
			}

			// Read stored internal name (most versions use a fixed 64-byte name field)
			string mdlName = System.Text.Encoding.ASCII.GetString(br.ReadBytes(64)).TrimEnd('\0');
			int fileLength = br.ReadInt32();
			if (string.IsNullOrWhiteSpace(mdlName)) mdlName = System.IO.Path.GetFileName(FullPath);

			Log.Info($"[gmod mdl] Parsed header: name='{mdlName}' v{version} len={fileLength} checksum=0x{checksum:X8}");

			// Try to read a few more common header fields for context (best-effort)
			try
			{
				var eye = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				var illum = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				var hullMin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				var hullMax = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				var viewMin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				var viewMax = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				int flags = br.ReadInt32();
				int numBones = br.ReadInt32();
				int boneIndex = br.ReadInt32();
				Log.Info($"[gmod mdl] eye={eye} hull=({hullMin} .. {hullMax}) bones={numBones} flags=0x{flags:X8}");
			}
			catch (Exception)
			{
				// ignore partial header read errors for now
			}

			// Sidecar file discovery (.vvd and .vtx variants)
			var dir = System.IO.Path.GetDirectoryName(FullPath) ?? string.Empty;
			var stem = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(FullPath));
			string vvd = stem + ".vvd";
			string[] vtxCandidates = new[] { stem + ".dx90.vtx", stem + ".dx80.vtx", stem + ".sw.vtx", stem + ".vtx" };
			string vtx = null;
			foreach (var c in vtxCandidates)
			{
				if (System.IO.File.Exists(c)) { vtx = c; break; }
			}
			string vvdDesc = DescribeFile(vvd);
			string vtxDesc = vtx != null ? DescribeFile(vtx) : "not found";
			Log.Info($"[gmod mdl] Sidecars: VVD={vvdDesc}, VTX={vtxDesc}");

			// If VVD is present, parse and log vertex summary
			SourceVvd.Data vvdData = null;
			if (System.IO.File.Exists(vvd))
			{
				vvdData = SourceVvd.Parse(vvd);
				Log.Info($"[gmod mdl] VVD verts={vvdData.Vertices.Count} ver={vvdData.Version} lods={vvdData.LodCount}");
			}

			SourceVtx.Summary vtxSummary = null;
			if (vtx != null)
			{
				vtxSummary = SourceVtx.Parse(vtx);
			}

			// Read bodygroups (bodyparts) and model names
			try
			{
				ReadAndLogBodygroups(FullPath);
				ReadAndLogModelsAndMeshes(FullPath);
				ReadAndLogMaterials(FullPath);
				ReadAndLogTextureLists(FullPath);
			}
			catch
			{
				//Log.warning($"[gmod mdl] Bodygroup read failed");
			}

			// Create model builder
			var builder = Model.Builder.WithName(Path);

			// Build skeleton (bones) from MDL
			try
			{
				BuildSkeletonFromMdl(FullPath, builder);
			}
			catch
			{
				//Log.warning($"[gmod mdl] Skeleton build failed");
			}

			// Build all LOD0 meshes across bodyparts/models; fallback to placeholder if none
			int builtCount = BuildAllMeshesLOD0(vtx, vvdData, FullPath, builder, (GModMount)Host);
			if (builtCount == 0)
			{
				var material = Material.Create("model", "shaders/VertexLitGeneric2.shader");
				material.Set("BaseTexture", Texture.White);
				material.Set("BaseTextureMask", GetWhite1x1());
				material.Set("AmbientOcclusion", GetWhite1x1());
				material.Set("Metalness", GetWhite1x1());
				material.Set("Roughness", GetWhite1x1());
				material.Set("OpacityMap", GetWhite1x1());
				material.Set("DetailTexture", GetWhite1x1());
				material.Set("DetailMask", GetWhite1x1());
				material.Set("LightWarpTexture", GetGray1x1());
				material.Set("SpecularWarpTexture", GetWhite1x1());
				material.Set("g_bFogEnabled", true);
				material.Set("g_bBlendTintByBaseAlpha", false);
				material.Set("g_bUseMetalnessValue", true);
				material.Set("g_bUseRoughnessValue", true);
				material.Set("g_flMetalnessValue", 0.0f);
				material.Set("g_flRoughnessValue", 1.0f);
				material.Set("g_vColorTint", new Vector3(1f,1f,1f));
				material.Set("NormalIntensity", 1.1f);
				material.Set("g_flNormalIntensity", 1.1f);
				var mesh = new Mesh(material);
				var verts = new List<SimpleVertex>
				{
					new SimpleVertex(new Vector3(-5, -5, 0), Vector3.Up, Vector3.Zero, new Vector2(0,0)),
					new SimpleVertex(new Vector3( 5, -5, 0), Vector3.Up, Vector3.Zero, new Vector2(1,0)),
					new SimpleVertex(new Vector3( 0,  5, 0), Vector3.Up, Vector3.Zero, new Vector2(0.5f,1))
				};
				mesh.CreateVertexBuffer(verts.Count, SimpleVertex.Layout, verts);
				mesh.CreateIndexBuffer(3, new int[]{0,1,2});
				mesh.Bounds = BBox.FromPositionAndSize(Vector3.Zero, new Vector3(10,10,0.1f));
				builder.AddMesh(mesh);
				Log.Info($"[gmod mdl] Built placeholder mesh for '{mdlName}'");
			}

			var model = builder.Create();
			
			// Try alternative approach: Set material attributes on the final model
			// This mimics the approach shown in user's example where they access Materials.GetOverride()
			try
			{
				Log.Info("[gmod mdl] Attempting to set material attributes on final model...");
				
				// Get all materials from the model and try to set attributes
				for (int matIndex = 0; matIndex < 10; matIndex++) // Try first 10 material slots
				{
					try
					{
						// Note: We can't directly access model.Materials here since it's not available
						// in the Model type, but this approach would work in a Component context
						Log.Info($"[gmod mdl] Would try to access material slot {matIndex} if this were in a Component");
					}
					catch (Exception ex)
					{
						Log.Info($"[gmod mdl] Material slot {matIndex} not accessible: {ex.Message}");
						break;
					}
				}
			}
			catch
			{
				//Log.warning($"[gmod mdl] Failed to post-process material attributes");
			}
			
			return model;
		}
		catch (Exception ex)
		{
			Log.Error($"[gmod mdl] Failed to load MDL '{FullPath}': {ex.Message}");
			throw;
		}
	}

	private static string DescribeFile(string path)
	{
		try
		{
			if (!System.IO.File.Exists(path)) return "not found";
			var fi = new System.IO.FileInfo(path);
			return $"{fi.Name} ({fi.Length:N0} bytes)";
		}
		catch
		{
			return "unavailable";
		}
	}

	private static (string groupName, int choiceIndex) GetBodypartAndModelName(string mdlPath, int bodyPartIndex, int modelIndex)
	{
		using var fs = System.IO.File.OpenRead(mdlPath);
		using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
		br.ReadBytes(4 + 4 + 4);
		br.ReadBytes(64);
		br.ReadInt32();
		br.ReadBytes(sizeof(float) * 18);
		br.ReadInt32();
		br.ReadInt32();
		br.ReadInt32();
		for (int i = 0; i < 17; i++) br.ReadInt32();
		int numBodyParts = br.ReadInt32();
		int bodyIndex = br.ReadInt32();
		if (bodyPartIndex < 0 || bodyPartIndex >= numBodyParts || bodyIndex <= 0) return (null, modelIndex);
		long bodyPos = bodyIndex + bodyPartIndex * 16;
		fs.Seek(bodyPos, SeekOrigin.Begin);
		int nameIndex = br.ReadInt32();
		int numModels = br.ReadInt32();
		int bpBase = br.ReadInt32();
		int modelOffset = br.ReadInt32();
		string groupName = ReadCString(fs, br, bodyPos + nameIndex);
		if (string.IsNullOrWhiteSpace(groupName)) groupName = $"bodypart_{bodyPartIndex}";
		return (groupName, modelIndex);
	}


	private static void ReadAndLogBodygroups(string mdlPath)
	{
		using var fs = System.IO.File.OpenRead(mdlPath);
		using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);

		// Header base = 0
		// Skip id/version/checksum/name[64]/length
		br.ReadBytes(4 + 4 + 4); // id, version, checksum
		br.ReadBytes(64); // name
		br.ReadInt32(); // length

		// Skip 6 vectors (18 floats)
		br.ReadBytes(sizeof(float) * 18);
		br.ReadInt32(); // flags
		br.ReadInt32(); // numbones
		br.ReadInt32(); // boneindex

		// Advance through known header ints up to skin + bodypart fields
		// numbonecontrollers, bonecontrollerindex,
		// numhitboxsets, hitboxsetindex,
		// numlocalanim, localanimindex,
		// numlocalseq, localseqindex,
		// activitylistversion, eventsindexed,
		// numtextures, textureindex,
		// numcdtextures, cdtextureindex,
		// numskinref, numskinfamilies, skinindex
		for (int i = 0; i < 17; i++) br.ReadInt32();

		int numBodyParts = br.ReadInt32();
		int bodyPartIndex = br.ReadInt32();
		Log.Info($"[gmod mdl] Bodygroups: count={numBodyParts}");
		if (numBodyParts <= 0 || bodyPartIndex <= 0) return;

		for (int i = 0; i < numBodyParts; i++)
		{
			long bodyPartPos = bodyPartIndex + i * 16; // 4 ints
			fs.Seek(bodyPartPos, SeekOrigin.Begin);
			int bpNameIndex = br.ReadInt32();
			int numModels = br.ReadInt32();
			int bpBase = br.ReadInt32();
			int modelIndex = br.ReadInt32();

			string bpName = ReadCString(fs, br, bodyPartPos + bpNameIndex);
			if (string.IsNullOrWhiteSpace(bpName)) bpName = $"bodypart_{i}";
			Log.Info($"[gmod mdl]  - '{bpName}' models={numModels}");

			if (numModels > 0 && modelIndex > 0)
			{
				// Best-effort model names; mstudiomodel_t starts with name[64]
				int[] candidateStride = new int[] { 148, 136, 160, 208, 216 };
				int stride = candidateStride[0];
				var sampleNames = new List<string>();
				for (int j = 0; j < numModels; j++)
				{
					long modelPos = bodyPartPos + modelIndex + j * stride;
					fs.Seek(modelPos, SeekOrigin.Begin);
					string modelName = ReadFixedCString(br, 64);
					if (string.IsNullOrWhiteSpace(modelName)) modelName = $"model_{j}";
					if (sampleNames.Count < 5) sampleNames.Add(modelName);
				}
				if (numModels > 0)
				{
					string preview = string.Join(", ", sampleNames);
					if (numModels > sampleNames.Count) preview += ", …";
					Log.Info($"[gmod mdl]      models: {preview}");
				}
			}
		}
	}

	private static string ReadCString(System.IO.FileStream fs, BinaryReader br, long offset)
	{
		try
		{
			long ret = fs.Position;
			fs.Seek(offset, SeekOrigin.Begin);
			var bytes = new List<byte>(64);
			for (;;)
			{
				byte b = br.ReadByte();
				if (b == 0) break;
				bytes.Add(b);
				if (bytes.Count > 256) break;
			}
			string s = System.Text.Encoding.ASCII.GetString(bytes.ToArray());
			fs.Seek(ret, SeekOrigin.Begin);
			return s;
		}
		catch { return string.Empty; }
	}

	private static string ReadFixedCString(BinaryReader br, int len)
	{
		var buf = br.ReadBytes(len);
		int zero = Array.IndexOf(buf, (byte)0);
		int n = zero >= 0 ? zero : buf.Length;
		return System.Text.Encoding.ASCII.GetString(buf, 0, n);
	}

	// Best-effort computation of each model's base index into VVD vertex array from MDL
	// Returns list indexed by bodypart -> model -> vertexIndex (base)
	private static List<List<int>> ComputeModelVertexBases(string mdlPath)
	{
		var result = new List<List<int>>();
		try
		{
			using var fs = System.IO.File.OpenRead(mdlPath);
			using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
			br.ReadBytes(4 + 4 + 4); // id, version, checksum
			br.ReadBytes(64); // name
			br.ReadInt32(); // length
			br.ReadBytes(sizeof(float) * 18);
			br.ReadInt32(); // flags
			br.ReadInt32(); // numbones
			br.ReadInt32(); // boneindex
			for (int i = 0; i < 17; i++) br.ReadInt32();
			int numBodyParts = br.ReadInt32();
			int bodyPartIndex = br.ReadInt32();
			for (int i = 0; i < numBodyParts; i++)
			{
				long bodyPartPos = bodyPartIndex + i * 16;
				fs.Seek(bodyPartPos, SeekOrigin.Begin);
				int bpNameIndex = br.ReadInt32();
				int numModels = br.ReadInt32();
				int bpBase = br.ReadInt32();
				int modelIndex = br.ReadInt32();
				var row = new List<int>();
				for (int j = 0; j < numModels; j++)
				{
					long modelPos = bodyPartPos + modelIndex + j * 148; // approx stride
					fs.Seek(modelPos, SeekOrigin.Begin);
					br.ReadBytes(64); // name
					br.ReadInt32(); // type
					br.ReadSingle(); // radius
					int meshCount = br.ReadInt32();
					int meshOffset = br.ReadInt32();
					int numVertices = br.ReadInt32();
					int vertexIndex = br.ReadInt32();
					row.Add(vertexIndex);
				}
				result.Add(row);
			}
		}
		catch { }
		return result;
	}

	// Returns mesh vertex offsets: [bodypart][model][mesh] = meshVertexOffset
	private static List<List<List<int>>> ComputeMeshVertexOffsets(string mdlPath)
	{
		var result = new List<List<List<int>>>();
		try
		{
			using var fs = System.IO.File.OpenRead(mdlPath);
			using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
			br.ReadBytes(4 + 4 + 4);
			br.ReadBytes(64);
			br.ReadInt32();
			br.ReadBytes(sizeof(float) * 18);
			br.ReadInt32(); // flags
			br.ReadInt32(); // numbones
			br.ReadInt32(); // boneindex
			for (int i = 0; i < 17; i++) br.ReadInt32();
			int numBodyParts = br.ReadInt32();
			int bodyPartIndex = br.ReadInt32();
			for (int bp = 0; bp < numBodyParts; bp++)
			{
				long bodyPartPos = bodyPartIndex + bp * 16;
				fs.Seek(bodyPartPos, SeekOrigin.Begin);
				br.ReadInt32(); // nameindex
				int numModels = br.ReadInt32();
				br.ReadInt32(); // base
				int modelIndex = br.ReadInt32();
				var modelsList = new List<List<int>>();
				for (int m = 0; m < numModels; m++)
				{
					long modelPos = bodyPartPos + modelIndex + m * 148; // approx
					fs.Seek(modelPos, SeekOrigin.Begin);
					br.ReadBytes(64); // name
					br.ReadInt32(); // type
					br.ReadSingle(); // radius
					int meshCount = br.ReadInt32();
					int meshOffset = br.ReadInt32();
					br.ReadInt32(); // numvertices
					br.ReadInt32(); // vertexindex
					var meshList = new List<int>();
					for (int me = 0; me < Math.Max(0, meshCount); me++)
					{
						long meshPos = modelPos + meshOffset + me * 56; // approx
						fs.Seek(meshPos, SeekOrigin.Begin);
						br.ReadInt32(); // material
						br.ReadInt32(); // modelindex backref
						br.ReadInt32(); // numvertices
						int meshVertexOffset = br.ReadInt32();
						meshList.Add(meshVertexOffset);
					}
					modelsList.Add(meshList);
				}
				result.Add(modelsList);
			}
		}
		catch { }
		return result;
	}

	// Fallback: compute per-(bodypart,model) base by accumulating model vertex counts in MDL order
	private static List<List<int>> ComputeModelBasesByAccumulation(string mdlPath)
	{
		var bases = new List<List<int>>();
		try
		{
			using var fs = System.IO.File.OpenRead(mdlPath);
			using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
			br.ReadBytes(4 + 4 + 4);
			br.ReadBytes(64);
			br.ReadInt32();
			br.ReadBytes(sizeof(float) * 18);
			br.ReadInt32(); // flags
			br.ReadInt32(); // numbones
			br.ReadInt32(); // boneindex
			for (int i = 0; i < 17; i++) br.ReadInt32();
			int numBodyParts = br.ReadInt32();
			int bodyPartIndex = br.ReadInt32();

			int running = 0;
			for (int bp = 0; bp < numBodyParts; bp++)
			{
				long bodyPartPos = bodyPartIndex + bp * 16;
				fs.Seek(bodyPartPos, SeekOrigin.Begin);
				br.ReadInt32(); // nameindex
				int numModels = br.ReadInt32();
				br.ReadInt32(); // base
				int modelIndex = br.ReadInt32();
				var row = new List<int>();
				for (int m = 0; m < numModels; m++)
				{
					row.Add(running);
					long modelPos = bodyPartPos + modelIndex + m * 148; // approx
					fs.Seek(modelPos, SeekOrigin.Begin);
					br.ReadBytes(64); // name
					br.ReadInt32(); // type
					br.ReadSingle(); // radius
					int meshCount = br.ReadInt32();
					int meshOffset = br.ReadInt32();
					int numVertices = br.ReadInt32();
					br.ReadInt32(); // vertexindex
					running += Math.Max(0, numVertices);
				}
				bases.Add(row);
			}
		}
		catch { }
		return bases;
	}

	// Fallback: compute per-mesh offsets by accumulating mesh vertex counts within a model
	private static List<List<List<int>>> ComputeMeshOffsetsByAccumulation(string mdlPath)
	{
		var result = new List<List<List<int>>>();
		try
		{
			using var fs = System.IO.File.OpenRead(mdlPath);
			using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
			br.ReadBytes(4 + 4 + 4);
			br.ReadBytes(64);
			br.ReadInt32();
			br.ReadBytes(sizeof(float) * 18);
			br.ReadInt32(); // flags
			br.ReadInt32(); // numbones
			br.ReadInt32(); // boneindex
			for (int i = 0; i < 17; i++) br.ReadInt32();
			int numBodyParts = br.ReadInt32();
			int bodyPartIndex = br.ReadInt32();
			for (int bp = 0; bp < numBodyParts; bp++)
			{
				long bodyPartPos = bodyPartIndex + bp * 16;
				fs.Seek(bodyPartPos, SeekOrigin.Begin);
				br.ReadInt32(); // nameindex
				int numModels = br.ReadInt32();
				br.ReadInt32(); // base
				int modelIndex = br.ReadInt32();
				var models = new List<List<int>>();
				for (int m = 0; m < numModels; m++)
				{
					long modelPos = bodyPartPos + modelIndex + m * 148; // approx
					fs.Seek(modelPos, SeekOrigin.Begin);
					br.ReadBytes(64); // name
					br.ReadInt32(); // type
					br.ReadSingle(); // radius
					int meshCount = br.ReadInt32();
					int meshOffset = br.ReadInt32();
					br.ReadInt32(); // numverts
					br.ReadInt32(); // vertexindex
					var offsets = new List<int>();
					int running = 0;
					for (int me = 0; me < Math.Max(0, meshCount); me++)
					{
						offsets.Add(running);
						long meshPos = modelPos + meshOffset + me * 56; // approx
						fs.Seek(meshPos, SeekOrigin.Begin);
						br.ReadInt32(); // material
						br.ReadInt32(); // modelindex backref
						int meshVerts = br.ReadInt32();
						br.ReadInt32(); // mesh vertex offset (ignore)
						running += Math.Max(0, meshVerts);
					}
					models.Add(offsets);
				}
				result.Add(models);
			}
		}
		catch { }
		return result;
	}

	private static void ReadAndLogMaterials(string mdlPath)
	{
		using var fs = System.IO.File.OpenRead(mdlPath);
		using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
		br.ReadBytes(4 + 4 + 4); // id, version, checksum
		br.ReadBytes(64); // name
		int length = br.ReadInt32();
		br.ReadBytes(sizeof(float) * 18); // eye/illum/hull/view
		int flags = br.ReadInt32();
		int numbones = br.ReadInt32();
		int boneindex = br.ReadInt32();
		int numbonecontrollers = br.ReadInt32();
		int bonecontrollerindex = br.ReadInt32();
		int numhitboxsets = br.ReadInt32();
		int hitboxsetindex = br.ReadInt32();
		int numlocalanim = br.ReadInt32();
		int localanimindex = br.ReadInt32();
		int numlocalseq = br.ReadInt32();
		int localseqindex = br.ReadInt32();
		int activitylistversion = br.ReadInt32();
		int eventsindexed = br.ReadInt32();
		int numtextures = br.ReadInt32();
		int textureindex = br.ReadInt32();
		int numcdtextures = br.ReadInt32();
		int cdtextureindex = br.ReadInt32();
		int numskinref = br.ReadInt32();
		int numskinfamilies = br.ReadInt32();
		int skinindex = br.ReadInt32();
		Log.Info($"[gmod mdl] Materials: textures={numtextures}, cdtex={numcdtextures}, skinref={numskinref}, skinfamilies={numskinfamilies}");
	}

	private static void ReadAndLogTextureLists(string mdlPath)
	{
		using var fs = System.IO.File.OpenRead(mdlPath);
		using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
		br.ReadBytes(4 + 4 + 4); // id, version, checksum
		br.ReadBytes(64); // name
		int length = br.ReadInt32();
		br.ReadBytes(sizeof(float) * 18);
		br.ReadInt32(); // flags
		br.ReadInt32(); // numbones
		br.ReadInt32(); // boneindex
		br.ReadInt32(); // numbonecontrollers
		br.ReadInt32(); // bonecontrollerindex
		br.ReadInt32(); // numhitboxsets
		br.ReadInt32(); // hitboxsetindex
		br.ReadInt32(); // numlocalanim
		br.ReadInt32(); // localanimindex
		br.ReadInt32(); // numlocalseq
		br.ReadInt32(); // localseqindex
		br.ReadInt32(); // activitylistversion
		br.ReadInt32(); // eventsindexed
		int numtextures = br.ReadInt32();
		int textureindex = br.ReadInt32();
		int numcdtextures = br.ReadInt32();
		int cdtextureindex = br.ReadInt32();
		int numskinref = br.ReadInt32();
		int numskinfamilies = br.ReadInt32();
		int skinindex = br.ReadInt32();

		var cdpaths = new List<string>();
		try
		{
			for (int i = 0; i < numcdtextures; i++)
			{
				fs.Seek(cdtextureindex + i * 4, SeekOrigin.Begin);
				int strOffset = br.ReadInt32();
				string p = ReadCString(fs, br, cdtextureindex + strOffset);
				if (!string.IsNullOrWhiteSpace(p)) cdpaths.Add(p.Replace('\\', '/'));
			}
		}
		catch { }

		var textures = new List<string>();
		try
		{
			for (int i = 0; i < Math.Min(numtextures, 32); i++)
			{
				long texPos = textureindex + i * 64; // assume at least 64 bytes structure to hop
				fs.Seek(texPos, SeekOrigin.Begin);
				int nameOffset = br.ReadInt32();
				string name = ReadCString(fs, br, texPos + nameOffset);
				if (!string.IsNullOrWhiteSpace(name)) textures.Add(name);
			}
		}
		catch { }

		if (cdpaths.Count > 0 || textures.Count > 0)
		{
			string cd = cdpaths.Count > 0 ? string.Join(", ", cdpaths) : "<none>";
			string tx = textures.Count > 0 ? string.Join(", ", textures) : "<none>";
			Log.Info($"[gmod mdl] CDMaterials: {cd}");
			Log.Info($"[gmod mdl] Textures: {tx}");
		}
	}

	private static void ReadAndLogModelsAndMeshes(string mdlPath)
	{
		using var fs = System.IO.File.OpenRead(mdlPath);
		using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);

		// Skip to bodypart fields as before
		br.ReadBytes(4 + 4 + 4); // id, version, checksum
		br.ReadBytes(64); // name
		br.ReadInt32(); // length
		br.ReadBytes(sizeof(float) * 18);
		br.ReadInt32(); // flags
		int numBones = br.ReadInt32();
		int boneIndex = br.ReadInt32();
		for (int i = 0; i < 17; i++) br.ReadInt32();
		int numBodyParts = br.ReadInt32();
		int bodyPartIndex = br.ReadInt32();
		if (numBodyParts <= 0 || bodyPartIndex <= 0) return;

		Log.Info($"[gmod mdl] Models/Meshes:");
		for (int i = 0; i < numBodyParts; i++)
		{
			long bodyPartPos = bodyPartIndex + i * 16;
			fs.Seek(bodyPartPos, SeekOrigin.Begin);
			int bpNameIndex = br.ReadInt32();
			int numModels = br.ReadInt32();
			int bpBase = br.ReadInt32();
			int modelIndex = br.ReadInt32();
			string bpName = ReadCString(fs, br, bodyPartPos + bpNameIndex);
			if (string.IsNullOrWhiteSpace(bpName)) bpName = $"bodypart_{i}";

			for (int j = 0; j < numModels; j++)
			{
				long modelPos = bodyPartPos + modelIndex + j * 148; // approximate stride
				fs.Seek(modelPos, SeekOrigin.Begin);
				string modelName = ReadFixedCString(br, 64);
				int mType = br.ReadInt32();
				float mRadius = br.ReadSingle();
				int meshCount = br.ReadInt32();
				int meshOffset = br.ReadInt32();
				int numVertices = br.ReadInt32();
				int vertexIndex = br.ReadInt32();
				if (string.IsNullOrWhiteSpace(modelName)) modelName = $"model_{j}";
				Log.Info($"[gmod mdl]   [{bpName}] model '{modelName}' verts={numVertices} meshes={meshCount}");

				if (meshCount > 0 && meshOffset > 0)
				{
					for (int k = 0; k < meshCount; k++)
					{
						long meshPos = modelPos + meshOffset + k * 56; // approximate stride
						fs.Seek(meshPos, SeekOrigin.Begin);
						int mat = br.ReadInt32();
						int modelIdxBack = br.ReadInt32();
						int meshVerts = br.ReadInt32();
						int meshVertexOffset = br.ReadInt32();
						Log.Info($"[gmod mdl]       mesh#{k} verts={meshVerts} vtxOffset={meshVertexOffset} mat={mat}");
					}
				}
			}
		}
	}

	private static string ReadZString(BinaryReader br)
	{
		var bytes = new List<byte>(64);
		for (;;)
		{
			byte b = br.ReadByte();
			if (b == 0) break;
			bytes.Add(b);
		}
		return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
	}

	// Replace characters that may be invalid or problematic in runtime resource names
	private static string Sanitize(string s)
	{
		if (string.IsNullOrWhiteSpace(s)) return "unnamed";
		var chars = new char[s.Length];
		int j = 0;
		foreach (var ch in s)
		{
			chars[j++] = (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') ? ch : '_';
		}
		return new string(chars, 0, j);
	}

	private static List<string> BuildVmtCandidates(string mdlPath, List<string> cdpaths, string texName)
	{
		var list = new List<string>();
		string norm = texName.Replace('\\','/');
		string derivedModelsDir = null;
		try
		{
			var lower = mdlPath.Replace('\\','/');
			int at = lower.LastIndexOf("/models/", StringComparison.OrdinalIgnoreCase);
			if (at >= 0)
			{
				var after = lower.Substring(at + "/models/".Length);
				int slash = after.LastIndexOf('/');
				if (slash > 0)
				{
					derivedModelsDir = "models/" + after.Substring(0, slash);
				}
			}
		}
		catch { }
		if (norm.Contains('/'))
		{
			list.Add(norm);
		}
		else
		{
			if (!string.IsNullOrWhiteSpace(derivedModelsDir)) list.Add(derivedModelsDir + "/" + norm);
			foreach (var cd in cdpaths) if (!string.IsNullOrWhiteSpace(cd)) list.Add(cd.Replace('\\','/') + "/" + norm);
			list.Add(norm);
		}
		return list;
	}

	private static int BuildAllMeshesLOD0(string vtxPath, SourceVvd.Data vvdData, string mdlPath, ModelBuilder builder, GModMount host)
	{
		if (string.IsNullOrEmpty(vtxPath) || vvdData == null) return 0;
		int added = 0;
		// Get hierarchy to know mesh counts per bodypart/model
		var h = SourceVtx.ParseHierarchy(vtxPath);
		// Derive starts from VTX usage to avoid MDL stride issues
		var starts = ComputeStartsFromVtx(vtxPath, h);
		// Read bodygroup info so we can ensure all choices exist in the final model
		var bodygroups = ReadBodygroupInfos(mdlPath);
		var addedChoices = new Dictionary<string, HashSet<int>>();
		// MDL-derived bases like Crowbar uses
		var modelVertexBases = ComputeModelVertexBases(mdlPath);
		var meshVertexOffsets = ComputeMeshVertexOffsets(mdlPath);
		var bodyPartVertexStarts = ComputeBodyPartVertexStarts(mdlPath);
		for (int bp = 0; bp < h.BodyParts.Count; bp++)
		{
			var bpInfo = h.BodyParts[bp];
			for (int m = 0; m < bpInfo.Models.Count; m++)
			{
				var modelInfo = bpInfo.Models[m];
				int meshCount = (modelInfo.Lods.Count > 0) ? modelInfo.Lods[0].MeshCount : 0;
				if (meshCount <= 0) continue;
				for (int me = 0; me < meshCount; me++)
				{
					var origIndices = SourceVtx.ReadLod0MeshOriginalIndices(vtxPath, bp, m, me);
					if (origIndices == null || origIndices.Count < 3) continue;
					Log.Info($"[gmod mdl uv] mapping starts: bp={bp} m={m} me={me} bodyStart={starts.BodyPartStart[bp]} meshStart={starts.MeshStart[bp][m][me]} count={origIndices.Count}");

					// Defer mesh/material creation until we compute a unique per-choice label
					var outVerts = new List<SourceSkinnedVertex>();
					var outIndices = new List<int>();
					var vertMap = new Dictionary<int,int>();
					for (int i = 0; i < origIndices.Count; i++)
					{
						int orig = starts.BodyPartStart[bp] + starts.ModelStart[bp][m] + starts.MeshStart[bp][m][me] + Math.Max(0, origIndices[i]);
						if (!vertMap.TryGetValue(orig, out int outIdx))
						{
							if (orig < 0 || orig >= vvdData.Vertices.Count) continue;
							var v = vvdData.Vertices[orig];
							// Map up to 4 bone weights to 0-255 range and pack indices/weights
							byte i0 = v.Bones.Length > 0 ? v.Bones[0] : (byte)0;
							byte i1 = v.Bones.Length > 1 ? v.Bones[1] : (byte)0;
							byte i2 = v.Bones.Length > 2 ? v.Bones[2] : (byte)0;
							byte i3 = (byte)0;
							float w0f = v.Weights.Length > 0 ? v.Weights[0] : 1f;
							float w1f = v.Weights.Length > 1 ? v.Weights[1] : 0f;
							float w2f = v.Weights.Length > 2 ? v.Weights[2] : 0f;
							float w3f = 0f;
							int w0 = (int)(w0f * 255f + 0.5f);
							int w1 = (int)(w1f * 255f + 0.5f);
							int w2 = (int)(w2f * 255f + 0.5f);
							int w3 = (int)(w3f * 255f + 0.5f);
							int sum = w0 + w1 + w2 + w3;
							if (sum != 255)
							{
								int diff = 255 - sum;
								int mx = Math.Max(Math.Max(w0, w1), Math.Max(w2, w3));
								if (w0 == mx) w0 += diff; else if (w1 == mx) w1 += diff; else if (w2 == mx) w2 += diff; else w3 += diff;
							}

							if (outVerts.Count == 0)
							{
								Log.Info($"[gmod mdl] skin sample bp={bp} m={m} me={me} idx={orig} bones=[{i0},{i1},{i2},{i3}] weights=[{w0},{w1},{w2},{w3}] sum={w0+w1+w2+w3}");
							}
							float u = v.UV.x;
							float vv = FlipV ? (1f - v.UV.y) : v.UV.y;
							if (ClampUVs)
							{
								u = System.Math.Clamp(u, 0f, 1f);
								vv = System.Math.Clamp(vv, 0f, 1f);
							}
							// Compute tangent space (approx) so sampling isn't broken by missing tangents
							Vector4 t;
							if (v.Tangent.x != 0f || v.Tangent.y != 0f || v.Tangent.z != 0f)
							{
								t = new Vector4(v.Tangent.x, v.Tangent.y, v.Tangent.z, (v.TangentW == 0f) ? 1f : v.TangentW);
							}
							else
							{
								t = new Vector4(1f, 0f, 0f, 1f);
							}
							var sv = new SourceSkinnedVertex(
								new Vector3(v.Position.x, v.Position.y, v.Position.z),
								new Vector3(v.Normal.x, v.Normal.y, v.Normal.z),
								t,
								new Vector2(u, vv),
								new Color32(i0,i1,i2,i3),
								new Color32((byte)w0,(byte)w1,(byte)w2,(byte)w3)
							);
							if (outVerts.Count < 5)
							{
								Log.Info($"[gmod mdl uv] sample bp={bp} m={m} me={me} origIdx={origIndices[i]} mapped={orig} vvdUV=({v.UV.x:F3},{v.UV.y:F3}) uv=({u:F3},{vv:F3}) flipV={FlipV}");
							}
							outIdx = outVerts.Count;
							outVerts.Add(sv);
							vertMap[orig] = outIdx;
						}
						outIndices.Add(outIdx);
					}

					if (outIndices.Count < 3)
						continue;
					int rag = outIndices.Count % 3;
					if (rag != 0)
						outIndices.RemoveRange(outIndices.Count - rag, rag);
					if (outIndices.Count < 3)
						continue;
					// Flip winding
					for (int t = 0; t + 2 < outIndices.Count; t += 3)
					{
						int tmp = outIndices[t + 1];
						outIndices[t + 1] = outIndices[t + 2];
						outIndices[t + 2] = tmp;
					}
					var (groupName, modelChoice) = GetBodypartAndModelName(mdlPath, bp, m);
					// Pass each mesh as its own bodygroup entry name so the dropdown shows Submodel k
					string displayGroup = string.IsNullOrWhiteSpace(groupName) ? $"bodypart_{bp}" : groupName;
					// Use the actual model choice index for this bodygroup
					int choiceIndex = modelChoice;
					// Name the mesh using group and choice to ensure unique labels in tools
					string meshName = $"Submodel {choiceIndex}";
					int matIndex = SourceVtx.ReadLod0MeshMaterialIndex(vtxPath, bp, m, me);
					var material = CreateMaterialForMesh(host, mdlPath, vtxPath, bp, m, me, meshName, matIndex);
					if (material == null)
					{
						material = Material.Create(meshName, "shaders/VertexLitGeneric2.shader");
						material.Set("BaseTexture", Texture.White);
					}
					else
					{
						// Log what material we resolved and ensure the texture param gets applied
						Log.Info($"[gmod mdl mat] Using material for mesh '{meshName}'");
					}
					var outMesh = new Mesh(meshName, material);
					outMesh.CreateVertexBuffer(outVerts.Count, SourceSkinnedVertex.Layout, outVerts);
					outMesh.CreateIndexBuffer(outIndices.Count, outIndices.ToArray());
					outMesh.Bounds = BBox.FromPoints(outVerts.ConvertAll(v => v.position));
					builder.AddMesh(outMesh, 0, displayGroup, choiceIndex);
					if (!addedChoices.TryGetValue(displayGroup, out var set))
					{
						set = new HashSet<int>();
						addedChoices[displayGroup] = set;
					}
					set.Add(choiceIndex);
					added++;
					if (added == 1)
					{
						Log.Info($"[gmod mdl] Built LOD0 mesh for bodypart {bp} ('{displayGroup}') model {m} (choice {choiceIndex}) mesh {me} tris={outIndices.Count/3}");
					}
				}
			}
		}

		// Maintain prior behavior: ensure at least NumModels choices per bodygroup
		foreach (var bg in bodygroups)
		{
			if (!addedChoices.TryGetValue(bg.Name, out var set)) set = new HashSet<int>();
			for (int c = 0; c < Math.Max(2, bg.NumModels); c++)
			{
				if (set.Contains(c)) continue;
				var empty = CreateEmptyMesh($"Submodel {c}");
				// Give placeholder meshes a unique name per group/choice so they don't all appear as 'mesh'
				var emptyMat = Material.Create($"gmod_{Sanitize(System.IO.Path.GetFileNameWithoutExtension(mdlPath))}_empty_{Sanitize(bg.Name)}_c{c}", "shaders/VertexLitGeneric2.shader");
				emptyMat.Set("BaseTexture", Texture.White);
				emptyMat.Set("Roughness", GetWhite1x1());
				emptyMat.Set("UseRoughnessValue", true);
				emptyMat.Set("RoughnessValue", 0.2f);
				emptyMat.Set("OpacityMap", GetWhite1x1());
				emptyMat.Set("g_bUseRoughnessValue", true);
				emptyMat.Set("g_flRoughnessValue", 0.2f);
				emptyMat.Set("BaseTextureMask", GetWhite1x1());
				emptyMat.Set("DetailTexture", GetWhite1x1());
				emptyMat.Set("DetailMask", GetWhite1x1());
				emptyMat.Set("LightWarpTexture", GetGray1x1());
				emptyMat.Set("SpecularWarpTexture", GetWhite1x1());
				empty.Material = emptyMat;
				builder.AddMesh(empty, 0, bg.Name, c);
				Log.Info($"[gmod mdl] Added empty bodygroup choice '{bg.Name}' #{c}");
			}
		}
		Log.Info($"[gmod mdl] Added {added} meshes for LOD0 across {h.BodyParts.Count} bodyparts");
		return added;
	}

	private sealed class VtxStarts
	{
		public List<int> BodyPartStart = new();
		public List<List<int>> ModelStart = new();
		public List<List<List<int>>> MeshStart = new();
	}

	// Compute starts from MDL: bodypart/model accumulations and per-mesh vertexIndexStart
	private static VtxStarts ComputeStartsFromMdl(string mdlPath, SourceVtx.Hierarchy h)
	{
		var starts = new VtxStarts();
		try
		{
			using var fs = System.IO.File.OpenRead(mdlPath);
			using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
			br.ReadBytes(4 + 4 + 4); // id, version, checksum
			br.ReadBytes(64); // name
			br.ReadInt32(); // length
			br.ReadBytes(sizeof(float) * 18);
			br.ReadInt32(); // flags
			br.ReadInt32(); // numbones
			br.ReadInt32(); // boneindex
			for (int i = 0; i < 17; i++) br.ReadInt32();
			int numBodyParts = br.ReadInt32();
			int bodyPartIndex = br.ReadInt32();
			int bodyAccum = 0;
			for (int bp = 0; bp < Math.Min(numBodyParts, h.BodyParts.Count); bp++)
			{
				starts.BodyPartStart.Add(bodyAccum);
				long bodyPartPos = bodyPartIndex + bp * 16;
				fs.Seek(bodyPartPos, SeekOrigin.Begin);
				br.ReadInt32(); // nameindex
				int numModels = br.ReadInt32();
				br.ReadInt32(); // base
				int modelIndex = br.ReadInt32();
				var modelStarts = new List<int>();
				var meshStartsForModels = new List<List<int>>();
				int modelAccum = 0;
				for (int m = 0; m < numModels && m < h.BodyParts[bp].Models.Count; m++)
				{
					modelStarts.Add(modelAccum);
					long modelPos = bodyPartPos + modelIndex + m * 148; // approx stride
					fs.Seek(modelPos, SeekOrigin.Begin);
					br.ReadBytes(64); // name
					br.ReadInt32(); // type
					br.ReadSingle(); // radius
					int meshCount = br.ReadInt32();
					int meshOffset = br.ReadInt32();
					int numVertices = br.ReadInt32();
					br.ReadInt32(); // vertexindex
					var meshStarts = new List<int>();
					for (int me = 0; me < meshCount; me++)
					{
						long meshPos = modelPos + meshOffset + me * 56; // approx stride
						fs.Seek(meshPos, SeekOrigin.Begin);
						br.ReadInt32(); // material
						br.ReadInt32(); // modelindex backref
						int meshVerts = br.ReadInt32();
						int meshVertexOffset = br.ReadInt32();
						meshStarts.Add(meshVertexOffset);
					}
					meshStartsForModels.Add(meshStarts);
					modelAccum += Math.Max(0, numVertices);
				}
				starts.ModelStart.Add(modelStarts);
				starts.MeshStart.Add(meshStartsForModels);
				bodyAccum += modelAccum;
			}
		}
		catch { }
		return starts;
	}

	private static VtxStarts ComputeStartsFromVtx(string vtxPath, SourceVtx.Hierarchy h)
	{
		var starts = new VtxStarts();
		int global = 0;
		for (int bp = 0; bp < h.BodyParts.Count; bp++)
		{
			starts.BodyPartStart.Add(global);
			var modelStarts = new List<int>();
			var meshStartsForModels = new List<List<int>>();
			int bodyAccum = 0;
			var bpInfo = h.BodyParts[bp];
			for (int m = 0; m < bpInfo.Models.Count; m++)
			{
				modelStarts.Add(bodyAccum);
				var meshStarts = new List<int>();
				int modelAccum = 0;
				int meshCount = (bpInfo.Models[m].Lods.Count > 0) ? bpInfo.Models[m].Lods[0].MeshCount : 0;
				for (int me = 0; me < meshCount; me++)
				{
					meshStarts.Add(modelAccum);
					var idx = SourceVtx.ReadLod0MeshOriginalIndices(vtxPath, bp, m, me);
					int meshVertCount = 0;
					if (idx != null && idx.Count > 0)
					{
						int maxOrig = 0;
						for (int i = 0; i < idx.Count; i++)
						{
							int v = Math.Max(0, idx[i]);
							if (v > maxOrig) maxOrig = v;
						}
						meshVertCount = maxOrig + 1;
					}
					modelAccum += Math.Max(0, meshVertCount);
				}
				meshStartsForModels.Add(meshStarts);
				bodyAccum += modelAccum;
			}
			starts.ModelStart.Add(modelStarts);
			starts.MeshStart.Add(meshStartsForModels);
			global += bodyAccum;
		}
		return starts;
	}

	private sealed class BodygroupInfo { public string Name; public int NumModels; }

	private static List<BodygroupInfo> ReadBodygroupInfos(string mdlPath)
	{
		var list = new List<BodygroupInfo>();
		try
		{
			using var fs = System.IO.File.OpenRead(mdlPath);
			using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
			br.ReadBytes(4 + 4 + 4);
			br.ReadBytes(64);
			br.ReadInt32();
			br.ReadBytes(sizeof(float) * 18);
			br.ReadInt32(); // flags
			br.ReadInt32(); // numbones
			br.ReadInt32(); // boneindex
			for (int i = 0; i < 17; i++) br.ReadInt32();
			int numBodyParts = br.ReadInt32();
			int bodyPartIndex = br.ReadInt32();
			for (int i = 0; i < numBodyParts; i++)
			{
				long bodyPartPos = bodyPartIndex + i * 16;
				fs.Seek(bodyPartPos, SeekOrigin.Begin);
				int bpNameIndex = br.ReadInt32();
				int numModels = br.ReadInt32();
				br.ReadInt32(); // bpBase
				int modelIndex = br.ReadInt32();
				string name = ReadCString(fs, br, bodyPartPos + bpNameIndex);
				if (string.IsNullOrWhiteSpace(name)) name = $"bodypart_{i}";
				list.Add(new BodygroupInfo { Name = name, NumModels = Math.Max(1, numModels) });
			}
		}
		catch { }
		return list;
	}

	private static Mesh CreateEmptyMesh(string name)
	{
		var material = Material.Create(name, "shaders/VertexLitGeneric2.shader");
		material.Set("BaseTexture", Texture.White);
		var mesh = new Mesh(name, material, MeshPrimitiveType.Triangles);
		var verts = new List<SimpleVertex>
		{
			new SimpleVertex(Vector3.Zero, Vector3.Up, Vector3.Zero, Vector2.Zero)
		};
		mesh.CreateVertexBuffer(1, SimpleVertex.Layout, verts);
		mesh.CreateIndexBuffer(3, new int[]{0,0,0});
		mesh.Bounds = BBox.FromPositionAndSize(Vector3.Zero, new Vector3(0.0001f,0.0001f,0.0001f));
		return mesh;
	}

	private static Material CreateMaterialForMesh(GModMount host, string mdlPath, string vtxPath, int bodyPartIndex, int modelIndex, int meshIndex, string meshName, int matIndex)
	{
		List<string> cdpaths = new();
		List<string> textures = new();
		int numtextures = 0;
		int textureindex = 0;
		int numcdtextures = 0;
		int cdtextureindex = 0;
		int numskinref = 0;
		int numskinfamilies = 0;
		int skinindex = 0;

		// Approximate target mesh characteristics from VTX to align with MDL mesh list
		int targetMeshStart = 0;
		int targetMeshVertCount = 0;
		try
		{
			var hLoc = SourceVtx.ParseHierarchy(vtxPath);
			var startsLoc = ComputeStartsFromVtx(vtxPath, hLoc);
			if (bodyPartIndex >= 0 && bodyPartIndex < startsLoc.MeshStart.Count &&
				modelIndex >= 0 && modelIndex < startsLoc.MeshStart[bodyPartIndex].Count &&
				meshIndex >= 0 && meshIndex < startsLoc.MeshStart[bodyPartIndex][modelIndex].Count)
			{
				targetMeshStart = startsLoc.MeshStart[bodyPartIndex][modelIndex][meshIndex];
			}
			var oi = SourceVtx.ReadLod0MeshOriginalIndices(vtxPath, bodyPartIndex, modelIndex, meshIndex);
			if (oi != null && oi.Count > 0)
			{
				int maxOrig = 0;
				for (int i = 0; i < oi.Count; i++) { int v = Math.Max(0, oi[i]); if (v > maxOrig) maxOrig = v; }
				targetMeshVertCount = maxOrig + 1;
			}
		}
		catch { }

		// Read MDL header bits we need + texture lists
		try
		{
			using var fsInfo = System.IO.File.OpenRead(mdlPath);
			using var brInfo = new BinaryReader(fsInfo, System.Text.Encoding.ASCII, leaveOpen: false);
			brInfo.ReadBytes(4 + 4 + 4); // id, version, checksum
			brInfo.ReadBytes(64); // name
			brInfo.ReadInt32(); // length
			brInfo.ReadBytes(sizeof(float) * 18); // eye/illum/hull/view
			brInfo.ReadInt32(); // flags
			brInfo.ReadInt32(); // numbones
			brInfo.ReadInt32(); // boneindex
			brInfo.ReadInt32(); // numbonecontrollers
			brInfo.ReadInt32(); // bonecontrollerindex
			brInfo.ReadInt32(); // numhitboxsets
			brInfo.ReadInt32(); // hitboxsetindex
			brInfo.ReadInt32(); // numlocalanim
			brInfo.ReadInt32(); // localanimindex
			brInfo.ReadInt32(); // numlocalseq
			brInfo.ReadInt32(); // localseqindex
			brInfo.ReadInt32(); // activitylistversion
			brInfo.ReadInt32(); // eventsindexed
			numtextures = brInfo.ReadInt32();
			textureindex = brInfo.ReadInt32();
			numcdtextures = brInfo.ReadInt32();
			cdtextureindex = brInfo.ReadInt32();
			numskinref = brInfo.ReadInt32();
			numskinfamilies = brInfo.ReadInt32();
			skinindex = brInfo.ReadInt32();

			for (int i = 0; i < Math.Min(numcdtextures, 16); i++)
			{
				fsInfo.Seek(cdtextureindex + i * 4, SeekOrigin.Begin);
				int off = brInfo.ReadInt32();
				string p = ReadCString(fsInfo, brInfo, cdtextureindex + off);
				if (!string.IsNullOrWhiteSpace(p)) cdpaths.Add(p.Replace('\\','/').TrimEnd('/'));
			}
			for (int i = 0; i < Math.Min(numtextures, 64); i++)
			{
				long texPos = textureindex + i * 64; // approximate hop
				fsInfo.Seek(texPos, SeekOrigin.Begin);
				int nameOffset = brInfo.ReadInt32();
				string name = ReadCString(fsInfo, brInfo, texPos + nameOffset);
				if (!string.IsNullOrWhiteSpace(name)) textures.Add(name);
			}
		}
		catch { }

		Log.Info($"[gmod mdl mat] Resolve for mesh '{meshName}': matIndex={matIndex}");
		if (textures.Count > 0)
		{
			Log.Info($"[gmod mdl mat] MDL textures list: {string.Join(", ", textures)}");
		}

		// Prefer direct MDL mesh.material -> skinref mapping
		int chosenStride;
		if (TryReadMeshMaterialSkinRef(mdlPath, bodyPartIndex, modelIndex, meshIndex, targetMeshStart, targetMeshVertCount, numskinref, numtextures, out int matRefLocal, out chosenStride, out int resolvedMeshIndex))
		{
			int textureListIndex = matRefLocal;
			// Map through skin family 0 if available
			if (numskinref > 0 && numskinfamilies > 0 && skinindex > 0)
			{
				try
				{
					using var fsSkin = System.IO.File.OpenRead(mdlPath);
					using var brSkin = new BinaryReader(fsSkin, System.Text.Encoding.ASCII, leaveOpen: false);
					// skin table layout: [family][skinref] as ushort, row size = numskinref * 2 bytes
					int rowSizeBytes = Math.Max(0, numskinref) * 2;
					if (matRefLocal >= 0 && matRefLocal < numskinref)
					{
						long offs = (long)skinindex + 0 * rowSizeBytes + (matRefLocal * 2L);
						if (offs >= 0 && offs + 2 <= fsSkin.Length)
						{
							fsSkin.Seek(offs, SeekOrigin.Begin);
							textureListIndex = brSkin.ReadUInt16();
						}
					}
				}
				catch { }
			}
			if (textureListIndex >= 0 && textureListIndex < textures.Count)
			{
				string texName = textures[textureListIndex];
				Log.Info($"[gmod mdl mat] MDL mesh.material={matRefLocal} stride={chosenStride} -> texture[{textureListIndex}]='{texName}'");
				var candidates = BuildVmtCandidates(mdlPath, cdpaths, texName);
				foreach (var cand in candidates)
				{
					if (!host.TryFindVmtByName(cand, out var vmtRel)) continue;
					using var vmtStream = host.GetFileStreamForVmt(vmtRel);
					if (vmtStream == System.IO.Stream.Null) continue;
					var vmt = GModVmt.Parse(vmtStream);
					if (vmt == null || !vmt.Kv.TryGetValue("$basetexture", out var baseTex)) continue;
					if (!host.TryResolveVtfByBasetexture(baseTex, out var vtfRel)) continue;
					var texObj = LoadVtfWithLog(host, vtfRel, "BaseTexture", meshName);
					var matName0 = BuildRuntimeMaterialName(mdlPath, vtfRel, bodyPartIndex, modelIndex, meshIndex);
					var mat = Material.Create(matName0, "shaders/VertexLitGeneric2.shader");
					SetBaseColorTexture(mat, texObj);
					// Set default textures that VertexLitGeneric2 shader expects
					mat.Set("g_tNormal", GetGray1x1()); // neutral normal
					mat.Set("g_tAmbientOcclusionTexture", GetWhite1x1());
					mat.Set("g_tDetailTexture", GetWhite1x1());
					mat.Set("g_tSelfIllumMaskTexture", GetWhite1x1());
					// Set VertexLitGeneric2 defaults
					mat.Set("g_vColorTint", new System.Numerics.Vector3(1f, 1f, 1f));
					// Specular exponent texture parsing now handled by ParseAndSetVmtTextures
					// Bind normal map if present in VMT
					string normalRelUsed0 = null;
					if (vmt.Kv.TryGetValue("$bumpmap", out var _nm) || vmt.Kv.TryGetValue("$normalmap", out _nm))
					{
						if (host.TryResolveVtfByBasetexture(_nm, out var vtfN))
						{
							var normalTex = LoadVtfWithLog(host, vtfN, "NormalMap", meshName);
							SetNormalTexture(mat, normalTex);
							normalRelUsed0 = vtfN;
							Log.Info($"[gmod mdl mat] NormalMap '{_nm}' -> '{vtfN}' applied for mesh '{meshName}'");
						}
					}
					
					// Bind light warp texture if present in VMT
					string lightWarpRelUsed0 = null;
					if (vmt.Kv.TryGetValue("$lightwarptexture", out var _lw))
					{
						if (host.TryResolveVtfByBasetexture(_lw, out var vtfLW))
						{
							var lightWarpTex = LoadVtfWithLog(host, vtfLW, "LightWarpTexture", meshName);
							if (!object.ReferenceEquals(lightWarpTex, Texture.White))
							{
								mat.Set("LightWarpTexture", lightWarpTex);
								lightWarpRelUsed0 = vtfLW;
							}
							Log.Info($"[gmod mdl mat] LightWarpTexture '{_lw}' -> '{vtfLW}' applied for mesh '{meshName}'");
						}
					}
					// VMT scalar/vector parsing
					bool phongOn0 = false;
					if (vmt.Kv.TryGetValue("$phong", out var _ph0) && IsTrue(_ph0)) { 
						phongOn0 = true; 
						//Log.warning($"[gmod mdl mat] VMT DEBUG: Found $phong = '{_ph0}' - phong should be enabled!");
					} else {
						Log.Info($"[gmod mdl mat] VMT DEBUG: No $phong found in VMT");
					}
					// VMT parameter parsing now handled by ParseAndSetVmtTextures
					if (vmt.Kv.TryGetValue("$color2", out var _c20) && TryParseVec3(_c20, out var vc20))
					{
						mat.Set("g_vColorTint", new System.Numerics.Vector3(vc20.X, vc20.Y, vc20.Z));
					}
					if (vmt.Kv.TryGetValue("$blendTintByBaseAlpha", out var _bt0) && IsTrue(_bt0))
					{
						mat.Set("g_bBlendTintByBaseAlpha", true);
					}
					// $PhongDisableHalfLambert not used in VertexLitGeneric2
					
					// Parse rimlight parameters and set dynamic combo
					bool hasRimlight = false;
					if (vmt.Kv.TryGetValue("$rimlight", out var _rl0) && IsTrue(_rl0))
					{
						hasRimlight = true;
					}
					if (vmt.Kv.TryGetValue("$rimmask", out var _rm0) && IsTrue(_rm0))
					{
						mat.Set("g_flRimMask", 1.0f);
						hasRimlight = true;
					}
					if (vmt.Kv.TryGetValue("$rimlightboost", out var _rb0) && float.TryParse(_rb0, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fboost0))
					{
						mat.Set("g_flRimBoost", fboost0);
						hasRimlight = true;
					}
					if (vmt.Kv.TryGetValue("$rimexponent", out var _re0) && float.TryParse(_re0, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fexp0))
					{
						mat.Set("g_flRimExponent", fexp0);
						hasRimlight = true;
					}
					
					// Parse phongtint parameter
					if (vmt.Kv.TryGetValue("$phongtint", out var _pt0) && TryParseVec3(_pt0, out var vpt0))
					{
						mat.Set("g_bConstantSpecularTint", false);
						mat.Set("g_vSpecularTint", new System.Numerics.Vector3(vpt0.X, vpt0.Y, vpt0.Z));
					}
					else
					{
						mat.Set("g_bConstantSpecularTint", true);
					}
					
					// Parse and load lightwarp texture from VMT
					bool hasLightwarp = false;
					if (vmt.Kv.TryGetValue("$lightwarptexture", out var _lw0))
					{
						if (host.TryResolveVtfByBasetexture(_lw0, out var vtfLW0))
						{
							var lightWarpTex = LoadVtfWithLog(host, vtfLW0, "LightWarpTexture", meshName);
							mat.Set("LightWarpTexture", lightWarpTex);
							mat.Set("g_tLightWarpTexture", lightWarpTex);
							hasLightwarp = true;
							Log.Info($"[gmod mdl mat] LightWarpTexture '{_lw0}' -> '{vtfLW0}' applied for mesh '{meshName}'");
						}
						else
						{
							Log.Warning($"[gmod mdl mat] lightwarp resolve failed for '{_lw0}'");
						}
					}
					else
					{
						// Set default neutral gray lightwarp if no texture specified
						mat.Set("LightWarpTexture", GetGray1x1());
					}
					
					// Parse and load phongwarp texture from VMT  
					bool hasPhongWarp = false;
					if (vmt.Kv.TryGetValue("$phongwarptexture", out var _pw0))
					{
						if (host.TryResolveVtfByBasetexture(_pw0, out var vtfPW0))
						{
							var phongWarpTex = LoadVtfWithLog(host, vtfPW0, "SpecularWarpTexture", meshName);
							mat.Set("SpecularWarpTexture", phongWarpTex);
							hasPhongWarp = true;
							Log.Info($"[gmod mdl mat] SpecularWarpTexture '{_pw0}' -> '{vtfPW0}' applied for mesh '{meshName}'");
						}
						else
						{
							Log.Warning($"[gmod mdl mat] phongwarp resolve failed for '{_pw0}'");
						}
					}
					else
					{
						// Set default white specular warp if no texture specified
						mat.Set("SpecularWarpTexture", GetWhite1x1());
					}
					
					// Set dynamic combos based on parsed VMT data
					try
					{
						if (mat.Attributes != null)
						{
							mat.Attributes.SetCombo("D_LIGHTWARPTEXTURE", hasLightwarp ? 1 : 0);
							mat.Attributes.SetCombo("D_BLENDTINTBYBASEALPHA", vmt.Kv.ContainsKey("$blendTintByBaseAlpha") && IsTrue(vmt.Kv["$blendTintByBaseAlpha"]) ? 1 : 0);
							mat.Attributes.SetCombo("D_PHONGWARPTEXTURE", hasPhongWarp ? 1 : 0);
							mat.Attributes.SetCombo("D_RIMLIGHT", hasRimlight ? 1 : 0);
							Log.Info($"[gmod mdl mat] Set dynamic combos: lightwarp={hasLightwarp}, blendtint={vmt.Kv.ContainsKey("$blendTintByBaseAlpha")}, phongwarp={hasPhongWarp}, rimlight={hasRimlight}");
						}
					}
					catch (Exception ex)
					{
						Log.Warning($"[gmod mdl mat] Failed to set dynamic combos: {ex.Message}");
					}
					
					// Parse VMT parameters (specular, textures, etc.)
					ParseAndSetVmtTextures(mat, vmt, host, meshName);
					
					EnsureDefaultPackedMaps(mat);
					
					// Apply phong parameters if phong is enabled in VMT
					if (phongOn0)
					{
						Log.Info($"[gmod mdl mat] Setting phong parameters: phong enabled in VMT");
						Log.Info($"[gmod mdl mat] Phong enabled from VMT");
					}
					
					// Comprehensive debug logging
					var vmtDebugData = new Dictionary<string, object>();
					if (vmt.Kv.TryGetValue("$color2", out var color2)) vmtDebugData["$color2"] = color2;
					if (vmt.Kv.TryGetValue("$blendTintByBaseAlpha", out var blendTint)) vmtDebugData["$blendTintByBaseAlpha"] = blendTint;
					if (vmt.Kv.TryGetValue("$phong", out var phong)) vmtDebugData["$phong"] = phong;
					if (vmt.Kv.TryGetValue("$lightwarptexture", out var lightWarpTexDebug)) vmtDebugData["$lightwarptexture"] = lightWarpTexDebug;
					LogComprehensiveMaterialDebug("MESH", meshName, mat, vmtDebugData);
					
					DumpMaterialTextures("MESH", meshName, mat);
					LogMaterialDebug("MESH", meshName, vtfRel, normalRelUsed0, null, lightWarpRelUsed0, mat);
					LogMaterialSpecularDebug("MESH", meshName, mat);
					Log.Info($"[gmod mdl mat] material created for mesh '{meshName}' (from VMT '{cand}', VTF '{vtfRel}')");
					return mat;
				}
			}
		}

		// MDL-based mapping: mesh.material (optionally through skin family 0)
		try
		{
			int[] skinMap = null;
			if (numskinref > 0 && numskinfamilies > 0 && skinindex > 0)
			{
				using var fsSkin = System.IO.File.OpenRead(mdlPath);
				using var brSkin = new BinaryReader(fsSkin, System.Text.Encoding.ASCII, leaveOpen: false);
				fsSkin.Seek(skinindex, SeekOrigin.Begin);
				skinMap = new int[numskinref];
				for (int i = 0; i < numskinref; i++) skinMap[i] = brSkin.ReadUInt16();
			}

			using var fsMdl = System.IO.File.OpenRead(mdlPath);
			using var brMdl = new BinaryReader(fsMdl, System.Text.Encoding.ASCII, leaveOpen: false);
			brMdl.ReadBytes(4 + 4 + 4); // id, version, checksum
			brMdl.ReadBytes(64); // name
			brMdl.ReadInt32(); // length
			brMdl.ReadBytes(sizeof(float) * 18);
			brMdl.ReadInt32(); // flags
			brMdl.ReadInt32(); // numbones
			brMdl.ReadInt32(); // boneindex
			for (int i = 0; i < 17; i++) brMdl.ReadInt32();
			int numBodyParts2 = brMdl.ReadInt32();
			int bodyPartIndex2 = brMdl.ReadInt32();
			if (bodyPartIndex >= 0 && bodyPartIndex < numBodyParts2 && bodyPartIndex2 > 0)
			{
				long bodyPartPos = bodyPartIndex2 + bodyPartIndex * 16;
				fsMdl.Seek(bodyPartPos, SeekOrigin.Begin);
				int bpNameIndex = brMdl.ReadInt32();
				int numModels = brMdl.ReadInt32();
				brMdl.ReadInt32(); // bpBase
				int modelIndexOffset = brMdl.ReadInt32();
				if (modelIndex >= 0 && modelIndex < numModels)
				{
					const int modelStride = 148; // v48 typical
					long modelPos = bodyPartPos + modelIndexOffset + modelIndex * modelStride;
					fsMdl.Seek(modelPos, SeekOrigin.Begin);
					brMdl.ReadBytes(64); // model name
					brMdl.ReadInt32(); // type
					brMdl.ReadSingle(); // radius
					int meshCount = brMdl.ReadInt32();
					int meshOffset = brMdl.ReadInt32();
					int modelNumVertices = brMdl.ReadInt32();
					brMdl.ReadInt32(); // vertexindex
					if (meshIndex >= 0 && meshIndex < meshCount && meshOffset > 0)
					{
						int[] strideCandidates = new[] { 56, 64, 68, 72, 76, 80, 84, 88, 92, 96, 104, 112 };
						for (int si = 0; si < strideCandidates.Length; si++)
						{
							int stride = strideCandidates[si];
							long meshPos = modelPos + meshOffset + meshIndex * stride;
							fsMdl.Seek(meshPos, SeekOrigin.Begin);
							int matCandidate = brMdl.ReadInt32();
							brMdl.ReadInt32(); // model backref
							int meshVerts = brMdl.ReadInt32();
							int meshVertexOff = brMdl.ReadInt32();
							bool vertsOk = (meshVerts >= 0 && meshVerts <= Math.Max(0, modelNumVertices)) && (meshVertexOff >= 0 && meshVertexOff <= Math.Max(0, modelNumVertices));
							if (!vertsOk) continue;

							int textureListIndex = matCandidate;
							if (skinMap != null && matCandidate >= 0 && matCandidate < skinMap.Length)
							{
								textureListIndex = skinMap[matCandidate];
							}
							if (textureListIndex >= 0 && textureListIndex < textures.Count)
							{
								string texName = textures[textureListIndex];
								Log.Info($"[gmod mdl mat] MDL map: mesh material={matCandidate} -> texture[{textureListIndex}]='{texName}' (stride={stride})");
								var candidates = BuildVmtCandidates(mdlPath, cdpaths, texName);

								foreach (var cand in candidates)
								{
									if (!host.TryFindVmtByName(cand, out var vmtRel)) continue;
									using var vmtStream = host.GetFileStreamForVmt(vmtRel);
									if (vmtStream == System.IO.Stream.Null) { Log.Info($"[gmod mdl mat] (MDL) VMT stream failed for '{vmtRel}'"); continue; }
									var vmt = GModVmt.Parse(vmtStream);
									if (vmt == null || !vmt.Kv.TryGetValue("$basetexture", out var baseTex)) { Log.Info($"[gmod mdl mat] (MDL) VMT '{vmtRel}' missing $basetexture"); continue; }
									if (!host.TryResolveVtfByBasetexture(baseTex, out var vtfRel)) { Log.Info($"[gmod mdl mat] (MDL) baseTex '{baseTex}' could not be resolved to VTF"); continue; }
									var texObj = LoadVtfWithLog(host, vtfRel, "BaseTexture", meshName);
									var matName1 = BuildRuntimeMaterialName(mdlPath, vtfRel, bodyPartIndex, modelIndex, meshIndex);
									var mat = Material.Create(matName1, "shaders/VertexLitGeneric2.shader");
									SetBaseColorTexture(mat, texObj);
									mat.Set("BaseTextureMask", GetWhite1x1());
									mat.Set("AmbientOcclusion", GetWhite1x1());
									mat.Set("Metalness", GetWhite1x1());
									mat.Set("Roughness", GetWhite1x1());
									mat.Set("OpacityMap", GetWhite1x1());
									mat.Set("DetailTexture", GetWhite1x1());
									mat.Set("DetailMask", GetWhite1x1());
							// Parse textures from VMT - defaults will be set if not found
							ParseAndSetVmtTextures(mat, vmt, host, meshName);
							// Set VertexLitGeneric2 defaults
							mat.Set("g_vColorTint", new System.Numerics.Vector3(1f, 1f, 1f));
									// Specular exponent texture parsing now handled by ParseAndSetVmtTextures
									// Bind normal map if present in VMT
									string normalRelUsed1 = null;
									if (vmt.Kv.TryGetValue("$bumpmap", out var _nm2) || vmt.Kv.TryGetValue("$normalmap", out _nm2))
									{
										if (host.TryResolveVtfByBasetexture(_nm2, out var vtfN2))
										{
											var normalTex2 = LoadVtfWithLog(host, vtfN2, "NormalMap", meshName);
											SetNormalTexture(mat, normalTex2);
											normalRelUsed1 = vtfN2;
											Log.Info($"[gmod mdl mat] NormalMap '{_nm2}' -> '{vtfN2}' applied for mesh '{meshName}'");
										}
									}
									
									// Bind light warp texture if present in VMT
									string lightWarpRelUsed1 = null;
									if (vmt.Kv.TryGetValue("$lightwarptexture", out var _lw2))
									{
										if (host.TryResolveVtfByBasetexture(_lw2, out var vtfLW2))
										{
											var lightWarpTex2 = LoadVtfWithLog(host, vtfLW2, "LightWarpTexture", meshName);
											if (!object.ReferenceEquals(lightWarpTex2, Texture.White))
											{
												mat.Set("LightWarpTexture", lightWarpTex2);
												lightWarpRelUsed1 = vtfLW2;
											}
											Log.Info($"[gmod mdl mat] LightWarpTexture '{_lw2}' -> '{vtfLW2}' applied for mesh '{meshName}'");
										}
									}
									
									// VMT scalar/vector parsing (same as first path)
									bool phongOn1 = false;
									if (vmt.Kv.TryGetValue("$phong", out var _ph1) && IsTrue(_ph1)) { phongOn1 = true; }
									// All VMT parameter parsing now handled by ParseAndSetVmtTextures
									if (vmt.Kv.TryGetValue("$blendTintByBaseAlpha", out var _bt1) && IsTrue(_bt1))
									{
										SetBoolParam(mat, "g_bBlendTintByBaseAlpha", true);
									}
									// $PhongDisableHalfLambert not used in VertexLitGeneric2
									if (phongOn1)
									{
										// VMT parameters now handled by ParseAndSetVmtTextures
										
										Log.Info($"[gmod mdl mat] Phong enabled from VMT");
									}
									
									EnsureDefaultPackedMaps(mat);
									
									// Default textures already set by EnsureDefaultPackedMaps()
									DumpMaterialTextures("MDL", meshName, mat);
									LogMaterialDebug("MDL", meshName, vtfRel, normalRelUsed1, null, lightWarpRelUsed1, mat);
									Log.Info($"[gmod mdl mat] (MDL) material created for mesh '{meshName}' (from VMT '{cand}', VTF '{vtfRel}')");
									return mat;
								}
							}
						}
					}
				}
			}
		}
		catch { }

		// Fallback: VTX-provided material index mapping
		if (matIndex >= 0 && matIndex < textures.Count)
		{
			string texName = textures[matIndex];
			if (host.TryFindVmtByName(texName, out var vmtRel))
			{
				using var vmtStream = host.GetFileStreamForVmt(vmtRel);
				if (vmtStream != System.IO.Stream.Null)
				{
					var vmt = GModVmt.Parse(vmtStream);
					if (vmt != null && vmt.Kv.TryGetValue("$basetexture", out var baseTex))
					{
						if (host.TryResolveVtfByBasetexture(baseTex, out var vtfRel))
						{
							var texObj = LoadVtfWithLog(host, vtfRel, "BaseTexture", meshName);
							var matName2 = BuildRuntimeMaterialName(mdlPath, vtfRel, bodyPartIndex, modelIndex, meshIndex);
							var mat = Material.Create(matName2, "shaders/VertexLitGeneric2.shader");
							SetBaseColorTexture(mat, texObj);
							// Parse textures from VMT - defaults will be set if not found
							ParseAndSetVmtTextures(mat, vmt, host, meshName);
							// Set VertexLitGeneric2 defaults
							mat.Set("g_vColorTint", new System.Numerics.Vector3(1f, 1f, 1f));
							// Specular exponent texture parsing now handled by ParseAndSetVmtTextures
							// Bind normal map if present in VMT
							string normalRelUsed2 = null;
							if (vmt.Kv.TryGetValue("$bumpmap", out var _nm3) || vmt.Kv.TryGetValue("$normalmap", out _nm3))
							{
								if (host.TryResolveVtfByBasetexture(_nm3, out var vtfN3))
								{
									var normalTex3 = LoadVtfWithLog(host, vtfN3, "NormalMap", meshName);
									SetNormalTexture(mat, normalTex3);
									normalRelUsed2 = vtfN3;
									Log.Info($"[gmod mdl mat] NormalMap '{_nm3}' -> '{vtfN3}' applied for mesh '{meshName}'");
								}
							}
							
							// Bind light warp texture if present in VMT
							string lightWarpRelUsed2 = null;
							if (vmt.Kv.TryGetValue("$lightwarptexture", out var _lw3))
							{
								if (host.TryResolveVtfByBasetexture(_lw3, out var vtfLW3))
								{
									var lightWarpTex3 = LoadVtfWithLog(host, vtfLW3, "LightWarpTexture", meshName);
									if (!object.ReferenceEquals(lightWarpTex3, Texture.White))
									{
										mat.Set("LightWarpTexture", lightWarpTex3);
										lightWarpRelUsed2 = vtfLW3;
									}
									Log.Info($"[gmod mdl mat] LightWarpTexture '{_lw3}' -> '{vtfLW3}' applied for mesh '{meshName}'");
								}
							}
							
							// VMT scalar/vector parsing (same as first path)
							bool phongOn2 = false;
							if (vmt.Kv.TryGetValue("$phong", out var _ph2) && IsTrue(_ph2)) { phongOn2 = true; }
							// All VMT parameter parsing now handled by ParseAndSetVmtTextures
							if (vmt.Kv.TryGetValue("$blendTintByBaseAlpha", out var _bt2) && IsTrue(_bt2))
							{
								SetBoolParam(mat, "g_bBlendTintByBaseAlpha", true);
							}
							// $PhongDisableHalfLambert not used in VertexLitGeneric2
							if (phongOn2)
							{
								// VMT parameters now handled by ParseAndSetVmtTextures
								
								Log.Info($"[gmod mdl mat] Phong enabled from VMT");
							}
							
							EnsureDefaultPackedMaps(mat);
							
							
							// Default textures already set by EnsureDefaultPackedMaps()
							DumpMaterialTextures("VTX", meshName, mat);
							LogMaterialDebug("VTX", meshName, vtfRel, normalRelUsed2, null, lightWarpRelUsed2, mat);
							Log.Info($"[gmod mdl mat] (VTX) material created for mesh '{meshName}' (from VMT '{texName}', VTF '{vtfRel}')");
							return mat;
						}
						else
						{
							Log.Info($"[gmod mdl mat] baseTex '{baseTex}' could not be resolved to VTF");
						}
					}
					else
					{
						Log.Info($"[gmod mdl mat] VMT '{vmtRel}' missing $basetexture");
					}
				}
				else
				{
					Log.Info($"[gmod mdl mat] Failed to open VMT stream '{vmtRel}'");
				}
			}
			else
			{
				Log.Info($"[gmod mdl mat] No VMT found by name '{texName}'");
			}
		}

		// Final fallback: scan textures list
		foreach (var tex in textures)
		{
			if (host.TryFindVmtByName(tex, out var vmtRel))
			{
				using var vmtStream = host.GetFileStreamForVmt(vmtRel);
				if (vmtStream != System.IO.Stream.Null)
				{
					var vmt = GModVmt.Parse(vmtStream);
					if (vmt != null && vmt.Kv.TryGetValue("$basetexture", out var baseTex))
					{
						if (host.TryResolveVtfByBasetexture(baseTex, out var vtfRel))
						{
							var texObj = LoadVtfWithLog(host, vtfRel, "BaseTexture", meshName);
							var matName3 = BuildRuntimeMaterialName(mdlPath, vtfRel, bodyPartIndex, modelIndex, meshIndex);
							var mat = Material.Create(matName3, "shaders/VertexLitGeneric2.shader");
							SetBaseColorTexture(mat, texObj);
							// Parse textures from VMT - defaults will be set if not found
							ParseAndSetVmtTextures(mat, vmt, host, meshName);
							// Set VertexLitGeneric2 defaults
							mat.Set("g_vColorTint", new System.Numerics.Vector3(1f, 1f, 1f));
							// Specular exponent texture parsing now handled by ParseAndSetVmtTextures
							// Bind normal map if present in VMT
							string normalRelUsed3 = null;
							if (vmt.Kv.TryGetValue("$bumpmap", out var _nm4) || vmt.Kv.TryGetValue("$normalmap", out _nm4))
							{
								if (host.TryResolveVtfByBasetexture(_nm4, out var vtfN4))
								{
									var normalTex4 = LoadVtfWithLog(host, vtfN4, "NormalMap", meshName);
									SetNormalTexture(mat, normalTex4);
									normalRelUsed3 = vtfN4;
									Log.Info($"[gmod mdl mat] NormalMap '{_nm4}' -> '{vtfN4}' applied for mesh '{meshName}'");
								}
							}
							
							// Bind light warp texture if present in VMT
							string lightWarpRelUsed3 = null;
							if (vmt.Kv.TryGetValue("$lightwarptexture", out var _lw4))
							{
								if (host.TryResolveVtfByBasetexture(_lw4, out var vtfLW4))
								{
									var lightWarpTex4 = LoadVtfWithLog(host, vtfLW4, "LightWarpTexture", meshName);
									if (!object.ReferenceEquals(lightWarpTex4, Texture.White))
									{
										mat.Set("LightWarpTexture", lightWarpTex4);
										lightWarpRelUsed3 = vtfLW4;
									}
									Log.Info($"[gmod mdl mat] LightWarpTexture '{_lw4}' -> '{vtfLW4}' applied for mesh '{meshName}'");
								}
							}
							
							// VMT scalar/vector parsing (same as first path)
							bool phongOn3 = false;
							if (vmt.Kv.TryGetValue("$phong", out var _ph3) && IsTrue(_ph3)) { phongOn3 = true; }
							// All VMT parameter parsing now handled by ParseAndSetVmtTextures
							if (vmt.Kv.TryGetValue("$blendTintByBaseAlpha", out var _bt3) && IsTrue(_bt3))
							{
								SetBoolParam(mat, "g_bBlendTintByBaseAlpha", true);
							}
							// $PhongDisableHalfLambert not used in VertexLitGeneric2
							if (phongOn3)
							{
								// VMT parameters now handled by ParseAndSetVmtTextures
								
								Log.Info($"[gmod mdl mat] Phong enabled from VMT");
							}
							
							EnsureDefaultPackedMaps(mat);
							
							
							// Default textures already set by EnsureDefaultPackedMaps()
							DumpMaterialTextures("SCAN", meshName, mat);
							LogMaterialDebug("SCAN", meshName, vtfRel, normalRelUsed3, null, lightWarpRelUsed3, mat);
							Log.Info($"[gmod mdl mat] (SCAN) material created for mesh '{meshName}' (from VMT '{tex}', VTF '{vtfRel}')");
							return mat;
						}
						else
						{
							Log.Info($"[gmod mdl mat] baseTex '{baseTex}' could not be resolved to VTF");
						}
					}
					else
					{
						Log.Info($"[gmod mdl mat] VMT '{vmtRel}' missing $basetexture");
					}
				}
				else
				{
					Log.Info($"[gmod mdl mat] Failed to open VMT stream '{vmtRel}'");
				}
			}
			else
			{
				Log.Info($"[gmod mdl mat] No VMT found by name '{tex}'");
			}
		}

		Log.Info($"[gmod mdl mat] No material resolved for mesh '{meshName}'. Using fallback.");
		var fallback = Material.Create(BuildRuntimeMaterialName(mdlPath, "fallback", bodyPartIndex, modelIndex, meshIndex), "shaders/VertexLitGeneric2.shader");
		fallback.Set("BaseTexture", Texture.White);
		fallback.Set("Roughness", GetWhite1x1());
		fallback.Set("OpacityMap", GetWhite1x1());
		fallback.Set("UseRoughnessValue", true);
		fallback.Set("RoughnessValue", 0.2f);
		fallback.Set("UseMetalnessValue", true);
		fallback.Set("MetalnessValue", 0.2f);
		fallback.Set("BaseTextureMask", GetWhite1x1());
		fallback.Set("DetailTexture", GetWhite1x1());
		fallback.Set("DetailMask", GetWhite1x1());
		fallback.Set("LightWarpTexture", GetGray1x1());
		fallback.Set("PhongWarpTexture", GetWhite1x1());
		EnsureDefaultPackedMaps(fallback);
		Log.Info($"[gmod mdl mat] material created for mesh '{meshName}' (from fallback)");
		return fallback;
	}

	// Minimal MDL v48 bone reader and ModelBuilder.AddBone wiring
	private static void BuildSkeletonFromMdl(string mdlPath, ModelBuilder builder)
	{
		using var fs = System.IO.File.OpenRead(mdlPath);
		using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);

		// Header
		var id = new string(br.ReadChars(4));
		int version = br.ReadInt32();
		br.ReadInt32(); // checksum
		br.ReadBytes(64); // name
		br.ReadInt32(); // length
		br.ReadBytes(sizeof(float) * 18); // eye/illum/hull/view
		br.ReadInt32(); // flags
		int numbones = br.ReadInt32();
		int boneindex = br.ReadInt32();

		// Skip to bone section
		if (numbones <= 0 || boneindex <= 0)
			return;

		// First pass: read raw entries using known v48 layout (mstudiobone_t ~216 bytes)
		var boneNames = new List<string>(numbones);
		var parentIndex = new List<int>(numbones);
		var localPosSrc = new List<System.Numerics.Vector3>(numbones);
		var localEulerSrc = new List<System.Numerics.Vector3>(numbones);
        var localQuatSrc = new List<System.Numerics.Quaternion>(numbones);

		for (int b = 0; b < numbones; b++)
		{
			long bonePos = boneindex + b * 216;
			fs.Seek(bonePos, SeekOrigin.Begin);
			int nameOffset = br.ReadInt32();
			int parent = br.ReadInt32();
			// bonecontroller[6]
			for (int i = 0; i < 6; i++) br.ReadInt32();
			// pos (Vector) - MDL local (parent-relative)
			float posX = br.ReadSingle();
			float posY = br.ReadSingle();
			float posZ = br.ReadSingle();
			// quat (Quaternion) - MDL local (parent-relative)
			float qx = br.ReadSingle();
			float qy = br.ReadSingle();
			float qz = br.ReadSingle();
			float qw = br.ReadSingle();
			// rot (RadianEuler)
			float rx = br.ReadSingle();
			float ry = br.ReadSingle();
			float rz = br.ReadSingle();
			// posscale, rotscale skip
			br.ReadSingle(); br.ReadSingle(); br.ReadSingle();
			br.ReadSingle(); br.ReadSingle(); br.ReadSingle();
			// poseToBone matrix3x4 (48 bytes) - used for skinning, not for bone placement
			br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); br.ReadSingle();
			br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); br.ReadSingle();
			br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); br.ReadSingle();
			// qAlignment (16 bytes)
			br.ReadBytes(16);
			// flags, proctype, procindex, physicsbone, surfacepropidx, contents
			br.ReadInt32(); br.ReadInt32(); br.ReadInt32(); br.ReadInt32(); br.ReadInt32(); br.ReadInt32();
			// unused[8]
			br.ReadBytes(32);

			string name = ReadCString(fs, br, bonePos + nameOffset);
			if (string.IsNullOrWhiteSpace(name)) name = $"bone_{b}";

			boneNames.Add(name);
			parentIndex.Add(parent);
			localPosSrc.Add(new System.Numerics.Vector3(posX, posY, posZ));
			localEulerSrc.Add(new System.Numerics.Vector3(rx, ry, rz));
            localQuatSrc.Add(new System.Numerics.Quaternion(qx, qy, qz, qw));
		}


		// Compose local (MDL) to world using parent chain and add bones with world transforms
		var worldPos = new Vector3[numbones];
		var worldRot = new Rotation[numbones];
		for (int i = 0; i < numbones; i++)
		{
			var lpos = new Vector3(localPosSrc[i].X, localPosSrc[i].Y, localPosSrc[i].Z);
			var lq = localQuatSrc[i];
			var lrot = new Rotation(lq.X, lq.Y, lq.Z, lq.W);
			int p = parentIndex[i];
			if (p < 0)
			{
				worldRot[i] = lrot;
				worldPos[i] = lpos;
			}
			else
			{
				var pr = worldRot[p];
				worldRot[i] = pr * lrot;
				worldPos[i] = worldPos[p] + pr * lpos;
			}

			string parentName = (p >= 0 && p < numbones) ? boneNames[p] : null;
			builder.AddBone(boneNames[i], worldPos[i], worldRot[i], parentName);
		}

		// Log hierarchy summary
		int roots = 0;
		var rootNames = new List<string>();
		for (int i = 0; i < numbones; i++)
		{
			if (parentIndex[i] < 0)
			{
				roots++;
				if (rootNames.Count < 3) rootNames.Add(boneNames[i]);
			}
		}
		Log.Info($"[gmod mdl] Skeleton: {numbones} bones, roots={roots}. Root names: {string.Join(", ", rootNames)}");
	}

	private static Rotation ExtractRotationOrthonormalized(Matrix4x4 m)
	{
		var x = new System.Numerics.Vector3(m.M11, m.M12, m.M13);
		var y = new System.Numerics.Vector3(m.M21, m.M22, m.M23);
		var z = new System.Numerics.Vector3(m.M31, m.M32, m.M33);

		x = System.Numerics.Vector3.Normalize(x);
		y = y - System.Numerics.Vector3.Dot(y, x) * x;
		y = System.Numerics.Vector3.Normalize(y);
		z = System.Numerics.Vector3.Cross(x, y);
		z = System.Numerics.Vector3.Normalize(z);
		y = System.Numerics.Vector3.Cross(z, x);

		float trace = x.X + y.Y + z.Z;
		float qx, qy, qz, qw;
		if (trace > 0.0f)
		{
			float s = MathF.Sqrt(trace + 1.0f) * 2.0f;
			qw = 0.25f * s;
			qx = (y.Z - z.Y) / s;
			qy = (z.X - x.Z) / s;
			qz = (x.Y - y.X) / s;
		}
		else if (x.X > y.Y && x.X > z.Z)
		{
			float s = MathF.Sqrt(1.0f + x.X - y.Y - z.Z) * 2.0f;
			qw = (y.Z - z.Y) / s;
			qx = 0.25f * s;
			qy = (y.X + x.Y) / s;
			qz = (z.X + x.Z) / s;
		}
		else if (y.Y > z.Z)
		{
			float s = MathF.Sqrt(1.0f + y.Y - x.X - z.Z) * 2.0f;
			qw = (z.X - x.Z) / s;
			qx = (y.X + x.Y) / s;
			qy = 0.25f * s;
			qz = (z.Y + y.Z) / s;
		}
		else
		{
			float s = MathF.Sqrt(1.0f + z.Z - x.X - y.Y) * 2.0f;
			qw = (x.Y - y.X) / s;
			qx = (z.X + x.Z) / s;
			qy = (z.Y + y.Z) / s;
			qz = 0.25f * s;
		}

		return new Rotation(qx, qy, qz, qw);
	}

	private static Rotation EulerToSourceQuaternion(float rx, float ry, float rz)
	{
		// Replicate Source's AngleQuaternion (QAngle->quat) using angles in radians
		// angles: x=pitch, y=yaw, z=roll
		float sr = (float)Math.Sin(rz * 0.5f);
		float cr = (float)Math.Cos(rz * 0.5f);
		float sp = (float)Math.Sin(rx * 0.5f);
		float cp = (float)Math.Cos(rx * 0.5f);
		float sy = (float)Math.Sin(ry * 0.5f);
		float cy = (float)Math.Cos(ry * 0.5f);
		float x = sr * cp * cy - cr * sp * sy;
		float y = cr * sp * cy + sr * cp * sy;
		float z = cr * cp * sy - sr * sp * cy;
		float w = cr * cp * cy + sr * sp * sy;
		return new Rotation(x, y, z, w);
	}

	// Sum of previous bodyparts' total model vertex counts (bodyPartVertexIndexStart)
	private static List<int> ComputeBodyPartVertexStarts(string mdlPath)
	{
		var starts = new List<int>();
		try
		{
			using var fs = System.IO.File.OpenRead(mdlPath);
			using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
			br.ReadBytes(4 + 4 + 4);
			br.ReadBytes(64);
			br.ReadInt32();
			br.ReadBytes(sizeof(float) * 18);
			br.ReadInt32(); // flags
			br.ReadInt32(); // numbones
			br.ReadInt32(); // boneindex
			for (int i = 0; i < 17; i++) br.ReadInt32();
			int numBodyParts = br.ReadInt32();
			int bodyPartIndex = br.ReadInt32();
			int running = 0;
			for (int bp = 0; bp < numBodyParts; bp++)
			{
				starts.Add(running);
				long bodyPartPos = bodyPartIndex + bp * 16;
				fs.Seek(bodyPartPos, SeekOrigin.Begin);
				br.ReadInt32(); // nameindex
				int numModels = br.ReadInt32();
				br.ReadInt32(); // base
				int modelIndex = br.ReadInt32();
				for (int m = 0; m < numModels; m++)
				{
					long modelPos = bodyPartPos + modelIndex + m * 148; // approx
					fs.Seek(modelPos, SeekOrigin.Begin);
					br.ReadBytes(64); // name
					br.ReadInt32(); // type
					br.ReadSingle(); // radius
					br.ReadInt32(); // meshCount
					br.ReadInt32(); // meshOffset
					int numVertices = br.ReadInt32();
					br.ReadInt32(); // vertexindex
					running += Math.Max(0, numVertices);
				}
			}
		}
		catch { }
		return starts;
	}

	private sealed class BuiltMeshResult
	{
		public Mesh Mesh;
		public int BodyPartIndex;
		public int ModelIndex;
		public int MeshIndex;
		public int TriangleCount;
	}

	private static BuiltMeshResult BuildFirstMeshDetailed(string vtxPath, SourceVvd.Data vvdData, string mdlPath)
	{
		if (string.IsNullOrEmpty(vtxPath) || vvdData == null) return null;
		for (int bp = 0; bp < 16; bp++)
		{
			for (int m = 0; m < 16; m++)
			{
				for (int me = 0; me < 8; me++)
				{
					var origIndices = SourceVtx.ReadLod0MeshOriginalIndices(vtxPath, bp, m, me);
					if (origIndices != null && origIndices.Count >= 3)
					{
						Log.Info($"[gmod mdl] Selecting mesh bp={bp} model={m} mesh={me} indices={origIndices.Count}");
						var material = Material.Create("model", "shaders/VertexLitGeneric2.shader");
						material.Set("BaseTexture", Texture.White);
						material.Set("Roughness", GetWhite1x1());
						material.Set("UseRoughnessValue", true);
						material.Set("RoughnessValue", 0.2f);
						material.Set("OpacityMap", GetWhite1x1());
						material.Set("g_bUseRoughnessValue", true);
						material.Set("g_flRoughnessValue", 0.2f);
						material.Set("BaseTextureMask", GetWhite1x1());
						material.Set("DetailTexture", GetWhite1x1());
						material.Set("DetailMask", GetWhite1x1());
						material.Set("LightWarpTexture", GetGray1x1());
						material.Set("PhongWarpTexture", GetWhite1x1());
						var outMesh = new Mesh(material);
						var outVerts = new List<SimpleVertex>();
						var outIndices = new List<int>();
						var vertMap = new Dictionary<int,int>();
						for (int i = 0; i < origIndices.Count; i++)
						{
							int orig = origIndices[i];
							if (!vertMap.TryGetValue(orig, out int outIdx))
							{
								if (orig < 0 || orig >= vvdData.Vertices.Count) continue;
								var v = vvdData.Vertices[orig];
								float u = v.UV.x;
								float vv = FlipV ? (1f - v.UV.y) : v.UV.y;
								var sv = new SimpleVertex(
									new Vector3(v.Position.x, v.Position.y, v.Position.z),
									new Vector3(v.Normal.x, v.Normal.y, v.Normal.z),
									Vector3.Zero,
									new Vector2(u, vv)
								);
								outIdx = outVerts.Count;
								outVerts.Add(sv);
								vertMap[orig] = outIdx;
							}
							outIndices.Add(outIdx);
						}
						// Flip triangle winding (Source/S2 winding may differ)
						if (outIndices.Count >= 3 && (outIndices.Count % 3) == 0)
						{
							for (int t = 0; t < outIndices.Count; t += 3)
							{
								int tmp = outIndices[t + 1];
								outIndices[t + 1] = outIndices[t + 2];
								outIndices[t + 2] = tmp;
							}
							Log.Info("[gmod mdl] Flipped triangle winding for compatibility");
						}
						outMesh.CreateVertexBuffer(outVerts.Count, SimpleVertex.Layout, outVerts);
						outMesh.CreateIndexBuffer(outIndices.Count, outIndices.ToArray());
						outMesh.Bounds = BBox.FromPoints(outVerts.ConvertAll(v => v.position));
						return new BuiltMeshResult
						{
							Mesh = outMesh,
							BodyPartIndex = bp,
							ModelIndex = m,
							MeshIndex = me,
							TriangleCount = outIndices.Count / 3
						};
					}
				}
			}
		}
		return null;
	}

	private static bool TryReadMeshMaterialSkinRef(string mdlPath, int bodyPartIndex, int modelIndex, int meshIndex, int expectedMeshStart, int expectedMeshVertCount, int numskinref, int numtextures, out int skinRef, out int chosenStride, out int resolvedMeshIndex)
	{
		skinRef = -1;
		chosenStride = 0;
		resolvedMeshIndex = meshIndex;
		try
		{
			using var fs = System.IO.File.OpenRead(mdlPath);
			using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
			br.ReadBytes(4 + 4 + 4); // id, version, checksum
			br.ReadBytes(64); // name
			br.ReadInt32(); // length
			br.ReadBytes(sizeof(float) * 18);
			br.ReadInt32(); // flags
			br.ReadInt32(); // numbones
			br.ReadInt32(); // boneindex
			for (int i = 0; i < 17; i++) br.ReadInt32();
			int numBodyParts = br.ReadInt32();
			int bodyPartIndexOff = br.ReadInt32();
			if (bodyPartIndex < 0 || bodyPartIndex >= numBodyParts || bodyPartIndexOff <= 0) return false;
			long bodyPartPos = bodyPartIndexOff + bodyPartIndex * 16;
			fs.Seek(bodyPartPos, SeekOrigin.Begin);
			br.ReadInt32(); // bpNameIndex
			int numModels = br.ReadInt32();
			br.ReadInt32(); // bpBase
			int modelIndexOff = br.ReadInt32();
			if (modelIndex < 0 || modelIndex >= numModels || modelIndexOff <= 0) return false;
			const int modelStride = 148; // typical v48
			long modelPos = bodyPartPos + modelIndexOff + modelIndex * modelStride;
			fs.Seek(modelPos, SeekOrigin.Begin);
			br.ReadBytes(64); // model name[64]
			br.ReadInt32(); // type
			br.ReadSingle(); // radius
			int meshCount = br.ReadInt32();
			int meshOffset = br.ReadInt32();
			int modelNumVertices = br.ReadInt32();
			br.ReadInt32(); // vertexindex
			if (meshIndex < 0 || meshIndex >= meshCount || meshOffset <= 0) return false;
			int[] candidates = new[] { 56, 60, 64, 68, 72, 76, 80, 84, 88, 92, 96, 100, 104, 112, 116, 120, 128 };
			int bestStride = 0;
			int bestPass = -1;
			for (int ci = 0; ci < candidates.Length; ci++)
			{
				int stride = candidates[ci];
				int pass = 0;
				for (int me = 0; me < meshCount; me++)
				{
					long mPos = modelPos + meshOffset + me * stride;
					if (mPos < 0 || mPos + 16 > fs.Length) { pass = -1; break; }
					fs.Seek(mPos, SeekOrigin.Begin);
					int mat = br.ReadInt32();
					br.ReadInt32(); // model backref
					int meshVerts = br.ReadInt32();
					int meshVertexOff = br.ReadInt32();
					if (meshVerts < 0 || meshVertexOff < 0 || meshVerts > Math.Max(0, modelNumVertices) || meshVertexOff > Math.Max(0, modelNumVertices)) { pass = -1; break; }
					// material in skinref range or texture list range gives small bonus
					if (mat >= 0 && (mat < numskinref || mat < numtextures)) pass += 10;
					if (expectedMeshVertCount > 0 && meshVerts == expectedMeshVertCount && meshVertexOff == expectedMeshStart) pass += 1000;
					pass++;
				}
				if (pass > bestPass)
				{
					bestPass = pass;
					bestStride = stride;
					if (pass == meshCount) break;
				}
			}
			if (bestPass <= 0) return false;
			// resolve index if we can match start/count
			resolvedMeshIndex = meshIndex;
			if (expectedMeshVertCount > 0)
			{
				for (int me = 0; me < meshCount; me++)
				{
					long mPos = modelPos + meshOffset + me * bestStride;
					if (mPos < 0 || mPos + 16 > fs.Length) continue;
					fs.Seek(mPos, SeekOrigin.Begin);
					br.ReadInt32(); // material
					br.ReadInt32(); // backref
					int meshVerts = br.ReadInt32();
					int meshVertexOff = br.ReadInt32();
					if (meshVerts == expectedMeshVertCount && meshVertexOff == expectedMeshStart) { resolvedMeshIndex = me; break; }
				}
			}
			long selPos = modelPos + meshOffset + resolvedMeshIndex * bestStride;
			if (selPos < 0 || selPos + 16 > fs.Length) return false;
			fs.Seek(selPos, SeekOrigin.Begin);
			int matSel = br.ReadInt32();
			br.ReadInt32(); // backref
			int selVerts = br.ReadInt32();
			int selOff = br.ReadInt32();
			skinRef = matSel;
			chosenStride = bestStride;
			Log.Info($"[gmod mdl mat] match bp={bodyPartIndex} m={modelIndex} me={meshIndex}->res={resolvedMeshIndex} expStart={expectedMeshStart} expVerts={expectedMeshVertCount} gotStart={selOff} gotVerts={selVerts} stride={chosenStride} matRef={skinRef}");
			return skinRef >= 0;
		}
		catch { return false; }
	}

	private static void WriteDebugMaterialDump(string tag, string meshName, string shader, string baseVtfRel, string bumpVtfRel)
	{
		try
		{
			var safeMesh = Sanitize(meshName);
			var now = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
			var relDir = "gmod_material_dumps";
			var relFile = System.IO.Path.Combine(relDir, $"{tag}_{safeMesh}_{now}.vmat").Replace('\\','/');

			// Convert VTF mount path to materials-relative if possible for readability
			string ToMaterialsPath(string p)
			{
				if (string.IsNullOrWhiteSpace(p)) return null;
				var s = p.Replace('\\','/');
				if (s.StartsWith("garrysmod/", StringComparison.OrdinalIgnoreCase)) s = s.Substring("garrysmod/".Length);
				return s;
			}

			var baseVis = ToMaterialsPath(baseVtfRel) ?? string.Empty;
			var bumpVis = ToMaterialsPath(bumpVtfRel);

			var sb = new System.Text.StringBuilder();
			sb.AppendLine("// THIS FILE IS AUTO-GENERATED");
			sb.AppendLine();
			sb.AppendLine("Layer0");
			sb.AppendLine("{");
			sb.AppendLine($"\tshader \"{shader}\"");
			sb.AppendLine();
			sb.AppendLine("\t//---- Material ----");
			if (!string.IsNullOrWhiteSpace(baseVis)) sb.AppendLine($"\tBaseTexture \"{baseVis}\"");
			if (!string.IsNullOrWhiteSpace(bumpVis)) sb.AppendLine($"\tNormalMap \"{bumpVis}\"");
			sb.AppendLine("\tRoughness \"[1.000000 1.000000 1.000000 0.000000]\"");
			sb.AppendLine("}");

			if (!Sandbox.FileSystem.Data.DirectoryExists(relDir)) Sandbox.FileSystem.Data.CreateDirectory(relDir);
			Sandbox.FileSystem.Data.WriteAllText(relFile, sb.ToString());
			//Log.Info($"[gmod mdl mat] wrote material dump: data://{relFile}");
		}
		catch
		{
			////Log.warning($"[gmod mdl mat] debug dump failed");
		}
	}

    private static Texture _white1x1;
    private static Texture GetWhite1x1()
    {
        if (_white1x1 == null)
        {
            _white1x1 = Texture.Create(1,1).WithData(new byte[]{255,255,255,255}).Finish();
        }
        return _white1x1;
    }

    private static Texture _gray1x1;
    private static Texture GetGray1x1()
    {
        if (_gray1x1 == null)
        {
            _gray1x1 = Texture.Create(1,1).WithData(new byte[]{128,128,128,255}).Finish(); // Neutral gray
        }
        return _gray1x1;
    }

    private static Texture LoadVtfWithLog(GModMount host, string relVtf, string usage, string meshName)
    {
        try
        {
            using (var s = host.GetFileStreamForVtf(relVtf))
            {
                if (s == System.IO.Stream.Null)
                {
                    //Log.warning($"[gmod vtf] {usage} stream null for '{relVtf}' (mesh '{meshName}')");
                    return Texture.White;
                }
            }
            var tex = GModVtfTextureLoader.LoadTextureFromMount(host, relVtf);
            if (object.ReferenceEquals(tex, Texture.White))
            {
                //Log.warning($"[gmod vtf] {usage} fallback to Texture.White for '{relVtf}' (mesh '{meshName}')");
            }
            return tex;
        }
        catch
        {
            //Log.warning($"[gmod vtf] {usage} load error for '{relVtf}'");
            return Texture.White;
        }
    }

    private static void LogMaterialDebug(string tag, string meshName, string baseVtfRel, string normalVtfRel, string pexpVtfRel, string lightWarpVtfRel, Material mat)
    {
        try
        {
            var baseOk = !string.IsNullOrWhiteSpace(baseVtfRel);
            var normalOk = !string.IsNullOrWhiteSpace(normalVtfRel);
            var pexpOk = !string.IsNullOrWhiteSpace(pexpVtfRel);
            var lightWarpOk = !string.IsNullOrWhiteSpace(lightWarpVtfRel);
            
            Log.Info($"[gmod mdl mat dbg] {tag} '{meshName}' textures: base=({baseOk}) normal=({normalOk}) phongexp=({pexpOk}) lightwarp=({lightWarpOk})");
            Log.Info($"[gmod mdl mat dbg] {tag} '{meshName}' files: base='{baseVtfRel}' normal='{normalVtfRel}' phongexp='{pexpVtfRel}' lightwarp='{lightWarpVtfRel}'");
        }
        catch { }
    }
    
    private static void LogComprehensiveMaterialDebug(string tag, string meshName, Material mat, Dictionary<string, object> vmtData = null)
    {
        try
        {
            Log.Info($"========== {tag} MATERIAL DEBUG: '{meshName}' ==========");
            
            // Shader combos
            var combos = new[]
            {
                "S_DETAIL_TEXTURE", "S_SELF_ILLUMINATION", "S_RIM_LIGHTING", "S_TRANSPARENCY",
                "S_ALPHA_TEST", "S_ENVIRONMENTAL_REFLECTIONS", "S_PHONG_WARP_TEXTURE", "S_VERTEX_COLORS", "S_BACKFACE_CULLING"
            };
            
            foreach (var combo in combos)
            {
                try
                {
                    // Can't read combo values from material, so just log what should be set
                    Log.Info($"[gmod mdl mat debug] Combo {combo}: (set during creation)");
                }
                catch { }
            }
            
            // Material parameters
            var parameters = new[]
            {
                ("g_vColorTint", "Color Tint"),
                ("g_bBlendTintByBaseAlpha", "Blend Tint By Base Alpha"),
                ("g_bUseMetalnessValue", "Use Metalness Value"),
                ("g_bUseRoughnessValue", "Use Roughness Value"),
                ("g_flMetalnessValue", "Metalness Value"),
                ("g_flRoughnessValue", "Roughness Value"),
                ("g_flNormalIntensity", "Normal Intensity"),
                // Old shader parameters removed - VertexLitGeneric2 uses different names
            };
            
            foreach (var (paramName, displayName) in parameters)
            {
                Log.Info($"[gmod mdl mat debug] {displayName} ({paramName}): (set during creation)");
            }
            
            // VMT data if available
            if (vmtData != null && vmtData.Count > 0)
            {
                Log.Info("[gmod mdl mat debug] VMT Parameters:");
                foreach (var kvp in vmtData)
                {
                    Log.Info($"[gmod mdl mat debug]   {kvp.Key} = {kvp.Value}");
                }
            }
            
            Log.Info($"========== END {tag} MATERIAL DEBUG ==========");
        }
        catch
        {
            //Log.warning($"[gmod mdl mat debug] Failed to log comprehensive debug");
        }
    }


    private static void DumpMaterialTextures(string tag, string meshName, Material mat)
    {
        try
        {
            var groups = new (string label, string[] names)[] {
                ("Base", new[]{"BaseTexture","g_tBaseTexture","g_tColor","Color"}),
                ("BaseMask", new[]{"BaseTextureMask"}),
                ("Normal", new[]{"NormalMap","Normal","g_tNormal","g_tNormalMap","BumpMap","g_tBumpMap"}),
                ("Roughness", new[]{"Roughness","g_tRMA"}),
                ("Metalness", new[]{"Metalness","g_tRMA"}),
                ("AO", new[]{"AmbientOcclusion","g_tRMA"}),
                ("Opacity", new[]{"OpacityMap"}),
                ("Detail", new[]{"DetailTexture","DetailMask","g_tDetailTexture"}),
                ("LightWarp", new[]{"LightWarpTexture","g_tLightWarpTexture"}),
                ("SpecExp", new[]{"SpecularExponentTexture","g_tSpecularExponentTexture"}),
                ("SpecWarp", new[]{"SpecularWarpTexture"})
            };
            var sb = new System.Text.StringBuilder();
            sb.Append($"[gmod mdl mat tex] {tag} '{meshName}' ");
            foreach (var g in groups)
            {
                bool any = false;
                foreach (var n in g.names)
                {
                    var t = mat.GetTexture(n);
                    if (t != null) { any = true; break; }
                }
                sb.Append($"{g.label}={(any?"bound":"null")} ");
            }
            Log.Info(sb.ToString());
        }
        catch { }
    }

    private static void SetBaseColorTexture(Material mat, Texture tex)
    {
        if (mat == null) return;
        try
        {
            mat.Set("BaseTexture", tex);
            mat.Set("g_tBaseTexture", tex);
            mat.Set("g_tColor", tex);
            mat.Set("Color", tex);
        }
        catch { }
    }

    private static void SetNormalTexture(Material mat, Texture tex)
    {
        if (mat == null) return;
        try
        {
            mat.Set("NormalMap", tex);
            mat.Set("Normal", tex);
            mat.Set("g_tNormal", tex);
            mat.Set("g_tNormalMap", tex);
            mat.Set("BumpMap", tex);
            mat.Set("g_tBumpMap", tex);
        }
        catch { }
    }

    private static string BuildRuntimeMaterialName(string mdlPath, string rel, int bp, int m, int me)
    {
        try
        {
            string mdl = System.IO.Path.GetFileNameWithoutExtension(mdlPath);
            string relSan = Sanitize(rel ?? "unknown");
            string mdlSan = Sanitize(mdl ?? "mdl");
            return $"gmod_{mdlSan}_{relSan}_bp{bp}_m{m}_me{me}";
        }
        catch
        {
            return $"gmod_mat_bp{bp}_m{m}_me{me}";
        }
    }

    private static void EnsureDefaultPackedMaps(Material mat)
    {
        if (mat == null) return;
        try
        {
            var white = GetWhite1x1();
            // Ensure packed RMA has valid sources
            mat.Set("Roughness", white);
            mat.Set("Metalness", white);
            mat.Set("AmbientOcclusion", white);
            // Try engine alias bindings too
            mat.Set("g_tRoughness", white);
            mat.Set("g_tMetalness", white);
            mat.Set("g_tAmbientOcclusion", white);
            mat.Set("g_tRMA", white);

			// Ensure base supporting maps exist for VertexLitGeneric2 (only set if not already set)
			if (mat.GetTexture("g_tNormal") == null) mat.Set("g_tNormal", GetGray1x1());
			if (mat.GetTexture("g_tAmbientOcclusionTexture") == null) mat.Set("g_tAmbientOcclusionTexture", white);
			if (mat.GetTexture("g_tDetailTexture") == null) mat.Set("g_tDetailTexture", white);
			if (mat.GetTexture("g_tSelfIllumMaskTexture") == null) mat.Set("g_tSelfIllumMaskTexture", white);
			
			// Don't override g_tSpecularExponentTexture - ParseAndSetVmtTextures handles this properly
			// including setting the correct g_bConstantSpecularExponent value
			
			// VertexLitGeneric2 defaults now handled by ParseAndSetVmtTextures
			// Don't override VMT-parsed values here
        }
        catch { }
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

    private static void ForceDebugColorTint(Material mat)
    {
        if (mat == null) return;
        try
        {
            var dbg3 = new System.Numerics.Vector3(0f, 2f, 2f);
            // Common names
            mat.Set("g_vColorTint", dbg3);
            mat.Set("ColorTint", dbg3);
            mat.Set("vColorTint", dbg3);
            mat.Set("Color2", dbg3);
            mat.Set("g_vColor2", dbg3);
            mat.Set("Tint", dbg3);
            mat.Set("g_bBlendTintByBaseAlpha", false);
            Log.Info("[gmod mdl mat] Debug: forced ColorTint = [0,2,2], disabled BlendTintByBaseAlpha");
        }
        catch { }
    }

    private static void SetFloatParam(Material mat, string name, float value)
    {
        try 
        { 
            mat.Set(name, new System.Numerics.Vector4(value, 0f, 0f, 0f)); 
            // Only log important parameters to reduce spam
            if (name.Contains("Phong") || name.Contains("Exponent")) 
                Log.Info($"[gmod mdl mat] Set float param {name} = {value}");
        } 
        catch 
        { 
            //Log.warning($"[gmod mdl mat] Failed to set float param {name}");
        }
    }

    private static void SetBoolParam(Material mat, string name, bool value)
    {
        try 
        { 
            mat.Set(name, new System.Numerics.Vector4(value ? 1f : 0f, 0f, 0f, 0f)); 
            // Only log important parameters to reduce spam
            if (name.Contains("Phong") || name.Contains("BlendTint")) 
                Log.Info($"[gmod mdl mat] Set bool param {name} = {value}");
        } 
        catch 
        { 
            //Log.warning($"[gmod mdl mat] Failed to set bool param {name}");
        }
    }

    private static void SetVec3Param(Material mat, string name, float x, float y, float z)
    {
        try 
        { 
            mat.Set(name, new System.Numerics.Vector3(x, y, z)); 
            // Log only important ones to reduce spam
            if (name == "g_vColorTint") 
                Log.Info($"[gmod mdl mat] Set color tint {name} = ({x}, {y}, {z})");
        } 
        catch 
        { 
            //Log.warning($"[gmod mdl mat] Failed to set vec3 param {name}");
        }
    }
    
    private static void LogMaterialSpecularDebug(string tag, string meshName, Material mat)
    {
        try
        {
            Log.Info($"========== {tag} SPECULAR DEBUG: {meshName} ==========");
            
            // Check if material and attributes exist
            if (mat?.Attributes == null)
            {
                Log.Warning($"[{tag}] Material or Attributes is null!");
                return;
            }
            
            // Debug specular-related parameters
            try { Log.Info($"[{tag}] g_bConstantSpecularExponent: {mat.Attributes.GetBool("g_bConstantSpecularExponent", false)}"); } catch { Log.Info($"[{tag}] g_bConstantSpecularExponent: NOT SET"); }
            try { Log.Info($"[{tag}] g_flSpecularExponent: {mat.Attributes.GetFloat("g_flSpecularExponent", 0f)}"); } catch { Log.Info($"[{tag}] g_flSpecularExponent: NOT SET"); }
            try { Log.Info($"[{tag}] g_flSpecularBoost: {mat.Attributes.GetFloat("g_flSpecularBoost", 0f)}"); } catch { Log.Info($"[{tag}] g_flSpecularBoost: NOT SET"); }
            try { Log.Info($"[{tag}] g_vSourceFresnelRanges: {mat.Attributes.GetVector("g_vSourceFresnelRanges", Vector3.Zero)}"); } catch { Log.Info($"[{tag}] g_vSourceFresnelRanges: NOT SET"); }
            try { Log.Info($"[{tag}] g_bConstantSpecularTint: {mat.Attributes.GetBool("g_bConstantSpecularTint", false)}"); } catch { Log.Info($"[{tag}] g_bConstantSpecularTint: NOT SET"); }
            
            // Check specular textures
            var specExpTex = mat.GetTexture("g_tSpecularExponentTexture");
            if (specExpTex != null)
            {
                Log.Info($"[{tag}] g_tSpecularExponentTexture: {specExpTex.ResourceName} (IsWhite: {object.ReferenceEquals(specExpTex, Texture.White)})");
            }
            else
            {
                Log.Info($"[{tag}] g_tSpecularExponentTexture: NULL");
            }
            
            var specWarpTex = mat.GetTexture("g_tSpecularWarpTexture");
            if (specWarpTex != null)
            {
                Log.Info($"[{tag}] g_tSpecularWarpTexture: {specWarpTex.ResourceName} (IsWhite: {object.ReferenceEquals(specWarpTex, Texture.White)})");
            }
            else
            {
                Log.Info($"[{tag}] g_tSpecularWarpTexture: NULL");
            }
            
            // Debug dynamic combos that affect specular
            try 
            { 
                var phongWarpCombo = mat.Attributes.GetComboInt("D_PHONGWARPTEXTURE");
                var lightWarpCombo = mat.Attributes.GetComboInt("D_LIGHTWARPTEXTURE");
                var blendTintCombo = mat.Attributes.GetComboInt("D_BLENDTINTBYBASEALPHA");
                var rimlightCombo = mat.Attributes.GetComboInt("D_RIMLIGHT");
                
                Log.Info($"[{tag}] Dynamic Combos:");
                Log.Info($"[{tag}]   D_PHONGWARPTEXTURE: {phongWarpCombo}");
                Log.Info($"[{tag}]   D_LIGHTWARPTEXTURE: {lightWarpCombo}");
                Log.Info($"[{tag}]   D_BLENDTINTBYBASEALPHA: {blendTintCombo}");
                Log.Info($"[{tag}]   D_RIMLIGHT: {rimlightCombo}");
        } 
        catch (Exception ex) 
        { 
                Log.Info($"[{tag}] Dynamic combos: ERROR - {ex.Message}"); 
            }
            
            // Summary analysis
            Log.Info($"[{tag}] === SPECULAR ANALYSIS ===");
            var constantSpecExp = mat.Attributes.GetBool("g_bConstantSpecularExponent", false);
            var specBoost = mat.Attributes.GetFloat("g_flSpecularBoost", 0f);
            var specExp = mat.Attributes.GetFloat("g_flSpecularExponent", 0f);
            // specExpTex already declared above
            
            if (constantSpecExp && specExp > 0 && specBoost > 0)
            {
                Log.Info($"[{tag}] ✅ Should have CONSTANT specular: Exp={specExp}, Boost={specBoost}");
            }
            else if (!constantSpecExp && specExpTex != null && !object.ReferenceEquals(specExpTex, Texture.White))
            {
                Log.Info($"[{tag}] ✅ Should use TEXTURE specular from: {specExpTex.ResourceName}");
            }
            else if (!constantSpecExp && (specExpTex == null || object.ReferenceEquals(specExpTex, Texture.White)))
            {
                Log.Warning($"[{tag}] ❌ TEXTURE specular enabled but NO TEXTURE: ConstantExp={constantSpecExp}");
            }
            else
            {
                Log.Warning($"[{tag}] ❌ SPECULAR DISABLED: ConstantExp={constantSpecExp}, Exp={specExp}, Boost={specBoost}");
            }
            
            Log.Info($"========== END {tag} SPECULAR DEBUG ==========");
        }
        catch (Exception ex)
        {
            Log.Warning($"[{tag}] Specular debug failed: {ex.Message}");
        }
    }
    
    private static void ParseAndSetVmtTextures(Material mat, GModVmt.Data vmt, GModMount host, string meshName)
    {
        try
        {
            // Parse and load lightwarp texture from VMT
            if (vmt.Kv.TryGetValue("$lightwarptexture", out var lightWarpName))
            {
                if (host.TryResolveVtfByBasetexture(lightWarpName, out var vtfLW))
                {
                    var lightWarpTex = LoadVtfWithLog(host, vtfLW, "LightWarpTexture", meshName);
                    mat.Set("LightWarpTexture", lightWarpTex);
                    mat.Set("g_tLightWarpTexture", lightWarpTex);
                    Log.Info($"[gmod mdl mat] LightWarpTexture '{lightWarpName}' -> '{vtfLW}' applied for mesh '{meshName}'");
                }
                else
                {
                    Log.Warning($"[gmod mdl mat] lightwarp resolve failed for '{lightWarpName}'");
                    mat.Set("LightWarpTexture", GetGray1x1());
                }
            }
            else
            {
                // Set default neutral gray lightwarp if no texture specified
                mat.Set("LightWarpTexture", GetGray1x1());
            }
            
            // Parse and load phongwarp texture from VMT  
            if (vmt.Kv.TryGetValue("$phongwarptexture", out var phongWarpName))
            {
                if (host.TryResolveVtfByBasetexture(phongWarpName, out var vtfPW))
                {
                    var phongWarpTex = LoadVtfWithLog(host, vtfPW, "SpecularWarpTexture", meshName);
                    mat.Set("g_tSpecularWarpTexture", phongWarpTex);
                    Log.Info($"[gmod mdl mat] SpecularWarpTexture '{phongWarpName}' -> '{vtfPW}' applied for mesh '{meshName}'");
                }
                else
                {
                    Log.Warning($"[gmod mdl mat] phongwarp resolve failed for '{phongWarpName}'");
                    mat.Set("g_tSpecularWarpTexture", GetWhite1x1());
                }
            }
            else
            {
                // Set default white specular warp if no texture specified
                mat.Set("g_tSpecularWarpTexture", GetWhite1x1());
            }
            
            // Parse and load specular exponent texture from VMT
            if (vmt.Kv.TryGetValue("$phongexponenttexture", out var specExpName))
            {
                if (host.TryResolveVtfByBasetexture(specExpName, out var vtfSE))
                {
                    var specExpTex = LoadVtfWithLog(host, vtfSE, "SpecularExponentTexture", meshName);
                    if (!object.ReferenceEquals(specExpTex, Texture.White))
                    {
                        mat.Set("g_tSpecularExponentTexture", specExpTex);
                        mat.Set("g_bConstantSpecularExponent", false);
                        Log.Info($"[gmod mdl mat] SpecularExponentTexture '{specExpName}' -> '{vtfSE}' applied for mesh '{meshName}'");
                    }
                }
                else
                {
                    Log.Warning($"[gmod mdl mat] specularexponent resolve failed for '{specExpName}'");
                    mat.Set("g_tSpecularExponentTexture", GetWhite1x1());
                    mat.Set("g_bConstantSpecularExponent", true);
                }
            }
            else
            {
                // Set default white specular exponent if no texture specified
                mat.Set("g_tSpecularExponentTexture", GetWhite1x1());
                mat.Attributes.Set("g_bConstantSpecularExponent", true);
            }
            
            // Parse fresnel ranges from VMT
            if (vmt.Kv.TryGetValue("$phongfresnelranges", out var fresnelRanges) && TryParseVec3(fresnelRanges, out var vfr))
            {
                mat.Set("g_vSourceFresnelRanges", new System.Numerics.Vector3(vfr.X, vfr.Y, vfr.Z));
                Log.Info($"[gmod mdl mat] $phongfresnelranges -> g_vSourceFresnelRanges [{vfr.X:0.###},{vfr.Y:0.###},{vfr.Z:0.###}]");
            }
            else
            {
                // Set default fresnel ranges only if not specified in VMT
                mat.Set("g_vSourceFresnelRanges", new System.Numerics.Vector3(0.0f, 0.5f, 1.0f));
            }
            
            // Parse specular boost from VMT  
            if (vmt.Kv.TryGetValue("$phongboost", out var specBoost) && float.TryParse(specBoost, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fboost))
            {
                mat.Set("g_flSpecularBoost", fboost);
                Log.Info($"[gmod mdl mat] $phongboost -> {fboost}");
            }
            else
            {
                // Set default specular boost only if not specified in VMT
                mat.Set("g_flSpecularBoost", 1.0f);
            }
            
            // Parse phong exponent from VMT
            if (vmt.Kv.TryGetValue("$phongexponent", out var phongExp) && float.TryParse(phongExp, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fexp))
            {
                mat.Set("g_flSpecularExponent", fexp);
                Log.Info($"[gmod mdl mat] $phongexponent -> {fexp}");
            }
            else
            {
                // Set default phong exponent only if not specified in VMT
                mat.Set("g_flSpecularExponent", 20.0f);
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[gmod mdl mat] Error parsing VMT textures: {ex.Message}");
            // Set defaults on error
            mat.Set("LightWarpTexture", GetGray1x1());
            mat.Set("g_tLightWarpTexture", GetGray1x1());
            mat.Set("g_tSpecularWarpTexture", GetWhite1x1());
            mat.Set("g_tSpecularExponentTexture", GetWhite1x1());
            mat.Set("g_bConstantSpecularExponent", true);
            mat.Set("g_vSourceFresnelRanges", new System.Numerics.Vector3(0.0f, 0.5f, 1.0f));
            mat.Set("g_flSpecularBoost", 1.0f);
            mat.Set("g_flSpecularExponent", 20.0f);
        }
    }
}



