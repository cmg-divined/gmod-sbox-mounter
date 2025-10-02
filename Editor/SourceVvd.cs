using System;
using System.Collections.Generic;
using IO = System.IO;
using Sandbox;

internal static class SourceVvd
{
	public const int MaxNumLods = 8;
	public const int MaxBonesPerVert = 3; // matches v48 layout (48-byte mstudiovertex_t)

	public sealed class Vertex
	{
		public float[] Weights = new float[MaxBonesPerVert];
		public byte[] Bones = new byte[MaxBonesPerVert];
		public byte BoneCount;
		public Vector3 Position;
		public Vector3 Normal;
		public Vector2 UV;
		public Vector3 Tangent;
		public float TangentW;
	}

	public sealed class Data
	{
		public string Id;
		public int Version;
		public int Checksum;
		public int LodCount;
		public int[] LodVertexCount = new int[MaxNumLods];
		public int FixupCount;
		public int FixupTableOffset;
		public int VertexDataOffset;
		public int TangentDataOffset;
		public List<Vertex> Vertices = new();
	}

	public static Data Parse(string path)
	{
		using var fs = IO.File.OpenRead(path);
		using var br = new IO.BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
		var data = new Data();

		data.Id = new string(br.ReadChars(4));
		data.Version = br.ReadInt32();
		data.Checksum = br.ReadInt32();
		data.LodCount = br.ReadInt32();
		for (int i = 0; i < MaxNumLods; i++) data.LodVertexCount[i] = br.ReadInt32();
		data.FixupCount = br.ReadInt32();
		data.FixupTableOffset = br.ReadInt32();
		data.VertexDataOffset = br.ReadInt32();
		data.TangentDataOffset = br.ReadInt32();

		//Log.Info($"[gmod vvd] id={data.Id} ver={data.Version} lods={data.LodCount} fixups={data.FixupCount} vertices@lod0={data.LodVertexCount[0]}");

		if (data.LodCount <= 0 || data.VertexDataOffset <= 0)
			return data;

		// Handle fixups to build correct LOD0 vertex order (matches Source behavior)
		if (data.FixupCount > 0 && data.FixupTableOffset > 0)
		{
			fs.Seek(data.FixupTableOffset, IO.SeekOrigin.Begin);
			var fixups = new (int lod, int source, int count)[data.FixupCount];
			for (int i = 0; i < data.FixupCount; i++)
			{
				int lod = br.ReadInt32();
				int source = br.ReadInt32();
				int num = br.ReadInt32();
				fixups[i] = (lod, source, num);
			}

			// Determine how many raw vertices we must read to satisfy all fixup ranges
			int rawCount = 0;
			for (int i = 0; i < fixups.Length; i++)
			{
				int end = fixups[i].source + fixups[i].count;
				if (end > rawCount) rawCount = end;
			}

			// Read raw vertex pool
			fs.Seek(data.VertexDataOffset, IO.SeekOrigin.Begin);
			var raw = new List<Vertex>(rawCount);
			for (int v = 0; v < rawCount; v++)
			{
				raw.Add(ReadOneVertex(br));
			}

			// Read raw tangents (if present): float4 per vertex (xyz=tangent, w=handedness)
			if (data.TangentDataOffset > 0)
			{
				fs.Seek(data.TangentDataOffset, IO.SeekOrigin.Begin);
				for (int v = 0; v < rawCount; v++)
				{
					float tx = br.ReadSingle();
					float ty = br.ReadSingle();
					float tz = br.ReadSingle();
					float tw = br.ReadSingle();
					raw[v].Tangent = new Vector3(tx, ty, tz);
					raw[v].TangentW = tw;
				}
			}

			// Assemble LOD0 vertex list: include fixups with lod >= 0 in table order
			for (int i = 0; i < fixups.Length; i++)
			{
				if (fixups[i].lod >= 0)
				{
					for (int j = 0; j < fixups[i].count; j++)
					{
						data.Vertices.Add(raw[fixups[i].source + j]);
					}
				}
			}
		}
		else
		{
			// No fixups: vertices are sequential for LOD0
			fs.Seek(data.VertexDataOffset, IO.SeekOrigin.Begin);
			int count = data.LodVertexCount[0];
			for (int v = 0; v < count; v++)
			{
				data.Vertices.Add(ReadOneVertex(br));
			}

			// Sequential tangents for LOD0 (if present)
			if (data.TangentDataOffset > 0)
			{
				fs.Seek(data.TangentDataOffset, IO.SeekOrigin.Begin);
				for (int v = 0; v < count; v++)
				{
					float tx = br.ReadSingle();
					float ty = br.ReadSingle();
					float tz = br.ReadSingle();
					float tw = br.ReadSingle();
					data.Vertices[v].Tangent = new Vector3(tx, ty, tz);
					data.Vertices[v].TangentW = tw;
				}
			}
		}

		//Log.Info($"[gmod vvd] read {data.Vertices.Count} vertices");
		return data;
	}

	private static Vertex ReadOneVertex(IO.BinaryReader br)
	{
		var vx = new Vertex();
		for (int i = 0; i < MaxBonesPerVert; i++) vx.Weights[i] = br.ReadSingle();
		for (int i = 0; i < MaxBonesPerVert; i++) vx.Bones[i] = br.ReadByte();
		vx.BoneCount = br.ReadByte();
		vx.Position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
		vx.Normal = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
		vx.UV = new Vector2(br.ReadSingle(), br.ReadSingle());
		return vx;
	}
}


