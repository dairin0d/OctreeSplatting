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
            public int MinX, MinY, MaxX, MaxY;
            public int MaxSize;
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
        
        private StackItem rootInfo;
        
        private int extentX, extentY, extentZ;
        
        private int XX, XY, XZ;
        private int YX, YY, YZ;
        private int ZX, ZY, ZZ;
        private int TX, TY, TZ;
        
        public unsafe void Render() {
            int maxLevel = CalculateMaxLevel();
            if (maxLevel < 0) return;
            
            CalculateIntMatrix(maxLevel);
            
            CalculateRootInfo();
            if (rootInfo.Z < 0) return;
            if ((rootInfo.MaxX < rootInfo.MinX) | (rootInfo.MaxY < rootInfo.MinY)) return;
            
            var queues = OctantOrder.SparseQueues;
            int forwardKey = OctantOrder.Key(in Matrix);
            int reverseKey = forwardKey ^ 0b11100000000;
            
            StackItem* nodeStackPtr = stackalloc StackItem[MaxSubdivisions * 8];
            
            nodeStackPtr[0] = rootInfo;
            
            Delta* deltasPtr = stackalloc Delta[8];
            
            CalculateDeltas(deltasPtr);
            
            fixed (PixelData* pixelsPtr = Pixels)
            fixed (OctreeNode* octreePtr = Octree)
            fixed (OctantOrder.Queue* queuesPtr = queues)
            {
                var unsafeRenderer = new OctreeRendererUnsafe {
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
            
            rootInfo.Level = 0;
            rootInfo.Address = RootAddress;
            rootInfo.X = TX;
            rootInfo.Y = TY;
            rootInfo.Z = TZ - extentZ;
            CalculateRootRect();
        }
        
        private void CalculateRootRect() {
            int nodeExtentX = extentX - SubpixelHalf;
            int nodeExtentY = extentY - SubpixelHalf;
            
            rootInfo.MinX = (rootInfo.X - nodeExtentX) >> SubpixelBits;
            rootInfo.MinY = (rootInfo.Y - nodeExtentY) >> SubpixelBits;
            rootInfo.MaxX = (rootInfo.X + nodeExtentX) >> SubpixelBits;
            rootInfo.MaxY = (rootInfo.Y + nodeExtentY) >> SubpixelBits;
            
            int width = rootInfo.MaxX - rootInfo.MinX;
            int height = rootInfo.MaxY - rootInfo.MinY;
            rootInfo.MaxSize = (width > height ? width : height);
            
            if (rootInfo.MinX < Viewport.MinX) rootInfo.MinX = Viewport.MinX;
            if (rootInfo.MinY < Viewport.MinY) rootInfo.MinY = Viewport.MinY;
            if (rootInfo.MaxX > Viewport.MaxX) rootInfo.MaxX = Viewport.MaxX;
            if (rootInfo.MaxY > Viewport.MaxY) rootInfo.MaxY = Viewport.MaxY;
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
                    
                    ref var node = ref Octree[current.Address];
                    
                    if (current.MaxSize < 1) {
                        int i = current.MinX + (current.MinY << BufferShift);
                        if (current.Z < Pixels[i].Depth) {
                            Pixels[i].Depth = current.Z;
                            Pixels[i].Color24 = node.Data;
                        }
                        continue;
                    } else if (node.Mask == 0) {
                        int j = current.MinX + (current.MinY << BufferShift);
                        int jEnd = current.MinX + (current.MaxY << BufferShift);
                        int iEnd = current.MaxX + (current.MinY << BufferShift);
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
                        int j = current.MinX + (current.MinY << BufferShift);
                        int jEnd = current.MinX + (current.MaxY << BufferShift);
                        int iEnd = current.MaxX + (current.MinY << BufferShift);
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
                    
                    int nextLevel = current.Level + 1;
                    int nodeExtentX = (ExtentX >> nextLevel) - SubpixelHalf;
                    int nodeExtentY = (ExtentY >> nextLevel) - SubpixelHalf;
                    
                    for (; queue != 0; queue >>= 4) {
                        uint octant = queue & 7;
                        
                        ref var delta = ref Deltas[octant];
                        
                        int x = current.X + (delta.X >> current.Level);
                        int y = current.Y + (delta.Y >> current.Level);
                        
                        int minX = (x - nodeExtentX) >> SubpixelBits;
                        int minY = (y - nodeExtentY) >> SubpixelBits;
                        int maxX = (x + nodeExtentX) >> SubpixelBits;
                        int maxY = (y + nodeExtentY) >> SubpixelBits;
                        
                        int width = maxX - minX;
                        int height = maxY - minY;
                        int maxSize = (width > height ? width : height);
                        
                        if (minX < current.MinX) minX = current.MinX;
                        if (minY < current.MinY) minY = current.MinY;
                        if (maxX > current.MaxX) maxX = current.MaxX;
                        if (maxY > current.MaxY) maxY = current.MaxY;
                        
                        if ((maxX < minX) | (maxY < minY)) continue;
                        
                        ++stackTop;
                        stackTop->MinX = minX;
                        stackTop->MinY = minY;
                        stackTop->MaxX = maxX;
                        stackTop->MaxY = maxY;
                        stackTop->MaxSize = maxSize;
                        stackTop->X = x;
                        stackTop->Y = y;
                        stackTop->Z = current.Z + (delta.Z >> current.Level);
                        stackTop->Address = node.Address + octant;
                        stackTop->Level = nextLevel;
                    }
                }
            }
        }
    }
}
