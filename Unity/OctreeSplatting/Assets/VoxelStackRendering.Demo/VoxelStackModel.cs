// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VoxelStackRendering.Demo {
    public class VoxelStackModel {
        public struct ColumnInfo {
            public int SpanStart;
            public ushort SpanCount;
            public ushort DataCount;
        }
        
        public int SizeX;
        public int SizeY;
        public int SizeZ;
        public ColumnInfo[] Grid;
        public byte[] SpanData;
        
        public int ClampX(int x) {
            return Mathf.Clamp(x, 0, SizeX - 1);
        }
        public int ClampY(int y) {
            return Mathf.Clamp(y, 0, SizeY - 1);
        }
        public void ClampXY(ref int x, ref int y) {
            x = ClampX(x);
            y = ClampY(y);
        }
        
        public void FromHeightmap(Texture2D heightmap, Texture2D colormap = null) {
            FromHeightmap(heightmap.width, heightmap.height, heightmap.GetPixels32(), colormap?.GetPixels32());
        }
        
        public void FromHeightmap(int sizeX, int sizeY, Color32[] heightmap, Color32[] colormap = null) {
            if (colormap == null) colormap = heightmap;
            
            SizeX = sizeX;
            SizeY = sizeY;
            SizeZ = 255;
            
            Grid = new ColumnInfo[SizeX * SizeY];
            
            using (var stream = new MemoryStream()) {
                var writer = new BinaryWriter(stream);
                
                int pos = 0;
                
                for (int y = 0; y < SizeY; y++) {
                    for (int x = 0; x < SizeX; x++) {
                        int i = x + y * SizeX;
                        var h = CalcHeight(heightmap[i]);
                        var c = colormap[i];
                        
                        var hOther = Mathf.Min(Mathf.Min(CalcHeightXY(x-1, y), CalcHeightXY(x+1, y)),
                            Mathf.Min(CalcHeightXY(x, y-1), CalcHeightXY(x, y+1)));
                        
                        int hMin = Mathf.Min(hOther, h);
                        int hMax = h + 1;
                        int hSize = Mathf.Min(hMax - hMin, 255);
                        hMin = hMax - hSize;
                        
                        Grid[i].SpanStart = pos;
                        Grid[i].SpanCount = 3;
                        Grid[i].DataCount = (ushort)hSize;
                        
                        writer.Write((byte)hMin); writer.Write((byte)0); pos += 2;
                        writer.Write((byte)hSize); writer.Write((byte)255); pos += 2;
                        writer.Write((byte)(256-hMax)); writer.Write((byte)0); pos += 2;
                        
                        for (; hSize >= 0; hSize--) {
                            writer.Write(c.r);
                            writer.Write(c.g);
                            writer.Write(c.b);
                            pos += 3;
                        }
                    }
                }
                
                writer.Flush();
                SpanData = stream.ToArray();
            }
            
            int CalcHeight(Color32 hc) => (byte)((hc.r + hc.g + hc.b) / 3);
            int CalcHeightXY(int x, int y) {
                if ((x < 0) | (x >= SizeX) | (y < 0) | (y >= SizeY)) return 0;
                return CalcHeight(heightmap[x + y * SizeX]);
            }
        }
        
        public void FromOctree(string path, int maxLevel) {
            FromOctree(LoadOctree(path), maxLevel);
        }
        
        public void FromOctree(OctreeSplatting.OctreeNode[] octree, int maxLevel) {
            SizeX = SizeY = SizeZ = 1 << maxLevel;
            
            Grid = new ColumnInfo[SizeX * SizeY];
            
            var spansGrid = new List<int>[SizeX * SizeY];
            var colorsGrid = new List<OctreeSplatting.Color24>[SizeX * SizeY];
            
            for (int y = 0; y < SizeY; y++) {
                for (int x = 0; x < SizeX; x++) {
                    int i = x + y * SizeX;
                    spansGrid[i] = new List<int>();
                    colorsGrid[i] = new List<OctreeSplatting.Color24>();
                }
            }
            
            Traverse(0, 0, 0, 0, 0);
            
            using (var stream = new MemoryStream()) {
                var writer = new BinaryWriter(stream);
                
                int pos = 0;
                
                for (int y = 0; y < SizeY; y++) {
                    for (int x = 0; x < SizeX; x++) {
                        int i = x + y * SizeX;
                        
                        Grid[i].SpanStart = pos;
                        Grid[i].DataCount = 0;
                        Grid[i].SpanCount = 0;
                        
                        foreach (var (size, alpha) in EnumerateSpans(spansGrid[i])) {
                            writer.Write((byte)size); writer.Write((byte)alpha); pos += 2;
                            Grid[i].SpanCount++;
                        }
                        
                        foreach (var color in colorsGrid[i]) {
                            writer.Write((byte)color.R);
                            writer.Write((byte)color.G);
                            writer.Write((byte)color.B);
                            pos += 3;
                            Grid[i].DataCount++;
                        }
                    }
                }
                
                writer.Flush();
                SpanData = stream.ToArray();
            }
            
            IEnumerable<(byte size, byte alpha)> EnumerateSpans(List<int> spansList) {
                if (spansList.Count == 0) yield break;
                
                if (spansList[spansList.Count-1] < (SizeY-1)) {
                    spansList.Add(SizeY-1);
                }
                
                bool isEmpty = spansList[0] >= 0;
                for (int j = (isEmpty ? 0 : 1); j < spansList.Count; j++, isEmpty = !isEmpty) {
                    int spanSize = (j == 0 ? spansList[j] + 1 : spansList[j] - spansList[j - 1]);
                    byte alpha = (byte)(isEmpty ? 0 : 255);
                    
                    for (int h = 0; h < spanSize; h += 255) {
                        int hTop = h + 255;
                        if (hTop > spanSize) hTop = spanSize;
                        byte size = (byte)(hTop - h);
                        yield return (size, alpha);
                    }
                }
            }
            
            void Traverse(uint address, int level, int x, int y, int z) {
                ref var node = ref octree[address];
                
                if ((node.Mask == 0) | (level >= maxLevel)) {
                    int nodeSize = 1 << (maxLevel - level);
                    for (int nz = 0; nz < nodeSize; nz++) {
                        for (int ny = 0; ny < nodeSize; ny++) {
                            for (int nx = 0; nx < nodeSize; nx++) {
                                Insert(x+nx, y+ny, z+nz, node.Data);
                            }
                        }
                    }
                    return;
                }
                
                int sublevel = level + 1;
                int shift = maxLevel - sublevel;
                int octant = 0;
                for (int oz = 0; oz < 2; oz++) {
                    int subz = z + (oz << shift);
                    for (int oy = 0; oy < 2; oy++) {
                        int suby = y + (oy << shift);
                        for (int ox = 0; ox < 2; ox++) {
                            int subx = x + (ox << shift);
                            if ((node.Mask & (1 << octant)) != 0) {
                                Traverse(node.Address + (uint)octant, sublevel, subx, suby, subz);
                            }
                            octant++;
                        }
                    }
                }
            }
            
            void Insert(int x, int y, int z, OctreeSplatting.Color24 color) {
                int i = x + z * SizeX;
                var spansList = spansGrid[i];
                var colorsList = colorsGrid[i];
                
                if (spansList.Count == 0) {
                    spansList.Add(y - 1);
                    spansList.Add(y);
                } else {
                    int yPrev = spansList[spansList.Count - 1];
                    if ((y - yPrev) == 1) {
                        spansList[spansList.Count - 1] = y;
                    } else {
                        spansList.Add(y - 1);
                        spansList.Add(y);
                    }
                }
                
                colorsList.Add(color);
            }
        }
        
        private OctreeSplatting.OctreeNode[] LoadOctree(string path) {
            var asset = Resources.Load<TextAsset>(path);
            return asset ? FromBytes<OctreeSplatting.OctreeNode>(asset.bytes) : null;
        }
        
        private static T[] FromBytes<T>(byte[] bytes) where T : struct {
            if (bytes == null) return null;
            var result = new T[bytes.Length / Marshal.SizeOf(typeof(T))];
            var handle = GCHandle.Alloc(result, GCHandleType.Pinned);
            try {
                Marshal.Copy(bytes, 0, handle.AddrOfPinnedObject(), bytes.Length);
            } finally {
                if (handle.IsAllocated) handle.Free();
            }
            return result;
        }
    }
}