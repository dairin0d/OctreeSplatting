// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System;
using System.Numerics;

namespace OctreeSplatting {
    public class OctreeRenderer {
        private struct Delta {
            public int X, Y, Z;
            //private int pad0;
        }
        
        private const int SubpixelShift = 16;
        private const int SubpixelSize = 1 << SubpixelShift;
        private const int SubpixelMask = SubpixelSize - 1;
        private const int SubpixelHalf = SubpixelSize >> 1;
        
        // Renderbuffer & viewport info
        public Range2D Viewport;
        public int BufferStride;
        public PixelData[] Pixels;
        
        // Model info
        public Matrix4x4 Matrix;
        public int ObjectID;
        public OctreeNode[] Octree;
        public uint RootAddress;
        public int MaxLevel = 32;
        
        private int extentX, extentY, extentZ;
        private int startX, startY, startZ;
        private Delta[] deltas = new Delta[8];
        
        private OctantOrder.Queue[] queues;
        private int forwardKey;
        private int reverseKey;
        private uint queue;
        
        public void Render() {
            if (!Setup()) return;
            
            forwardKey = OctantOrder.Key(in Matrix);
            reverseKey = forwardKey ^ 0b11100000000;
            queues = OctantOrder.SparseQueues;
            queue = queues[forwardKey | 255].Octants;
            
            Render(startX, startY, startZ, RootAddress, 0);
        }
        
        private void Render(int nodeX, int nodeY, int nodeZ, uint address, int level) {
            var boundingBox = CalculateScreenSpaceBoundaries(nodeX, nodeY, level);
            
            var visibleBox = boundingBox.Intersection(Viewport);
            
            if ((visibleBox.SizeX < 0) | (visibleBox.SizeY < 0)) return;
            
            var projectedSize = Math.Max(boundingBox.SizeX, boundingBox.SizeY);
            
            var node = Octree[address];
            
            if ((node.Mask == 0) | (projectedSize < 1)) {
                Draw(visibleBox, nodeZ, node);
                return;
            }
            
            if (IsFullyOccluded(visibleBox, nodeZ)) return;
            
            var queue = this.queue;
            for (int i = 0; i < 8; i++, queue >>= 4) {
                uint octant = queue & 7;
                if ((node.Mask & (1 << (int)octant)) == 0) continue;
                
                var delta = deltas[octant];
                int childX = nodeX + (delta.X >> level);
                int childY = nodeY + (delta.Y >> level);
                int childZ = nodeZ + (delta.Z >> level);
                var childAddress = node.Address + octant;
                Render(childX, childY, childZ, childAddress, level+1);
            }
        }
        
        private Range2D CalculateScreenSpaceBoundaries(int nodeX, int nodeY, int level) {
            var nodeExtentX = (extentX >> level) - SubpixelHalf;
            var nodeExtentY = (extentY >> level) - SubpixelHalf;
            return new Range2D {
                MinX = (nodeX - nodeExtentX) >> SubpixelShift,
                MinY = (nodeY - nodeExtentY) >> SubpixelShift,
                MaxX = (nodeX + nodeExtentX) >> SubpixelShift,
                MaxY = (nodeY + nodeExtentY) >> SubpixelShift,
            };
        }
        
        private void Draw(Range2D boundingBox, int nodeZ, OctreeNode node) {
            for (int y = boundingBox.MinY; y <= boundingBox.MaxY; y++) {
                int index = boundingBox.MinX + (y * BufferStride);
                for (int x = boundingBox.MinX; x <= boundingBox.MaxX; x++, index++) {
                    ref var pixel = ref Pixels[index];
                    if (nodeZ < pixel.Depth) {
                        pixel.Depth = nodeZ;
                        pixel.Color24 = node.Data;
                    }
                }
            }
        }
        
        private bool IsFullyOccluded(Range2D boundingBox, int nodeZ) {
            for (int y = boundingBox.MinY; y <= boundingBox.MaxY; y++) {
                int index = boundingBox.MinX + (y * BufferStride);
                for (int x = boundingBox.MinX; x <= boundingBox.MaxX; x++, index++) {
                    ref var pixel = ref Pixels[index];
                    if (nodeZ < pixel.Depth) return false;
                }
            }
            return true;
        }
        
        private bool Setup() {
            // The model's matrix is expected to be in renderbuffer space,
            // which ranges from (0, 0, 0) to (SizeX, SizeY, SizeZ).
            // Its 4 rows must correspond to the X-axis, Y-axis, Z-axis
            // and Translation vectors, respectively.
            
            int maxLevel = CalculateMaxLevel(Matrix.M11, Matrix.M12, Matrix.M21, Matrix.M22, Matrix.M31, Matrix.M32);
            if (maxLevel < 0) return false;
            
            int levelDifference = SubpixelShift - maxLevel;
            float levelScale = (levelDifference >= 0 ? (1 << levelDifference) : 1f / (1 << -levelDifference));
            
            int XX = (int)(Matrix.M11*levelScale), XY = (int)(Matrix.M12*levelScale), XZ = (int)(Matrix.M13);
            int YX = (int)(Matrix.M21*levelScale), YY = (int)(Matrix.M22*levelScale), YZ = (int)(Matrix.M23);
            int ZX = (int)(Matrix.M31*levelScale), ZY = (int)(Matrix.M32*levelScale), ZZ = (int)(Matrix.M33);
            int TX = (int)(Matrix.M41*SubpixelSize), TY = (int)(Matrix.M42*SubpixelSize), TZ = (int)(Matrix.M43);
            
            XX <<= maxLevel; XY <<= maxLevel;
            YX <<= maxLevel; YY <<= maxLevel;
            ZX <<= maxLevel; ZY <<= maxLevel;
            XX >>= 1; XY >>= 1; XZ >>= 1;
            YX >>= 1; YY >>= 1; YZ >>= 1;
            ZX >>= 1; ZY >>= 1; ZZ >>= 1;
            
            extentX = ((XX < 0 ? -XX : XX) + (YX < 0 ? -YX : YX) + (ZX < 0 ? -ZX : ZX)) << 1;
            extentY = ((XY < 0 ? -XY : XY) + (YY < 0 ? -YY : YY) + (ZY < 0 ? -ZY : ZY)) << 1;
            extentZ = ((XZ < 0 ? -XZ : XZ) + (YZ < 0 ? -YZ : YZ) + (ZZ < 0 ? -ZZ : ZZ)) << 1;
            
            startX = TX;
            startY = TY;
            startZ = TZ - extentZ;
            if (startZ < 0) return false;
            
            int octant = 0;
            for (int z = -1; z <= 1; z += 2) {
                for (int y = -1; y <= 1; y += 2) {
                    for (int x = -1; x <= 1; x += 2) {
                        deltas[octant].X = (XX * x + YX * y + ZX * z);
                        deltas[octant].Y = (XY * x + YY * y + ZY * z);
                        deltas[octant].Z = (XZ * x + YZ * y + ZZ * z) + extentZ;
                        ++octant;
                    }
                }
            }
            
            return true;
        }
        
        private static int CalculateMaxLevel(float XX, float XY, float YX, float YY, float ZX, float ZY) {
            if (XX < 0) XX = -XX;
            if (XY < 0) XY = -XY;
            if (YX < 0) YX = -YX;
            if (YY < 0) YY = -YY;
            if (ZX < 0) ZX = -ZX;
            if (ZY < 0) ZY = -ZY;
            
            float maxGap = 0, gap = 0;
            gap = XX + YX; if (gap > maxGap) maxGap = gap;
            gap = XY + YY; if (gap > maxGap) maxGap = gap;
            gap = YX + ZX; if (gap > maxGap) maxGap = gap;
            gap = YY + ZY; if (gap > maxGap) maxGap = gap;
            gap = ZX + XX; if (gap > maxGap) maxGap = gap;
            gap = ZY + XY; if (gap > maxGap) maxGap = gap;
            
            for (int maxLevel = 0; maxLevel <= 30; maxLevel++) {
                if (maxGap < (1 << maxLevel)) return maxLevel;
            }
            
            return -1;
        }
    }
}
