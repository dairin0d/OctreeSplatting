// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System;
using System.Numerics;

namespace OctreeSplatting {
    public class OctreeRenderer {
        private struct Delta {
            public int X, Y, Z;
            private int pad0;
        }
        
        private struct StackItem {
            public int X, Y, Z;
            public uint Address;
            public int Level;
        }
        
        private const int SubpixelBits = 16;
        private const int SubpixelSize = 1 << SubpixelBits;
        private const int SubpixelHalf = SubpixelSize >> 1;
        private const int MaxSubdivisions = 31 - SubpixelBits;
        
        // Viewport & renderbuffer info
        public Range2D Viewport;
        public int BufferShift;
        public PixelData[] Pixels;
        
        // Model info
        public Matrix4x4 Matrix;
        public OctreeNode[] Octree;
        public uint RootAddress;
        
        private int extentX, extentY, extentZ;
        private int startX, startY, startZ;
        
        private int XX, XY, XZ;
        private int YX, YY, YZ;
        private int ZX, ZY, ZZ;
        private int TX, TY, TZ;
        
        public unsafe void Render() {
            if (!Setup()) return;
            
            var queues = OctantOrder.SparseQueues;
            int forwardKey = OctantOrder.Key(in Matrix);
            int reverseKey = forwardKey ^ 0b11100000000;
            
            StackItem* nodeStackPtr = stackalloc StackItem[MaxSubdivisions * 8];
            
            nodeStackPtr[0] = new StackItem {
                X = startX,
                Y = startY,
                Z = startZ,
                Address = RootAddress,
                Level = 0,
            };
            
            Delta* deltasPtr = stackalloc Delta[8];
            
            CalculateDeltas(deltasPtr);
            
            fixed (PixelData* pixelsPtr = Pixels)
            fixed (OctreeNode* octreePtr = Octree)
            fixed (OctantOrder.Queue* queuesPtr = queues)
            {
                var unsafeRenderer = new OctreeRendererUnsafe {
                    Viewport = Viewport,
                    BufferShift = BufferShift,
                    Pixels = pixelsPtr,
                    
                    Octree = octreePtr,
                    
                    ExtentX = extentX,
                    ExtentY = extentY,
                    Deltas = deltasPtr,
                    NodeStack = nodeStackPtr,
                    
                    ForwardQueues = queuesPtr + forwardKey,
                    ReverseQueues = queuesPtr + reverseKey,
                };
                unsafeRenderer.Render();
            }
        }
        
        private bool Setup() {
            int maxLevel = CalculateMaxLevel();
            
            if (maxLevel < 0) return false;
            
            CalculateIntMatrix(maxLevel);
            
            CalculateRootInfo();
            
            return startZ >= 0;
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
        
        private unsafe void CalculateDeltas(Delta* deltas) {
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
        
        private unsafe struct OctreeRendererUnsafe {
            public Range2D Viewport;
            public int BufferShift;
            public PixelData* Pixels;
            
            public OctreeNode* Octree;
            
            public int ExtentX, ExtentY;
            public Delta* Deltas;
            public StackItem* NodeStack;
            
            public OctantOrder.Queue* ForwardQueues;
            public OctantOrder.Queue* ReverseQueues;
            
            public void Render() {
                var stackTop = NodeStack;
                
                while (stackTop >= NodeStack) {
                    // We need a copy anyway for subnode processing
                    var current = *stackTop;
                    --stackTop;
                    
                    int nodeExtentX = (ExtentX >> current.Level) - SubpixelHalf;
                    int nodeExtentY = (ExtentY >> current.Level) - SubpixelHalf;
                    
                    int minX = (current.X - nodeExtentX) >> SubpixelBits;
                    int minY = (current.Y - nodeExtentY) >> SubpixelBits;
                    int maxX = (current.X + nodeExtentX) >> SubpixelBits;
                    int maxY = (current.Y + nodeExtentY) >> SubpixelBits;
                    
                    bool isPixelSize = (maxX - minX < 1) & (maxY - minY < 1);
                    
                    if (minX < Viewport.MinX) minX = Viewport.MinX;
                    if (minY < Viewport.MinY) minY = Viewport.MinY;
                    if (maxX > Viewport.MaxX) maxX = Viewport.MaxX;
                    if (maxY > Viewport.MaxY) maxY = Viewport.MaxY;
                    
                    if ((maxX < minX) | (maxY < minY)) continue;
                    
                    ref var node = ref Octree[current.Address];
                    
                    if (isPixelSize) {
                        int i = minX + (minY << BufferShift);
                        if (current.Z < Pixels[i].Depth) {
                            Pixels[i].Depth = current.Z;
                            Pixels[i].Color24 = node.Data;
                        }
                        continue;
                    } else if (node.Mask == 0) {
                        int j = minX + (minY << BufferShift);
                        int jEnd = minX + (maxY << BufferShift);
                        int iEnd = maxX + (minY << BufferShift);
                        int jStep = 1 << BufferShift;
                        for (; j <= jEnd; j += jStep, iEnd += jStep) {
                            for (int i = j; i <= iEnd; i++) {
                                if (current.Z < Pixels[i].Depth) {
                                    Pixels[i].Depth = current.Z;
                                    Pixels[i].Color24 = node.Data;
                                }
                            }
                        }
                        continue;
                    }
                    
                    {
                        int j = minX + (minY << BufferShift);
                        int jEnd = minX + (maxY << BufferShift);
                        int iEnd = maxX + (minY << BufferShift);
                        int jStep = 1 << BufferShift;
                        for (; j <= jEnd; j += jStep, iEnd += jStep) {
                            for (int i = j; i <= iEnd; i++) {
                                if (current.Z < Pixels[i].Depth) goto OcclusionTestPassed;
                            }
                        }
                    }
                    continue;
                    OcclusionTestPassed:;
                    
                    var queue = ReverseQueues[node.Mask].Octants;
                    
                    for (; queue != 0; queue >>= 4) {
                        uint octant = queue & 7;
                        
                        if ((node.Mask & (1 << (int)octant)) == 0) continue;
                        
                        ref var delta = ref Deltas[octant];
                        
                        ++stackTop;
                        stackTop->X = current.X + (delta.X >> current.Level);
                        stackTop->Y = current.Y + (delta.Y >> current.Level);
                        stackTop->Z = current.Z + (delta.Z >> current.Level);
                        stackTop->Address = node.Address + octant;
                        stackTop->Level = current.Level + 1;
                    }
                }
            }
        }
    }
}
