using System;
using System.Collections.Generic;
using IO = System.IO;
using Sandbox;

internal static class SourceVtx
{
    public sealed class Header
    {
        public int Version;
        public int VertexCacheSize;
        public ushort MaxBonesPerStrip;
        public ushort MaxBonesPerTri;
        public int MaxBonesPerVertex;
        public int Checksum;
        public int LodCount;
        public int MaterialReplacementListOffset;
        public int BodyPartCount;
        public int BodyPartOffset;
    }

    public sealed class Summary
    {
        public Header Header = new();
        public int BodyParts;
        public int Models;
        public int Lods;
        public int Meshes;
        public int StripGroups;
        public int Strips;
        public int VtxVertices;
        public int VtxIndices;
        public int TriangleCount;
    }

    public sealed class LodInfo { public int MeshCount; }
    public sealed class ModelInfo { public List<LodInfo> Lods = new(); }
    public sealed class BodyPartInfo { public List<ModelInfo> Models = new(); }
    public sealed class Hierarchy { public List<BodyPartInfo> BodyParts = new(); }

    public static Summary Parse(string path)
    {
        using var fs = IO.File.OpenRead(path);
        using var br = new IO.BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
        var s = new Summary();

        // Header
        s.Header.Version = br.ReadInt32();
        s.Header.VertexCacheSize = br.ReadInt32();
        s.Header.MaxBonesPerStrip = br.ReadUInt16();
        s.Header.MaxBonesPerTri = br.ReadUInt16();
        s.Header.MaxBonesPerVertex = br.ReadInt32();
        s.Header.Checksum = br.ReadInt32();
        s.Header.LodCount = br.ReadInt32();
        s.Header.MaterialReplacementListOffset = br.ReadInt32();
        s.Header.BodyPartCount = br.ReadInt32();
        s.Header.BodyPartOffset = br.ReadInt32();

        // Walk bodyparts/models/lods/meshes/stripgroups to accumulate counts
        if (s.Header.BodyPartCount > 0 && s.Header.BodyPartOffset > 0)
        {
            long bodyPartsArray = s.Header.BodyPartOffset;
            s.BodyParts = s.Header.BodyPartCount;
            for (int bp = 0; bp < s.Header.BodyPartCount; bp++)
            {
                long bodyPartPos = bodyPartsArray + bp * 8; // int modelCount, int modelOffset
                fs.Seek(bodyPartPos, IO.SeekOrigin.Begin);
                int modelCount = br.ReadInt32();
                int modelOffset = br.ReadInt32();
                s.Models += Math.Max(0, modelCount);

                if (modelCount > 0 && modelOffset > 0)
                {
                    long modelsArray = bodyPartPos + modelOffset;
                    for (int m = 0; m < modelCount; m++)
                    {
                        long modelPos = modelsArray + m * 8; // int lodCount, int lodOffset
                        fs.Seek(modelPos, IO.SeekOrigin.Begin);
                        int lodCount = br.ReadInt32();
                        int lodOffset = br.ReadInt32();
                        s.Lods += Math.Max(0, lodCount);
                        if (lodCount > 0 && lodOffset > 0)
                        {
                            long lodsArray = modelPos + lodOffset;
                            for (int l = 0; l < lodCount; l++)
                            {
                                long lodPos = lodsArray + l * 12; // int meshCount, int meshOffset, float switchPoint
                                fs.Seek(lodPos, IO.SeekOrigin.Begin);
                                int meshCount = br.ReadInt32();
                                int meshOffset = br.ReadInt32();
                                br.ReadSingle(); // switchPoint
                                s.Meshes += Math.Max(0, meshCount);
                                if (meshCount > 0 && meshOffset > 0)
                                {
                                    long meshesArray = lodPos + meshOffset;
                                    for (int me = 0; me < meshCount; me++)
                                    {
                                        long meshPos = meshesArray + me * 9; // int stripGroupCount, int stripGroupOffset, byte flags
                                        fs.Seek(meshPos, IO.SeekOrigin.Begin);
                                        int stripGroupCount = br.ReadInt32();
                                        int stripGroupOffset = br.ReadInt32();
                                        br.ReadByte(); // flags
                                        s.StripGroups += Math.Max(0, stripGroupCount);
                                        if (stripGroupCount > 0 && stripGroupOffset > 0)
                                        {
                                            long stripGroupsArray = meshPos + stripGroupOffset;
                                            for (int sg = 0; sg < stripGroupCount; sg++)
                                            {
                                                long sgPos = stripGroupsArray + sg * 25; // vertexCount, vertexOffset, indexCount, indexOffset, stripCount, stripOffset, flags
                                                fs.Seek(sgPos, IO.SeekOrigin.Begin);
                                                int vertexCount = br.ReadInt32();
                                                int vertexOffset = br.ReadInt32();
                                                int indexCount = br.ReadInt32();
                                                int indexOffset = br.ReadInt32();
                                                int stripCount = br.ReadInt32();
                                                int stripOffset = br.ReadInt32();
                                                br.ReadByte(); // flags

                                                s.VtxVertices += Math.Max(0, vertexCount);
                                                s.VtxIndices += Math.Max(0, indexCount);
                                                s.Strips += Math.Max(0, stripCount);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        s.TriangleCount = s.VtxIndices / 3;
        //Log.Info($"[gmod vtx] ver={s.Header.Version} bodyparts={s.BodyParts} models={s.Models} lods={s.Lods} meshes={s.Meshes} stripgroups={s.StripGroups} idx={s.VtxIndices} (~{s.TriangleCount} tris)");
        return s;
    }

    public static Hierarchy ParseHierarchy(string path)
    {
        using var fs = IO.File.OpenRead(path);
        using var br = new IO.BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);
        var h = new Hierarchy();

        int version = br.ReadInt32();
        int vertexCacheSize = br.ReadInt32();
        ushort maxBonesPerStrip = br.ReadUInt16();
        ushort maxBonesPerTri = br.ReadUInt16();
        int maxBonesPerVertex = br.ReadInt32();
        int checksum = br.ReadInt32();
        int lodCount = br.ReadInt32();
        int matReplOffset = br.ReadInt32();
        int bodyPartCount = br.ReadInt32();
        int bodyPartOffset = br.ReadInt32();

        if (bodyPartCount <= 0 || bodyPartOffset <= 0)
            return h;

        long bodyPartsArray = bodyPartOffset;
        for (int bp = 0; bp < bodyPartCount; bp++)
        {
            long bodyPartPos = bodyPartsArray + bp * 8;
            fs.Seek(bodyPartPos, IO.SeekOrigin.Begin);
            int modelCount = br.ReadInt32();
            int modelOffset = br.ReadInt32();

            var bpInfo = new BodyPartInfo();
            if (modelCount > 0 && modelOffset > 0)
            {
                long modelsArray = bodyPartPos + modelOffset;
                for (int m = 0; m < modelCount; m++)
                {
                    long modelPos = modelsArray + m * 8;
                    fs.Seek(modelPos, IO.SeekOrigin.Begin);
                    int lodCountM = br.ReadInt32();
                    int lodOffset = br.ReadInt32();

                    var modelInfo = new ModelInfo();
                    if (lodCountM > 0 && lodOffset > 0)
                    {
                        long lodsArray = modelPos + lodOffset;
                        for (int l = 0; l < lodCountM; l++)
                        {
                            long lodPos = lodsArray + l * 12;
                            fs.Seek(lodPos, IO.SeekOrigin.Begin);
                            int meshCount = br.ReadInt32();
                            int meshOffset = br.ReadInt32();
                            br.ReadSingle();
                            modelInfo.Lods.Add(new LodInfo { MeshCount = meshCount });
                        }
                    }
                    bpInfo.Models.Add(modelInfo);
                }
            }
            h.BodyParts.Add(bpInfo);
        }
        return h;
    }

    // Returns originalMeshVertexIndex for each referenced vertex (triangle order) for LOD0 of specific mesh
    public static List<int> ReadLod0MeshOriginalIndices(string path, int bodyPartIndex, int modelIndex, int meshIndex)
    {
        var result = new List<int>();
        using var fs = IO.File.OpenRead(path);
        using var br = new IO.BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);

        int version = br.ReadInt32();
        br.ReadInt32(); // vertexCacheSize
        br.ReadUInt16(); // maxBonesPerStrip
        br.ReadUInt16(); // maxBonesPerTri
        br.ReadInt32(); // maxBonesPerVertex
        br.ReadInt32(); // checksum
        br.ReadInt32(); // lodCount
        br.ReadInt32(); // mat repl offset
        int bodyPartCount = br.ReadInt32();
        int bodyPartOffset = br.ReadInt32();

        if (bodyPartIndex < 0 || bodyPartIndex >= bodyPartCount) return result;
        long bodyPartPos = bodyPartOffset + bodyPartIndex * 8;
        fs.Seek(bodyPartPos, IO.SeekOrigin.Begin);
        int modelCount = br.ReadInt32();
        int modelOffset = br.ReadInt32();
        if (modelIndex < 0 || modelIndex >= modelCount || modelOffset <= 0) return result;

        long modelsArray = bodyPartPos + modelOffset;
        long modelPos = modelsArray + modelIndex * 8;
        fs.Seek(modelPos, IO.SeekOrigin.Begin);
        int lodCount = br.ReadInt32();
        int lodOffset = br.ReadInt32();
        if (lodCount <= 0 || lodOffset <= 0) return result;

        long lodsArray = modelPos + lodOffset;
        long lodPos = lodsArray + 0 * 12; // LOD0
        fs.Seek(lodPos, IO.SeekOrigin.Begin);
        int meshCount = br.ReadInt32();
        int meshOffset = br.ReadInt32();
        br.ReadSingle(); // switchPoint
        if (meshIndex < 0 || meshIndex >= meshCount || meshOffset <= 0) return result;

        long meshesArray = lodPos + meshOffset;
        long meshPos = meshesArray + meshIndex * 9;
        fs.Seek(meshPos, IO.SeekOrigin.Begin);
        int stripGroupCount = br.ReadInt32();
        int stripGroupOffset = br.ReadInt32();
        br.ReadByte(); // flags
        if (stripGroupCount <= 0 || stripGroupOffset <= 0) return result;

        long stripGroupsArray = meshPos + stripGroupOffset;
        for (int sg = 0; sg < stripGroupCount; sg++)
        {
            long sgPos = stripGroupsArray + sg * 25;
            fs.Seek(sgPos, IO.SeekOrigin.Begin);
            int vertexCount = br.ReadInt32();
            int vertexOffset = br.ReadInt32();
            int indexCount = br.ReadInt32();
            int indexOffset = br.ReadInt32();
            int stripCount = br.ReadInt32();
            int stripOffset = br.ReadInt32();
            br.ReadByte(); // flags

            // Read stripgroup vertices (to map local vtx vertex index to originalMeshVertexIndex)
            var localOrig = new int[Math.Max(0, vertexCount)];
            if (vertexCount > 0 && vertexOffset > 0)
            {
                long vtxVertsPos = sgPos + vertexOffset;
                fs.Seek(vtxVertsPos, IO.SeekOrigin.Begin);
                for (int v = 0; v < vertexCount; v++)
                {
                    // struct: byte boneWeightIndex[3], byte boneCount, ushort originalMeshVertexIndex, byte boneId[3]
                    br.ReadBytes(3); // boneWeightIndex
                    br.ReadByte(); // boneCount
                    ushort orig = br.ReadUInt16();
                    localOrig[v] = orig;
                    br.ReadBytes(3); // boneId
                }
            }
            // Read indices and map through localOrig
            if (indexCount > 0 && indexOffset > 0)
            {
                long idxPos = sgPos + indexOffset;
                fs.Seek(idxPos, IO.SeekOrigin.Begin);
                for (int i = 0; i < indexCount; i++)
                {
                    ushort local = br.ReadUInt16();
                    int orig = (local >= 0 && local < localOrig.Length) ? localOrig[local] : 0;
                    result.Add(orig);
                }
            }
        }

        return result;
    }

    // Returns the material index for a given mesh at LOD0
    public static int ReadLod0MeshMaterialIndex(string path, int bodyPartIndex, int modelIndex, int meshIndex)
    {
        try
        {
            using var fs = IO.File.OpenRead(path);
            using var br = new IO.BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: false);

            int version = br.ReadInt32();
            br.ReadInt32(); // vertexCacheSize
            br.ReadUInt16(); // maxBonesPerStrip
            br.ReadUInt16(); // maxBonesPerTri
            br.ReadInt32(); // maxBonesPerVertex
            br.ReadInt32(); // checksum
            br.ReadInt32(); // lodCount
            br.ReadInt32(); // mat repl offset
            int bodyPartCount = br.ReadInt32();
            int bodyPartOffset = br.ReadInt32();

            if (bodyPartIndex < 0 || bodyPartIndex >= bodyPartCount) return -1;
            long bodyPartPos = bodyPartOffset + bodyPartIndex * 8;
            fs.Seek(bodyPartPos, IO.SeekOrigin.Begin);
            int modelCount = br.ReadInt32();
            int modelOffset = br.ReadInt32();
            if (modelIndex < 0 || modelIndex >= modelCount || modelOffset <= 0) return -1;

            long modelsArray = bodyPartPos + modelOffset;
            long modelPos = modelsArray + modelIndex * 8;
            fs.Seek(modelPos, IO.SeekOrigin.Begin);
            int lodCount = br.ReadInt32();
            int lodOffset = br.ReadInt32();
            if (lodCount <= 0 || lodOffset <= 0) return -1;

            long lodsArray = modelPos + lodOffset;
            long lodPos = lodsArray + 0 * 12; // LOD0
            fs.Seek(lodPos, IO.SeekOrigin.Begin);
            int meshCount = br.ReadInt32();
            int meshOffset = br.ReadInt32();
            br.ReadSingle(); // switchPoint
            if (meshIndex < 0 || meshIndex >= meshCount || meshOffset <= 0) return -1;

            long meshesArray = lodPos + meshOffset;
            long meshPos = meshesArray + meshIndex * 9;
            fs.Seek(meshPos, IO.SeekOrigin.Begin);
            int material = br.ReadInt32();
            return material;
        }
        catch { return -1; }
    }
}


