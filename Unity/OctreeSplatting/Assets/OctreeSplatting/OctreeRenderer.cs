// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OctreeSplatting {
    public class OctreeRenderer {
        private struct Delta {
            public int X, Y, Z;
        }
        
        private const int SubpixelBits = 16;
        private const int SubpixelSize = 1 << SubpixelBits;
        private const int SubpixelHalf = SubpixelSize >> 1;
        
        // Viewport & renderbuffer info
        public Range2D Viewport;
        public int BufferStride;
        public PixelData[] Pixels;
        
        // Model info
        public Matrix4x4 Matrix;
        public OctreeNode[] Octree;
        public uint RootAddress;
        
        private int extentX, extentY, extentZ;
        private int startX, startY, startZ;
        private Delta[] deltas = new Delta[8];
        private uint queue;
        
        private int XX, XY, XZ;
        private int YX, YY, YZ;
        private int ZX, ZY, ZZ;
        private int TX, TY, TZ;
        
        public void Render() {
            if (!Setup()) return;
            
            Render(startX, startY, startZ, RootAddress, 0);
        }
        
        private void Render(int nodeX, int nodeY, int nodeZ, uint address, int level) {
            var boundingRect = CalculateScreenSpaceBoundaries(nodeX, nodeY, level);
            
            var visibleRect = boundingRect.Intersection(Viewport);
            
            if ((visibleRect.SizeX < 0) | (visibleRect.SizeY < 0)) return;
            
            var node = Octree[address];
            
            var projectedSize = Math.Max(boundingRect.SizeX, boundingRect.SizeY);
            
            if ((node.Mask == 0) | (projectedSize < 1)) {
                Draw(visibleRect, nodeZ, node);
                return;
            }
            
            if (IsFullyOccluded(visibleRect, nodeZ)) return;
            
            for (int i = 0; i < 8; i++) {
                uint octant = (queue >> (i*4)) & 7;
                
                if ((node.Mask & (1 << (int)octant)) == 0) continue;
                
                var delta = deltas[octant];
                
                int childX = nodeX + (delta.X >> level);
                int childY = nodeY + (delta.Y >> level);
                int childZ = nodeZ + (delta.Z >> level);
                var childAddress = node.Address + octant;
                
                Render(childX, childY, childZ, childAddress, level+1);
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Range2D CalculateScreenSpaceBoundaries(int nodeX, int nodeY, int level) {
            var nodeExtentX = (extentX >> level) - SubpixelHalf;
            var nodeExtentY = (extentY >> level) - SubpixelHalf;
            
            return new Range2D {
                MinX = (nodeX - nodeExtentX) >> SubpixelBits,
                MinY = (nodeY - nodeExtentY) >> SubpixelBits,
                MaxX = (nodeX + nodeExtentX) >> SubpixelBits,
                MaxY = (nodeY + nodeExtentY) >> SubpixelBits,
            };
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Draw(Range2D rect, int nodeZ, OctreeNode node) {
            for (int y = rect.MinY; y <= rect.MaxY; y++) {
                int index = rect.MinX + (y * BufferStride);
                for (int x = rect.MinX; x <= rect.MaxX; x++, index++) {
                    ref var pixel = ref Pixels[index];
                    if (nodeZ < pixel.Depth) {
                        pixel.Depth = nodeZ;
                        pixel.Color24 = node.Data;
                    }
                }
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsFullyOccluded(Range2D rect, int nodeZ) {
            for (int y = rect.MinY; y <= rect.MaxY; y++) {
                int index = rect.MinX + (y * BufferStride);
                for (int x = rect.MinX; x <= rect.MaxX; x++, index++) {
                    ref var pixel = ref Pixels[index];
                    if (nodeZ < pixel.Depth) return false;
                }
            }
            return true;
        }
        
        private bool Setup() {
            int maxLevel = CalculateMaxLevel();
            
            if (maxLevel < 0) return false;
            
            CalculateIntMatrix(maxLevel);
            
            CalculateRootInfo();
            
            if (startZ < 0) return false;
            
            CalculateDeltas();
            
            CalculateQueue();
            
            return true;
        }
        
        private int CalculateMaxLevel() {
            float absXX = Math.Abs(Matrix.M11);
            float absXY = Math.Abs(Matrix.M12);
            float absYX = Math.Abs(Matrix.M21);
            float absYY = Math.Abs(Matrix.M22);
            float absZX = Math.Abs(Matrix.M31);
            float absZY = Math.Abs(Matrix.M32);
            
            float maxGap = 0;
            maxGap = Math.Max(maxGap, absXX + absYX + absZX);
            maxGap = Math.Max(maxGap, absXY + absYY + absZY);
            
            int maxShift = 30 - SubpixelBits;
            
            for (int maxLevel = 0; maxLevel <= maxShift; maxLevel++) {
                if (maxGap < (1 << maxLevel)) return maxLevel;
            }
            
            return -1; // too big; can't render
        }
        
        private void CalculateIntMatrix(int maxLevel) {
            float levelScale = (float)Math.Pow(2, SubpixelBits - maxLevel);
            
            XX = (int)(Matrix.M11 * levelScale);
            XY = (int)(Matrix.M12 * levelScale);
            XZ = (int)(Matrix.M13);
            YX = (int)(Matrix.M21 * levelScale);
            YY = (int)(Matrix.M22 * levelScale);
            YZ = (int)(Matrix.M23);
            ZX = (int)(Matrix.M31 * levelScale);
            ZY = (int)(Matrix.M32 * levelScale);
            ZZ = (int)(Matrix.M33);
            TX = (int)(Matrix.M41 * SubpixelSize);
            TY = (int)(Matrix.M42 * SubpixelSize);
            TZ = (int)(Matrix.M43);
            
            XX >>= 1; XY >>= 1; XZ >>= 1;
            YX >>= 1; YY >>= 1; YZ >>= 1;
            ZX >>= 1; ZY >>= 1; ZZ >>= 1;
            
            XX <<= maxLevel; XY <<= maxLevel;
            YX <<= maxLevel; YY <<= maxLevel;
            ZX <<= maxLevel; ZY <<= maxLevel;
        }
        
        private void CalculateRootInfo() {
            extentX = (Math.Abs(XX) + Math.Abs(YX) + Math.Abs(ZX)) << 1;
            extentY = (Math.Abs(XY) + Math.Abs(YY) + Math.Abs(ZY)) << 1;
            extentZ = (Math.Abs(XZ) + Math.Abs(YZ) + Math.Abs(ZZ)) << 1;
            
            startX = TX;
            startY = TY;
            startZ = TZ - extentZ;
        }
        
        private void CalculateDeltas() {
            int offsetZ = extentZ >> 1;
            int octant = 0;
            for (int z = -1; z <= 1; z += 2) {
                for (int y = -1; y <= 1; y += 2) {
                    for (int x = -1; x <= 1; x += 2) {
                        deltas[octant].X = (XX * x + YX * y + ZX * z);
                        deltas[octant].Y = (XY * x + YY * y + ZY * z);
                        deltas[octant].Z = (XZ * x + YZ * y + ZZ * z) + offsetZ;
                        octant++;
                    }
                }
            }
        }
        
        private void CalculateQueue() {
            int axisOrder = OctantOrder.Order(in Matrix);
            int startingOctant = OctantOrder.Octant(in Matrix);
            int nodeMask = 255;
            
            int lookupIndex = axisOrder;
            lookupIndex = (lookupIndex << 3) | startingOctant;
            lookupIndex = (lookupIndex << 8) | nodeMask;
            
            queue = OctantOrder.SparseQueues[lookupIndex].Octants;
        }
    }
}
