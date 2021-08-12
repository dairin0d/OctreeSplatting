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
        
        // If the projected octree size is above this limit,
        // there will be index-out-of-bounds errors
        private const int LevelLimit = 28 - SubpixelBits;
        
        public const int MaxSizeInPixels = 1 << LevelLimit;
        
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
        
        public float AbsoluteDilation = 0; // in pixels
        public float RelativeDilation = 0; // relative to the root size
        
        public SplatShape Shape = SplatShape.Rectangle;
        
        public int DrawnPixels;
        
        private StackItem rootInfo;
        
        private int extentX, extentY, extentZ;
        
        private int XX, XY, XZ;
        private int YX, YY, YZ;
        private int ZX, ZY, ZZ;
        private int TX, TY, TZ;
        
        private int[] traceBuffer;
        
        public unsafe int Render() {
            DrawnPixels = 0;
            
            int maxLevel = CalculateMaxLevel();
            if (maxLevel < 0) return -2;
            
            CalculateIntMatrix(maxLevel);
            
            CalculateRootInfo();
            
            int dilation = (int)(Math.Max(AbsoluteDilation, 0) * SubpixelSize);
            dilation += (int)(Math.Min(Math.Max(RelativeDilation, 0), 1) * Math.Max(extentX, extentY));
            
            // Trying to support dilated cubes is quite troublesome.
            // Better make sure that they're mutually exclusive.
            if (Shape == SplatShape.Cube) dilation = 0;
            
            CalculateRootRect(dilation - SubpixelHalf);
            
            if (rootInfo.Z < 0) return -1;
            if ((rootInfo.MaxX < rootInfo.MinX) | (rootInfo.MaxY < rootInfo.MinY)) return 0;
            
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
            fixed (uint* cubeNodesPtr = CubeOctree.CubeNodes)
            {
                var unsafeRenderer = new OctreeRendererUnsafe {
                    Viewport = Viewport,
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
                    
                    Dilation = dilation - SubpixelHalf,
                    
                    Shape = Shape,
                    
                    CubeNodes = cubeNodesPtr,
                };
                
                DrawnPixels = unsafeRenderer.Render();
            }
            
            return 1;
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
            
            for (int maxLevel = 0; maxLevel <= LevelLimit; maxLevel++) {
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
            if (Shape == SplatShape.Circle) {
                // Diagonals are symmetric, so we only need 4 corners
                float maxDiagonal2 = 0;
                int z = 1;
                for (int y = -1; y <= 1; y += 2) {
                    for (int x = -1; x <= 1; x += 2) {
                        float diagonalX = (XX * x + YX * y + ZX * z) << 1;
                        float diagonalY = (XY * x + YY * y + ZY * z) << 1;
                        float diagonal2 = diagonalX*diagonalX + diagonalY*diagonalY;
                        if (maxDiagonal2 < diagonal2) maxDiagonal2 = diagonal2;
                    }
                }
                extentX = extentY = (int)(Math.Sqrt(maxDiagonal2) + 1);
            } else {
                extentX = (Math.Abs(XX) + Math.Abs(YX) + Math.Abs(ZX)) << 1;
                extentY = (Math.Abs(XY) + Math.Abs(YY) + Math.Abs(ZY)) << 1;
                
                if (Shape == SplatShape.Square) {
                    extentX = extentY = Math.Max(extentX, extentY);
                }
            }
            
            extentZ = (Math.Abs(XZ) + Math.Abs(YZ) + Math.Abs(ZZ)) << 1;
            
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
            public Range2D Viewport;
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
            
            public uint* CubeNodes;
            
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
                        if (Shape == SplatShape.Cube) {
                            RenderCube(stackTop + 1, ref traceFront, node.Data);
                            continue;
                        }
                        
                        current.Z += ExtentZ >> current.Level;
                        
                        if (Shape == SplatShape.Circle) {
                            DrawCircle(ref current, ref traceFront, node.Data);
                            continue;
                        }
                        
                        if (Shape == SplatShape.Point) {
                            int dilation = (Dilation > 0 ? Dilation : 0);
                            current.MinX = (current.X - dilation) >> SubpixelBits;
                            current.MinY = (current.Y - dilation) >> SubpixelBits;
                            current.MaxX = (current.X + dilation) >> SubpixelBits;
                            current.MaxY = (current.Y + dilation) >> SubpixelBits;
                            
                            if (current.MinX < Viewport.MinX) current.MinX = Viewport.MinX;
                            if (current.MinY < Viewport.MinY) current.MinY = Viewport.MinY;
                            if (current.MaxX > Viewport.MaxX) current.MaxX = Viewport.MaxX;
                            if (current.MaxY > Viewport.MaxY) current.MaxY = Viewport.MaxY;
                        }
                        
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
            
            private void DrawCircle(ref StackItem current, ref int* traceFront, Color24 color) {
                // For circle, ExtentX and ExtentY are always equal
                int radius = (ExtentX >> current.Level) + Dilation + SubpixelHalf;
                
                const int MagnitudeLimit = 23170; // (2 * 23170)^2 < 2^31
                int circleShift = SubpixelBits;
                for (; (radius > MagnitudeLimit) | ((1 << circleShift) > MagnitudeLimit); circleShift--, radius >>= 1);
                
                int radius2 = radius * radius;
                
                int startDX = (((current.MinX << SubpixelBits) + SubpixelHalf) - current.X) >> (SubpixelBits - circleShift);
                int startDY = (((current.MinY << SubpixelBits) + SubpixelHalf) - current.Y) >> (SubpixelBits - circleShift);
                int stepAdd = 1 << circleShift, stepShift = circleShift + 1, stepAdd2 = stepAdd * stepAdd;
                int distance2Y = startDX * startDX + startDY * startDY;
                
                int j = current.MinX + (current.MinY << BufferShift);
                int jEnd = current.MinX + (current.MaxY << BufferShift);
                int iEnd = current.MaxX + (current.MinY << BufferShift);
                int jStep = 1 << BufferShift;
                for (; j <= jEnd; j += jStep, iEnd += jStep) {
                    int distance2 = distance2Y, rowDX = startDX;
                    for (int i = j; i <= iEnd; i++) {
                        if ((distance2 <= radius2) & (current.Z < Pixels[i].Depth)) {
                            Pixels[i].Depth = current.Z | int.MinValue;
                            Pixels[i].Color24 = color;
                            *(traceFront++) = i;
                        }
                        distance2 += (rowDX << stepShift) + stepAdd2;
                        rowDX += stepAdd;
                    }
                    distance2Y += (startDY << stepShift) + stepAdd2;
                    startDY += stepAdd;
                }
            }
            
            private void RenderCube(StackItem* stackTop, ref int* traceFront, Color24 color) {
                int mapHalf = (MapSize << MapShift) >> 1;
                
                var stackBottom = stackTop;
                
                stackTop[0].Address = (ForwardQueues[255].Octants & 7) * CubeOctree.Step;
                
                while (stackTop >= stackBottom) {
                    // We need a copy anyway for subnode processing
                    var current = *stackTop;
                    --stackTop;
                    
                    var nodeMask = (byte)CubeNodes[current.Address];
                    
                    if (current.MaxSize < 1) {
                        int i = current.MinX + (current.MinY << BufferShift);
                        if (current.Z < Pixels[i].Depth) {
                            Pixels[i].Depth = current.Z | int.MinValue;
                            Pixels[i].Color24 = color;
                            *(traceFront++) = i;
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
                            int maskY = MapY[my >> mapShift] & nodeMask;
                            for (int mx = mapStartX, i = j; i <= iEnd; i++, mx += SubpixelSize) {
                                int mask = MapX[mx >> mapShift] & maskY;
                                
                                if ((mask != 0) & (current.Z < Pixels[i].Depth)) {
                                    var octant = unchecked((int)(ForwardQueues[mask].Octants & 7));
                                    
                                    int z = current.Z + (Deltas[octant].Z >> current.Level);
                                    
                                    if (z < Pixels[i].Depth) {
                                        Pixels[i].Depth = z | int.MinValue;
                                        Pixels[i].Color24 = color;
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
                        
                        var queue = ReverseQueues[nodeMask].Octants;
                        
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
                            stackTop->Address = CubeNodes[current.Address + 1 + octant];
                            stackTop->Level = nextLevel;
                        }
                    }
                }
            }
        }
        
        private static class CubeOctree {
            private const int S = 9, __ = 0;
            private const int C0 = 0*S, C1 = 1*S, C2 = 2*S, C3 = 3*S, C4 = 4*S, C5 = 5*S, C6 = 6*S, C7 = 7*S;
            private const int X0 = 8*S, X1 = 9*S, X2 = 10*S, X3 = 11*S;
            private const int Y0 = 12*S, Y1 = 13*S, Y2 = 14*S, Y3 = 15*S;
            private const int Z0 = 16*S, Z1 = 17*S, Z2 = 18*S, Z3 = 19*S;
            private const int XN = 20*S, XP = 21*S, YN = 22*S, YP = 23*S, ZN = 24*S, ZP = 25*S;
            
            public const int Step = S;
            
            public static uint[] CubeNodes = new uint[] {
                // corners
                0b01111111, C0, X0, Y0, ZN, Z0, YN, XN, __, // C0
                0b10111111, X0, C1, ZN, Y3, YN, Z1, __, XP, // C1
                0b11011111, Y0, ZN, C2, X1, XN, __, Z3, YP, // C2
                0b11101111, ZN, Y3, X1, C3, __, XP, YP, Z2, // C3
                0b11110111, Z0, YN, XN, __, C4, X3, Y1, ZP, // C4
                0b11111011, YN, Z1, __, XP, X3, C5, ZP, Y2, // C5
                0b11111101, XN, __, Z3, YP, Y1, ZP, C6, X2, // C6
                0b11111110, __, XP, YP, Z2, ZP, Y2, X2, C7, // C7
                
                // X-edges
                0b00111111, X0, X0, ZN, ZN, YN, YN, __, __, // X0
                0b11001111, ZN, ZN, X1, X1, __, __, YP, YP, // X1
                0b11111100, __, __, YP, YP, ZP, ZP, X2, X2, // X2
                0b11110011, YN, YN, __, __, X3, X3, ZP, ZP, // X3
                // Y-edges
                0b01011111, Y0, ZN, Y0, ZN, XN, __, XN, __, // Y0
                0b11110101, XN, __, XN, __, Y1, ZP, Y1, ZP, // Y1
                0b11111010, __, XP, __, XP, ZP, Y2, ZP, Y2, // Y2
                0b10101111, ZN, Y3, ZN, Y3, __, XP, __, XP, // Y3
                // Z-edges
                0b01110111, Z0, YN, XN, __, Z0, YN, XN, __, // Z0
                0b10111011, YN, Z1, __, XP, YN, Z1, __, XP, // Z1
                0b11101110, __, XP, YP, Z2, __, XP, YP, Z2, // Z2
                0b11011101, XN, __, Z3, YP, XN, __, Z3, YP, // Z3
                
                // faces
                0b01010101, XN, __, XN, __, XN, __, XN, __, // XN
                0b10101010, __, XP, __, XP, __, XP, __, XP, // XP
                0b00110011, YN, YN, __, __, YN, YN, __, __, // YN
                0b11001100, __, __, YP, YP, __, __, YP, YP, // YP
                0b00001111, ZN, ZN, ZN, ZN, __, __, __, __, // ZN
                0b11110000, __, __, __, __, ZP, ZP, ZP, ZP, // ZP
            };
        }
    }
}
