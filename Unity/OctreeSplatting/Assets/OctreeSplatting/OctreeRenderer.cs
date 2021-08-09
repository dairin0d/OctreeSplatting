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
        
        private const int MapBits = 5;
        private const int MapSize = 1 << MapBits;
        
        // Viewport & renderbuffer info
        public Range2D Viewport;
        public int BufferShift;
        public PixelData[] Pixels;
        
        // Model info
        public Matrix4x4 Matrix;
        public OctreeNode[] Octree;
        public uint RootAddress;
        
        public int MapThreshold = 2;
        
        public int MaxLevel = -1;
        
        public float Dilation = 0; // in pixels
        public float MinSplatSize = 0; // relative to the root size
        
        public SplatShape Shape = SplatShape.Rectangle;
        
        public int DrawnPixels;
        
        private StackItem rootInfo;
        
        private int extentX, extentY, extentZ;
        
        private int XX, XY, XZ;
        private int YX, YY, YZ;
        private int ZX, ZY, ZZ;
        private int TX, TY, TZ;
        
        private int[] traceBuffer;
        
        public unsafe void Render() {
            DrawnPixels = 0;
            
            int maxLevel = CalculateMaxLevel();
            if (maxLevel < 0) return;
            
            CalculateIntMatrix(maxLevel);
            
            CalculateRootInfo();
            
            int dilation = (int)(Math.Max(Dilation, 0) * SubpixelSize);
            dilation += (int)(Math.Min(Math.Max(MinSplatSize, 0), 1) * Math.Max(extentX, extentY));
            dilation -= SubpixelHalf;
            
            CalculateRootRect(dilation);
            
            if (rootInfo.Z < 0) return;
            if ((rootInfo.MaxX < rootInfo.MinX) | (rootInfo.MaxY < rootInfo.MinY)) return;
            
            InitializeTraceBuffer();
            
            var queues = OctantOrder.SparseQueues;
            int forwardKey = OctantOrder.Key(in Matrix);
            int reverseKey = forwardKey ^ 0b11100000000;
            
            StackItem* nodeStackPtr = stackalloc StackItem[MaxSubdivisions * 8];
            
            nodeStackPtr[0] = rootInfo;
            
            Delta* deltasPtr = stackalloc Delta[8];
            CalculateDeltas(deltasPtr);
            
            byte* mapX = stackalloc byte[MapSize];
            byte* mapY = stackalloc byte[MapSize];
            int mapShift = CalculateMaps(deltasPtr, mapX, mapY);
            
            fixed (PixelData* pixelsPtr = Pixels)
            fixed (OctreeNode* octreePtr = Octree)
            fixed (OctantOrder.Queue* queuesPtr = queues)
            fixed (int* traceBufferPtr = traceBuffer)
            {
                var unsafeRenderer = new OctreeRendererUnsafe {
                    BufferShift = BufferShift,
                    Pixels = pixelsPtr,
                    
                    Octree = octreePtr,
                    
                    ExtentX = extentX,
                    ExtentY = extentY,
                    ExtentZ = extentZ,
                    Deltas = deltasPtr,
                    NodeStack = nodeStackPtr,
                    
                    ForwardQueues = queuesPtr + forwardKey,
                    ReverseQueues = queuesPtr + reverseKey,
                    
                    TraceBuffer = traceBufferPtr,
                    
                    MapX = mapX,
                    MapY = mapY,
                    MapShift = mapShift,
                    
                    MapThreshold = MapThreshold,
                    
                    MaxLevel = Math.Min(MaxLevel >= 0 ? MaxLevel : int.MaxValue, maxLevel+1),
                    
                    Dilation = dilation,
                    
                    Shape = Shape,
                };
                
                DrawnPixels = unsafeRenderer.Render();
            }
        }
        
        private void InitializeTraceBuffer() {
            int viewportArea = (Viewport.SizeX + 1) * (Viewport.SizeY + 1);
            int traceBufferSize = (traceBuffer != null ? traceBuffer.Length : 0);
            
            if (viewportArea <= traceBufferSize) return;
            
            if (traceBufferSize == 0) traceBufferSize = 1;
            
            while (traceBufferSize < viewportArea) traceBufferSize *= 2;
            
            traceBuffer = new int[traceBufferSize];
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
            
            int maxShift = 29 - SubpixelBits;
            
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
            
            if (Shape == SplatShape.Square) {
                extentX = extentY = Math.Max(extentX, extentY);
            }
            
            rootInfo.Level = 0;
            rootInfo.Address = RootAddress;
            rootInfo.X = TX;
            rootInfo.Y = TY;
            rootInfo.Z = TZ - extentZ;
        }
        
        private void CalculateRootRect(int dilation) {
            int nodeExtentX = extentX + dilation;
            int nodeExtentY = extentY + dilation;
            
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
        
        private unsafe int CalculateMaps(Delta* deltas, byte* mapX, byte* mapY) {
            int maxSize = Math.Max(extentX, extentY) * 2 + 1;
            int safeSize = MapSize - 2; // ensure 1-pixel empty border, just to be safe
            int mapShift = 0;
            while ((safeSize << mapShift) < maxSize) mapShift++;
            
            int mapCenter = (MapSize << mapShift) >> 1;
            
            int nodeExtentX = extentX >> 1;
            int nodeExtentY = extentY >> 1;
            
            for (int octant = 0; octant < 8; octant++) {
                int nodeX = mapCenter + deltas[octant].X;
                int nodeY = mapCenter + deltas[octant].Y;
                
                int minX = (nodeX - nodeExtentX) >> mapShift;
                int minY = (nodeY - nodeExtentY) >> mapShift;
                int maxX = (nodeX + nodeExtentX) >> mapShift;
                int maxY = (nodeY + nodeExtentY) >> mapShift;
                
                byte mask = (byte)(1 << octant);
                
                for (int x = minX; x <= maxX; x++) {
                    mapX[x] |= mask;
                }
                for (int y = minY; y <= maxY; y++) {
                    mapY[y] |= mask;
                }
            }
            
            return mapShift;
        }
        
        private unsafe struct OctreeRendererUnsafe {
            public int BufferShift;
            public PixelData* Pixels;
            
            public OctreeNode* Octree;
            
            public int ExtentX, ExtentY, ExtentZ;
            public Delta* Deltas;
            public StackItem* NodeStack;
            
            public OctantOrder.Queue* ForwardQueues;
            public OctantOrder.Queue* ReverseQueues;
            
            public int* TraceBuffer;
            
            public byte* MapX;
            public byte* MapY;
            public int MapShift;
            
            public int MapThreshold;
            
            public int MaxLevel;
            
            public int Dilation;
            
            public SplatShape Shape;
            
            public int Render() {
                int mapHalf = (MapSize << MapShift) >> 1;
                
                var stackTop = NodeStack;
                
                int* traceFront = TraceBuffer;
                
                while (stackTop >= NodeStack) {
                    // We need a copy anyway for subnode processing
                    var current = *stackTop;
                    --stackTop;
                    
                    ref var node = ref Octree[current.Address];
                    
                    if (current.MaxSize < 1) {
                        int i = current.MinX + (current.MinY << BufferShift);
                        if (current.Z < Pixels[i].Depth) {
                            if ((node.Mask == 0) | (MapThreshold > 1)) {
                                Pixels[i].Depth = current.Z | int.MinValue;
                                Pixels[i].Color24 = node.Data;
                                *(traceFront++) = i;
                            } else {
                                int mx = ((current.MinX << SubpixelBits) + SubpixelHalf) - (current.X - (mapHalf >> current.Level));
                                int my = ((current.MinY << SubpixelBits) + SubpixelHalf) - (current.Y - (mapHalf >> current.Level));
                                int mapShift = MapShift - current.Level;
                                int mask = MapX[mx >> mapShift] & MapY[my >> mapShift] & node.Mask;
                                
                                if (mask != 0) {
                                    var octant = unchecked((int)(ForwardQueues[mask].Octants & 7));
                                    
                                    int z = current.Z + (Deltas[octant].Z >> current.Level);
                                    
                                    if (z < Pixels[i].Depth) {
                                        Pixels[i].Depth = z | int.MinValue;
                                        Pixels[i].Color24 = Octree[node.Address + octant].Data;
                                        *(traceFront++) = i;
                                    }
                                }
                            }
                        }
                    } else if ((node.Mask == 0) | (current.Level >= MaxLevel)) {
                        if (Shape == SplatShape.Point) {
                            int x = current.X >> SubpixelBits;
                            int y = current.Y >> SubpixelBits;
                            if ((x >= current.MinX) & (x <= current.MaxX)) {
                                if ((y >= current.MinY) & (y <= current.MaxY)) {
                                    int z = current.Z + (ExtentZ >> (current.Level+1));
                                    int i = x + (y << BufferShift);
                                    if (z < Pixels[i].Depth) {
                                        Pixels[i].Depth = current.Z | int.MinValue;
                                        Pixels[i].Color24 = node.Data;
                                        *(traceFront++) = i;
                                    }
                                }
                            }
                        } else {
                            int j = current.MinX + (current.MinY << BufferShift);
                            int jEnd = current.MinX + (current.MaxY << BufferShift);
                            int iEnd = current.MaxX + (current.MinY << BufferShift);
                            int jStep = 1 << BufferShift;
                            for (; j <= jEnd; j += jStep, iEnd += jStep) {
                                for (int i = j; i <= iEnd; i++) {
                                    if (current.Z < Pixels[i].Depth) {
                                        Pixels[i].Depth = current.Z | int.MinValue;
                                        Pixels[i].Color24 = node.Data;
                                        *(traceFront++) = i;
                                    }
                                }
                            }
                        }
                    } else if (current.MaxSize < MapThreshold) {
                        int mapStartX = ((current.MinX << SubpixelBits) + SubpixelHalf) - (current.X - (mapHalf >> current.Level));
                        int mapStartY = ((current.MinY << SubpixelBits) + SubpixelHalf) - (current.Y - (mapHalf >> current.Level));
                        int mapShift = MapShift - current.Level;
                        
                        int j = current.MinX + (current.MinY << BufferShift);
                        int jEnd = current.MinX + (current.MaxY << BufferShift);
                        int iEnd = current.MaxX + (current.MinY << BufferShift);
                        int jStep = 1 << BufferShift;
                        
                        for (int my = mapStartY; j <= jEnd; j += jStep, iEnd += jStep, my += SubpixelSize) {
                            int maskY = MapY[my >> mapShift] & node.Mask;
                            for (int mx = mapStartX, i = j; i <= iEnd; i++, mx += SubpixelSize) {
                                int mask = MapX[mx >> mapShift] & maskY;
                                
                                if ((mask != 0) & (current.Z < Pixels[i].Depth)) {
                                    var octant = unchecked((int)(ForwardQueues[mask].Octants & 7));
                                    
                                    int z = current.Z + (Deltas[octant].Z >> current.Level);
                                    
                                    if (z < Pixels[i].Depth) {
                                        Pixels[i].Depth = z | int.MinValue;
                                        Pixels[i].Color24 = Octree[node.Address + octant].Data;
                                        *(traceFront++) = i;
                                    }
                                }
                            }
                        }
                    } else {
                        int j = current.MinX + (current.MinY << BufferShift);
                        int jEnd = current.MinX + (current.MaxY << BufferShift);
                        int iEnd = current.MaxX + (current.MinY << BufferShift);
                        int jStep = 1 << BufferShift;
                        for (; j <= jEnd; j += jStep, iEnd += jStep) {
                            for (int i = j; i <= iEnd; i++) {
                                if (current.Z < Pixels[i].Depth) goto OcclusionTestPassed;
                            }
                            current.MinY++;
                        }
                        continue;
                        OcclusionTestPassed:;
                        
                        var queue = ReverseQueues[node.Mask].Octants;
                        
                        int nextLevel = current.Level + 1;
                        int nodeExtentX = (ExtentX >> nextLevel) + Dilation;
                        int nodeExtentY = (ExtentY >> nextLevel) + Dilation;
                        
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
                
                // Clear stencil bits in the written pixels
                for (var trace = TraceBuffer; trace != traceFront; trace++) {
                    Pixels[*trace].Depth &= int.MaxValue;
                }
                
                return (int)(traceFront - TraceBuffer);
            }
        }
    }
}
