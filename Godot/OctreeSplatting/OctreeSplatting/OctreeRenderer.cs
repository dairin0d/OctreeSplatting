// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

#define USE_STENCIL

using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OctreeSplatting {
    public class OctreeRenderer {
        public enum Result {
            TooBig, TooClose, Culled, Rendered
        }
        
        [StructLayout(LayoutKind.Explicit)]
        private struct BoolByte {
            [FieldOffset(0)] public bool BoolValue;
            [FieldOffset(0)] public byte ByteValue;
        }
        
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
        
        private unsafe struct StackItemFwd {
            // Dynamic data:
            public uint Queue;
            public int X, Y, Z;
            public uint Address;
            // Precalculated data:
            public int ExtentX, ExtentY;
            public Delta* Deltas;
        }
        
        private const int SubpixelBits = 16;
        private const int SubpixelSize = 1 << SubpixelBits;
        private const int SubpixelHalf = SubpixelSize >> 1;
        private const int MaxSubdivisions = 31 - SubpixelBits;
        
        private const int MapBits = 6;
        private const int MapSize = 1 << MapBits;
        
        // If the projected octree size is above this limit,
        // there will be index-out-of-bounds errors
        private const int LevelLimit = 28 - SubpixelBits;
        
        public const int MaxSizeInPixels = 1 << LevelLimit;
        
        // Viewport & renderbuffer info
        public Range2D Viewport;
        public Renderbuffer Renderbuffer;
        
        // Model info
        public Matrix4x4 Matrix;
        public OctreeNode[] Octree;
        public uint RootAddress;
        
        public int MapThreshold = 2;
        
        public int MaxLevel = -1;
        
        public float AbsoluteDilation = 0; // in pixels
        public float RelativeDilation = 0; // relative to the root size
        
        public SplatShape Shape = SplatShape.Rectangle;
        
        public Color24 BoundsColor = new Color24 {R = 192, G = 0, B = 0};
        public bool ShowBounds;
        
        private StackItem rootInfo;
        
        private int extentX, extentY, extentZ;
        
        private int XX, XY, XZ;
        private int YX, YY, YZ;
        private int ZX, ZY, ZZ;
        private int TX, TY, TZ;
        
        private UnsafeRef octreeRef;
        private UnsafeRef queuesRef;
        private UnsafeRef cubeNodesRef;
        
        public void Begin(Renderbuffer renderbuffer, Range2D viewport) {
            Renderbuffer = renderbuffer;
            Viewport = viewport;
            
            Begin();
        }
        
        public void Begin() {
            queuesRef.Set(OctantOrder.SparseQueues);
            cubeNodesRef.Set(CubeOctree.CubeNodes);
        }
        
        public void Finish() {
            octreeRef.Clear();
            queuesRef.Clear();
            cubeNodesRef.Clear();
        }
        
        public unsafe bool IsOccluded(Range2D region, int z, out int lastY) {
            if (region.MinX < Viewport.MinX) region.MinX = Viewport.MinX;
            if (region.MinY < Viewport.MinY) region.MinY = Viewport.MinY;
            if (region.MaxX > Viewport.MaxX) region.MaxX = Viewport.MaxX;
            if (region.MaxY > Viewport.MaxY) region.MaxY = Viewport.MaxY;
            
            lastY = region.MinY;
            
            if (z < 0) return false;
            
            var tiles = Renderbuffer.ToTiles(region);
            for (var ty = tiles.MinY; ty <= tiles.MaxY; ty++) {
                for (var tx = tiles.MinX; tx <= tiles.MaxX; tx++) {
                    var buffers = Renderbuffer.GetBuffers(tx, ty);
                    var xMin = region.MinX - buffers.MinX;
                    var xMax = region.MaxX - buffers.MinX;
                    var yMin = region.MinY - buffers.MinY;
                    var yMax = region.MaxY - buffers.MinY;
                    if (xMin < 0) xMin = 0;
                    if (yMin < 0) yMin = 0;
                    if (xMax >= buffers.SizeX) xMax = buffers.SizeX - 1;
                    if (yMax >= buffers.SizeY) yMax = buffers.SizeY - 1;
                    
                    int j = xMin + (yMin << buffers.Shift);
                    int jEnd = xMin + (yMax << buffers.Shift);
                    int iEnd = xMax + (yMin << buffers.Shift);
                    int jStep = 1 << buffers.Shift;
                    for (; j <= jEnd; j += jStep, iEnd += jStep) {
                        for (int i = j; i <= iEnd; i++) {
                            if (z < buffers.Depth[i]) return false;
                        }
                        // lastY++;
                    }
                }
            }
            
            return true;
        }
        
        public unsafe Result Render() {
            int maxLevel = CalculateMaxLevel();
            if (maxLevel < 0) return Result.TooBig;
            
            CalculateIntMatrix(maxLevel);
            
            CalculateRootInfo();
            
            int dilation = (int)(Math.Max(AbsoluteDilation, 0) * SubpixelSize);
            dilation += (int)(Math.Min(Math.Max(RelativeDilation, 0), 1) * Math.Max(extentX, extentY));
            
            // Trying to support dilated cubes is quite troublesome.
            // Better make sure that they're mutually exclusive.
            if (Shape == SplatShape.Cube) dilation = 0;
            
            CalculateRootRect(dilation - SubpixelHalf);
            
            if (rootInfo.Z < 0) return Result.TooClose;
            if ((rootInfo.MaxX < rootInfo.MinX) | (rootInfo.MaxY < rootInfo.MinY)) return Result.Culled;
            
            int forwardKey = OctantOrder.Key(in Matrix);
            int reverseKey = forwardKey ^ 0b11100000000;
            
            // Delta* deltasPtr = stackalloc Delta[8];
            Delta* deltasPtr = stackalloc Delta[MaxSubdivisions * 8];
            CalculateDeltas(deltasPtr);
            
            int mapThreshold8 = (MapThreshold * 3) / 2;
            bool useMap8 = (mapThreshold8 > MapThreshold);
            
            byte* mapX = stackalloc byte[MapSize];
            byte* mapY = stackalloc byte[MapSize];
            ulong* mapX8 = stackalloc ulong[MapSize];
            ulong* mapY8 = stackalloc ulong[MapSize];
            int mapShift = CalculateMaps(deltasPtr, mapX, mapY, mapX8, mapY8, useMap8);
            
            dilation -= SubpixelHalf;
            
            octreeRef.Set(Octree);
            
            var octreePtr = (OctreeNode*)octreeRef;
            var queuesPtr = (OctantOrder.Queue*)queuesRef;
            
            const bool useStencilFwd = false;
            
            if (!useStencilFwd) {
                StackItem* nodeStackPtr = stackalloc StackItem[MaxSubdivisions * 8];
                nodeStackPtr[0] = rootInfo;
                
                var cubeNodesPtr = (uint*)cubeNodesRef;
                
                var unsafeRenderer = new OctreeRendererUnsafe {
                    Viewport = Viewport,
                    
                    Octree = octreePtr,
                    
                    ExtentX = extentX,
                    ExtentY = extentY,
                    ExtentZ = extentZ,
                    Deltas = deltasPtr,
                    NodeStack = nodeStackPtr,
                    
                    ForwardQueues = queuesPtr + forwardKey,
                    ReverseQueues = queuesPtr + reverseKey,
                    
                    MapX = mapX,
                    MapY = mapY,
                    MapShift = mapShift,
                    
                    MapX8 = mapX8,
                    MapY8 = mapY8,
                    
                    MapThreshold = MapThreshold,
                    MapThreshold8 = mapThreshold8,
                    
                    MaxLevel = Math.Min(MaxLevel >= 0 ? MaxLevel : int.MaxValue, maxLevel+1),
                    
                    Dilation = dilation,
                    
                    Shape = Shape,
                    
                    CubeNodes = cubeNodesPtr,
                    
                    BoundsColor = BoundsColor,
                    ShowBounds = ShowBounds,
                };
                
                var region = new Range2D {
                    MinX = rootInfo.MinX,
                    MinY = rootInfo.MinY,
                    MaxX = rootInfo.MaxX,
                    MaxY = rootInfo.MaxY,
                };
                var tiles = Renderbuffer.ToTiles(region);
                for (var ty = tiles.MinY; ty <= tiles.MaxY; ty++) {
                    for (var tx = tiles.MinX; tx <= tiles.MaxX; tx++) {
                        var buffers = Renderbuffer.GetBuffers(tx, ty);
                        
                        unsafeRenderer.BufferShift = buffers.Shift;
                        unsafeRenderer.DepthData = buffers.Depth;
                        unsafeRenderer.ColorData = buffers.Color;
                        unsafeRenderer.StencilData = buffers.Stencil;
                        
                        var xMin = region.MinX - buffers.MinX;
                        var xMax = region.MaxX - buffers.MinX;
                        var yMin = region.MinY - buffers.MinY;
                        var yMax = region.MaxY - buffers.MinY;
                        if (xMin < 0) xMin = 0;
                        if (yMin < 0) yMin = 0;
                        if (xMax >= buffers.SizeX) xMax = buffers.SizeX - 1;
                        if (yMax >= buffers.SizeY) yMax = buffers.SizeY - 1;
                        
                        nodeStackPtr[0] = rootInfo;
                        nodeStackPtr[0].MinX = xMin;
                        nodeStackPtr[0].MinY = yMin;
                        nodeStackPtr[0].MaxX = xMax;
                        nodeStackPtr[0].MaxY = yMax;
                        nodeStackPtr[0].X = rootInfo.X - (buffers.MinX << SubpixelBits);
                        nodeStackPtr[0].Y = rootInfo.Y - (buffers.MinY << SubpixelBits);
                        
                        // unsafeRenderer.Render();
                        unsafeRenderer.RenderSimple();
                        
                        buffers.ClearStencil();
                    }
                }
            }
            
            if (useStencilFwd) {
                var forwardQueues = queuesPtr + forwardKey;
                
                StackItemFwd* stack = stackalloc StackItemFwd[MaxSubdivisions];
                stack[0].ExtentX = extentX + dilation;
                stack[0].ExtentY = extentY + dilation;
                stack[0].Deltas = deltasPtr;
                
                var deltaPrev = deltasPtr;
                var deltaNext = deltaPrev + 8;
                for (var i = 1; i < MaxSubdivisions; i++) {
                    for (var octant = 0; octant < 8; octant++) {
                        deltaNext->X = deltaPrev->X >> 1;
                        deltaNext->Y = deltaPrev->Y >> 1;
                        deltaNext->Z = deltaPrev->Z >> 1;
                        deltaPrev++;
                        deltaNext++;
                    }
                    stack[i].ExtentX = (extentX >> i) + dilation;
                    stack[i].ExtentY = (extentY >> i) + dilation;
                    stack[i].Deltas = deltasPtr + i * 8;
                    if (stack[i].ExtentX < 0) stack[i].ExtentX = 0;
                    if (stack[i].ExtentY < 0) stack[i].ExtentY = 0;
                }
                
                var region = new Range2D {
                    MinX = rootInfo.MinX,
                    MinY = rootInfo.MinY,
                    MaxX = rootInfo.MaxX,
                    MaxY = rootInfo.MaxY,
                };
                var tiles = Renderbuffer.ToTiles(region);
                for (var ty = tiles.MinY; ty <= tiles.MaxY; ty++) {
                    for (var tx = tiles.MinX; tx <= tiles.MaxX; tx++) {
                        var buffers = Renderbuffer.GetBuffers(tx, ty);
                        stack[0].Queue = forwardQueues[octreePtr[0].Mask].Octants;
                        stack[0].X = rootInfo.X - (buffers.MinX << SubpixelBits);
                        stack[0].Y = rootInfo.Y - (buffers.MinY << SubpixelBits);
                        stack[0].Z = rootInfo.Z;
                        stack[0].Address = octreePtr[0].Address;
                        RenderStencilFwd(stack, octreePtr, forwardQueues, buffers.Stencil, buffers.Color);
                        buffers.ClearStencil();
                    }
                }
            }
            
            return Result.Rendered;
        }
        
        private unsafe void RenderStencilFwd(StackItemFwd* stack,
                                OctreeNode* octree,
                                OctantOrder.Queue* forwardQueues,
                                uint* stencilBuffer,
                                Color32* renderBuffer)
        {
            var stackStart = stack;
            
            LoopStart:;
            
            while (stack[0].Queue == 0) {
                if (stack-- == stackStart) return;
            }
            
            uint octant = stack[0].Queue & 7;
            stack[0].Queue >>= 4;
            
            var octantDeltas = stack[0].Deltas + octant;
            stack[1].X = stack[0].X + octantDeltas[0].X;
            stack[1].Y = stack[0].Y + octantDeltas[0].Y;
            stack[1].Z = stack[0].Z + octantDeltas[0].Z;
            
            int minX = (stack[1].X - stack[0].ExtentX) >> SubpixelBits;
            int maxX = (stack[1].X + stack[0].ExtentX) >> SubpixelBits;
            int sizeX = maxX - minX;
            
            if (minX < 0) minX = 0;
            if (maxX > 31) maxX = 31;
            if (maxX < minX) goto LoopStart;
            
            // Note: this works only if minX <= maxX, and they are in range [0..31]
            // When minX == maxX, the mask will contain 1 bit
            uint rowMask = (uint.MaxValue >> (31 - maxX + minX)) << minX;
            
            int minY = (stack[1].Y - stack[0].ExtentY) >> SubpixelBits;
            int maxY = (stack[1].Y + stack[0].ExtentY) >> SubpixelBits;
            int sizeY = maxY - minY;
            
            if (minY < 0) minY = 0;
            if (maxY > 31) maxY = 31;
            if (maxY < minY) goto LoopStart;
            
            // Note: if we define 0 as occluded and 1 as not occluded,
            // we can compare to 0, which can use test/jne instructions
            // Assuming we always have minY <= maxY:
            do {
                if ((stencilBuffer[minY] & rowMask) != 0) goto OcclusionTestPassed;
            } while (++minY <= maxY);
            goto LoopStart;
            OcclusionTestPassed:;
            
            // if ((sizeX <= 0) & (sizeY <= 0)) {
            if ((sizeX|sizeY) == 0) {
                stencilBuffer[minY] &= ~rowMask;
                renderBuffer[minX | (minY << 5)].RGB = octree[stack[0].Address + octant].Data;
                goto LoopStart;
            }
            
            var address = stack[0].Address + octant;
            stack[1].Address = octree[address].Address;
            stack[1].Queue = forwardQueues[octree[address].Mask].Octants;
            ++stack;
            goto LoopStart;
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
        
        private unsafe int CalculateMaps(Delta* deltas, byte* mapX, byte* mapY, ulong* mapX8, ulong* mapY8, bool useMap8) {
            int maxSize = Math.Max(extentX, extentY) * 2 + 1;
            int safeSize = MapSize - 2; // ensure 1-pixel empty border, just to be safe
            int mapShift = 0;
            while ((safeSize << mapShift) < maxSize) mapShift++;
            
            int mapCenter = (MapSize << mapShift) >> 1;
            
            int nodeExtentX = extentX >> 1;
            int nodeExtentY = extentY >> 1;
            
            int subNodeExtentX = nodeExtentX >> 1;
            int subNodeExtentY = nodeExtentY >> 1;
            
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
                
                if (useMap8) {
                    for (int subOctant = 0; subOctant < 8; subOctant++) {
                        int subNodeX = nodeX + (deltas[subOctant].X >> 1);
                        int subNodeY = nodeY + (deltas[subOctant].Y >> 1);
                        
                        int subMinX = (subNodeX - subNodeExtentX) >> mapShift;
                        int subMinY = (subNodeY - subNodeExtentY) >> mapShift;
                        int subMaxX = (subNodeX + subNodeExtentX) >> mapShift;
                        int subMaxY = (subNodeY + subNodeExtentY) >> mapShift;
                        
                        ulong subMask = ((ulong)1 << subOctant) << (octant * 8);
                        
                        for (int x = subMinX; x <= subMaxX; x++) {
                            mapX8[x] |= subMask;
                        }
                        for (int y = subMinY; y <= subMaxY; y++) {
                            mapY8[y] |= subMask;
                        }
                    }
                }
            }
            
            return mapShift;
        }
        
        private unsafe struct OctreeRendererUnsafe {
            private uint fullQueue;
            private ulong mask8Bit0;
            private ulong mask8Bit1;
            private ulong mask8Bit2;
            
            public Range2D Viewport;
            public int BufferShift;
            public int* DepthData;
            public Color32* ColorData;
            public uint* StencilData;
            
            public OctreeNode* Octree;
            
            public int ExtentX, ExtentY, ExtentZ;
            public Delta* Deltas;
            public StackItem* NodeStack;
            
            public OctantOrder.Queue* ForwardQueues;
            public OctantOrder.Queue* ReverseQueues;
            
            public byte* MapX;
            public byte* MapY;
            public int MapShift;
            
            public ulong* MapX8;
            public ulong* MapY8;
            
            public int MapThreshold;
            public int MapThreshold8;
            
            public int MaxLevel;
            
            public int Dilation;
            
            public SplatShape Shape;
            
            public uint* CubeNodes;
            
            public Color24 BoundsColor;
            public bool ShowBounds;
            
            public void RenderSimple() {
                int mapHalf = (MapSize << MapShift) >> 1;
                
                var stackTop = NodeStack;
                
                while (stackTop >= NodeStack) {
                    // We need a copy anyway for subnode processing
                    var current = *stackTop;
                    --stackTop;
                    
                    ref var node = ref Octree[current.Address];
                    
                    if (current.MaxSize < 1) {
                        #if USE_STENCIL
                        uint pixelMask = unchecked((uint)(1 << current.MinX));
                        if ((StencilData[current.MinY] & pixelMask) != 0) {
                            StencilData[current.MinY] &= ~pixelMask;
                            int i = current.MinX + (current.MinY << BufferShift);
                            if (current.Z < DepthData[i]) {
                                DepthData[i] = current.Z;
                                ColorData[i].RGB = node.Data;
                            }
                        }
                        #else
                        int i = current.MinX + (current.MinY << BufferShift);
                        if (current.Z < DepthData[i]) {
                            DepthData[i] = current.Z;
                            ColorData[i].RGB = node.Data;
                        }
                        #endif
                    } else if ((node.Mask == 0) | (current.Level >= MaxLevel)) {
                        current.Z += ExtentZ >> current.Level;
                        
                        #if USE_STENCIL
                        uint rowMask = (uint.MaxValue >> (31 - current.MaxX + current.MinX)) << current.MinX;
                        
                        for (int y = current.MinY; y <= current.MaxY; y++) {
                            if ((StencilData[y] & rowMask) == 0) continue;
                            
                            for (int x = current.MinX; x <= current.MaxX; x++) {
                                uint pixelMask = unchecked((uint)(1 << x));
                                if ((StencilData[y] & pixelMask) != 0) {
                                    StencilData[y] &= ~pixelMask;
                                    int i = x + (y << BufferShift);
                                    if (current.Z < DepthData[i]) {
                                        DepthData[i] = current.Z;
                                        ColorData[i].RGB = node.Data;
                                    }
                                }
                            }
                        }
                        #else
                        int j = current.MinX + (current.MinY << BufferShift);
                        int jEnd = current.MinX + (current.MaxY << BufferShift);
                        int iEnd = current.MaxX + (current.MinY << BufferShift);
                        int jStep = 1 << BufferShift;
                        for (; j <= jEnd; j += jStep, iEnd += jStep) {
                            for (int i = j; i <= iEnd; i++) {
                                if (current.Z < DepthData[i]) {
                                    DepthData[i] = current.Z;
                                    ColorData[i].RGB = node.Data;
                                }
                            }
                        }
                        #endif
                    } else if (current.MaxSize < MapThreshold) {
                        int mapStartX = ((current.MinX << SubpixelBits) + SubpixelHalf) - (current.X - (mapHalf >> current.Level));
                        int mapStartY = ((current.MinY << SubpixelBits) + SubpixelHalf) - (current.Y - (mapHalf >> current.Level));
                        int mapShift = MapShift - current.Level;
                        
                        #if USE_STENCIL
                        uint rowMask = (uint.MaxValue >> (31 - current.MaxX + current.MinX)) << current.MinX;
                        
                        for (int my = mapStartY, y = current.MinY; y <= current.MaxY; y++, my += SubpixelSize) {
                            if ((StencilData[y] & rowMask) == 0) continue;
                            
                            int maskY = MapY[my >> mapShift] & node.Mask;
                            for (int mx = mapStartX, x = current.MinX; x <= current.MaxX; x++, mx += SubpixelSize) {
                                int mask = MapX[mx >> mapShift] & maskY;
                                
                                uint pixelMask = unchecked((uint)(1 << x));
                                if ((mask != 0) & ((StencilData[y] & pixelMask) != 0)) {
                                    StencilData[y] &= ~pixelMask;
                                    var octant = unchecked((int)(ForwardQueues[mask].Octants & 7));
                                    int z = current.Z + (Deltas[octant].Z >> current.Level);
                                    int i = x + (y << BufferShift);
                                    if (z < DepthData[i]) {
                                        DepthData[i] = z;
                                        ColorData[i].RGB = Octree[node.Address + octant].Data;
                                    }
                                }
                            }
                        }
                        #else
                        int j = current.MinX + (current.MinY << BufferShift);
                        int jEnd = current.MinX + (current.MaxY << BufferShift);
                        int iEnd = current.MaxX + (current.MinY << BufferShift);
                        int jStep = 1 << BufferShift;
                        
                        for (int my = mapStartY; j <= jEnd; j += jStep, iEnd += jStep, my += SubpixelSize) {
                            int maskY = MapY[my >> mapShift] & node.Mask;
                            for (int mx = mapStartX, i = j; i <= iEnd; i++, mx += SubpixelSize) {
                                int mask = MapX[mx >> mapShift] & maskY;
                                
                                if ((mask != 0) & (current.Z < DepthData[i])) {
                                    var octant = unchecked((int)(ForwardQueues[mask].Octants & 7));
                                    
                                    int z = current.Z + (Deltas[octant].Z >> current.Level);
                                    
                                    if (z < DepthData[i]) {
                                        DepthData[i] = z;
                                        ColorData[i].RGB = Octree[node.Address + octant].Data;
                                    }
                                }
                            }
                        }
                        #endif
                    } else {
                        #if USE_STENCIL
                        // Note: this works only if minX <= maxX, and they are in range [0..31]
                        // When minX == maxX, the mask will contain 1 bit
                        uint rowMask = (uint.MaxValue >> (31 - current.MaxX + current.MinX)) << current.MinX;
                        
                        // Note: if we define 0 as occluded and 1 as not occluded,
                        // we can compare to 0, which can use test/jne instructions
                        // Assuming we always have minY <= maxY:
                        do {
                            if ((StencilData[current.MinY] & rowMask) != 0) goto OcclusionTestPassed;
                        } while (++current.MinY <= current.MaxY);
                        continue;
                        OcclusionTestPassed:;
                        #else
                        int j = current.MinX + (current.MinY << BufferShift);
                        int jEnd = current.MinX + (current.MaxY << BufferShift);
                        int iEnd = current.MaxX + (current.MinY << BufferShift);
                        int jStep = 1 << BufferShift;
                        for (; j <= jEnd; j += jStep, iEnd += jStep) {
                            for (int i = j; i <= iEnd; i++) {
                                if (current.Z < DepthData[i]) goto OcclusionTestPassed;
                            }
                            current.MinY++;
                        }
                        continue;
                        OcclusionTestPassed:;
                        #endif
                        
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
            }
            
            public void Render() {
                int mapHalf = (MapSize << MapShift) >> 1;
                
                var stackTop = NodeStack;
                
                fullQueue = ForwardQueues[255].Octants;
                mask8Bit0 = 0;
                mask8Bit1 = 0;
                mask8Bit2 = 0;
                for (uint item = 0, queue = fullQueue; item < 8; item++, queue >>= 4) {
                    ulong octantMask = (ulong)255 << (int)((queue & 7) * 8);
                    if ((item & 1) == 0) mask8Bit0 |= octantMask;
                    if ((item & 2) == 0) mask8Bit1 |= octantMask;
                    if ((item & 4) == 0) mask8Bit2 |= octantMask;
                }
                BoolByte octant8Bit0 = default;
                BoolByte octant8Bit1 = default;
                BoolByte octant8Bit2 = default;
                
                if (ShowBounds) {
                    stackTop[1] = stackTop[0];
                    RenderCube(stackTop + 1, BoundsColor, CubeOctree.WireCube);
                }
                
                while (stackTop >= NodeStack) {
                    // We need a copy anyway for subnode processing
                    var current = *stackTop;
                    --stackTop;
                    
                    ref var node = ref Octree[current.Address];
                    
                    if (current.MaxSize < 1) {
                        int i = current.MinX + (current.MinY << BufferShift);
                        if (current.Z < DepthData[i]) {
                            if ((node.Mask == 0) | (MapThreshold > 1)) {
                                DepthData[i] = current.Z;
                                ColorData[i].RGB = node.Data;
                            } else {
                                int mx = ((current.MinX << SubpixelBits) + SubpixelHalf) - (current.X - (mapHalf >> current.Level));
                                int my = ((current.MinY << SubpixelBits) + SubpixelHalf) - (current.Y - (mapHalf >> current.Level));
                                int mapShift = MapShift - current.Level;
                                int mask = MapX[mx >> mapShift] & MapY[my >> mapShift] & node.Mask;
                                
                                if (mask != 0) {
                                    var octant = unchecked((int)(ForwardQueues[mask].Octants & 7));
                                    
                                    int z = current.Z + (Deltas[octant].Z >> current.Level);
                                    
                                    if (z < DepthData[i]) {
                                        DepthData[i] = z;
                                        ColorData[i].RGB = Octree[node.Address + octant].Data;
                                    }
                                }
                            }
                        }
                    } else if ((node.Mask == 0) | (current.Level >= MaxLevel)) {
                        if (current.MaxSize > 1) {
                            if (Shape == SplatShape.Cube) {
                                RenderCube(stackTop + 1, node.Data);
                                continue;
                            }
                            
                            current.Z += ExtentZ >> current.Level;
                            
                            if (Shape == SplatShape.Circle) {
                                DrawCircle(ref current, node.Data);
                                continue;
                            }
                        } else {
                            current.Z += ExtentZ >> current.Level;
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
                                if (current.Z < DepthData[i]) {
                                    DepthData[i] = current.Z;
                                    ColorData[i].RGB = node.Data;
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
                                
                                if ((mask != 0) & (current.Z < DepthData[i])) {
                                    var octant = unchecked((int)(ForwardQueues[mask].Octants & 7));
                                    
                                    int z = current.Z + (Deltas[octant].Z >> current.Level);
                                    
                                    if (z < DepthData[i]) {
                                        DepthData[i] = z;
                                        ColorData[i].RGB = Octree[node.Address + octant].Data;
                                    }
                                }
                            }
                        }
                    } else {
                        if (current.MaxSize < MapThreshold8) {
                            ulong mask8 = 0;
                            for (int octant = 0, mshift = 0; octant < 8; octant++, mshift += 8) {
                                var octantMask = Octree[node.Address + octant].Mask;
                                if ((octantMask == 0) & (((node.Mask >> octant) & 1) != 0)) octantMask = 255;
                                mask8 |= ((ulong)octantMask) << mshift;
                            }
                            
                            int mapStartX = ((current.MinX << SubpixelBits) + SubpixelHalf) - (current.X - (mapHalf >> current.Level));
                            int mapStartY = ((current.MinY << SubpixelBits) + SubpixelHalf) - (current.Y - (mapHalf >> current.Level));
                            int mapShift = MapShift - current.Level;
                            
                            int j = current.MinX + (current.MinY << BufferShift);
                            int jEnd = current.MinX + (current.MaxY << BufferShift);
                            int iEnd = current.MaxX + (current.MinY << BufferShift);
                            int jStep = 1 << BufferShift;
                            
                            for (int my = mapStartY; j <= jEnd; j += jStep, iEnd += jStep, my += SubpixelSize) {
                                ulong maskY = MapY8[my >> mapShift] & mask8;
                                for (int mx = mapStartX, i = j; i <= iEnd; i++, mx += SubpixelSize) {
                                    ulong mask = MapX8[mx >> mapShift] & maskY;
                                    
                                    if ((mask != 0) & (current.Z < DepthData[i])) {
                                        octant8Bit2.BoolValue = (mask & mask8Bit2) == 0;
                                        mask &= mask8Bit2 ^ unchecked((ulong)(-octant8Bit2.ByteValue));
                                        octant8Bit1.BoolValue = (mask & mask8Bit1) == 0;
                                        mask &= mask8Bit1 ^ unchecked((ulong)(-octant8Bit1.ByteValue));
                                        octant8Bit0.BoolValue = (mask & mask8Bit0) == 0;
                                        int queueItem8 = octant8Bit0.ByteValue |
                                                        (octant8Bit1.ByteValue << 1) |
                                                        (octant8Bit2.ByteValue << 2);
                                        int octant8 = (int)((fullQueue >> (queueItem8 << 2)) & 7);
                                        
                                        int z = current.Z + (Deltas[octant8].Z >> current.Level);
                                        
                                        if (z < DepthData[i]) {
                                            DepthData[i] = z;
                                            ColorData[i].RGB = Octree[node.Address + octant8].Data;
                                        }
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
                                    if (current.Z < DepthData[i]) goto OcclusionTestPassed;
                                }
                                current.MinY++;
                            }
                            continue;
                            OcclusionTestPassed:;
                        }
                        
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
            }
            
            private void DrawCircle(ref StackItem current, Color24 color) {
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
                        if ((distance2 <= radius2) & (current.Z < DepthData[i])) {
                            DepthData[i] = current.Z;
                            ColorData[i].RGB = color;
                        }
                        distance2 += (rowDX << stepShift) + stepAdd2;
                        rowDX += stepAdd;
                    }
                    distance2Y += (startDY << stepShift) + stepAdd2;
                    startDY += stepAdd;
                }
            }
            
            private void RenderCube(StackItem* stackTop, Color24 color, int address = -1) {
                int mapHalf = (MapSize << MapShift) >> 1;
                
                // In the temporal upsampling mode, cube mode can sometimes be
                // noticeably slower than without upsampling. This is an attempt
                // to mitigate that (at the cost of small visual artifacts).
                int mapThreshold = (MapThreshold > 2 ? MapThreshold : 2);
                
                var stackBottom = stackTop;
                
                stackTop[0].Address = address >= 0 ? (uint)address : (ForwardQueues[255].Octants & 7) * CubeOctree.Step;
                
                while (stackTop >= stackBottom) {
                    // We need a copy anyway for subnode processing
                    var current = *stackTop;
                    --stackTop;
                    
                    var nodeMask = (byte)CubeNodes[current.Address];
                    
                    if (current.MaxSize < 1) {
                        int i = current.MinX + (current.MinY << BufferShift);
                        if (current.Z < DepthData[i]) {
                            DepthData[i] = current.Z;
                            ColorData[i].RGB = color;
                        }
                    } else if (current.MaxSize < mapThreshold) {
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
                                
                                if ((mask != 0) & (current.Z < DepthData[i])) {
                                    var octant = unchecked((int)(ForwardQueues[mask].Octants & 7));
                                    
                                    int z = current.Z + (Deltas[octant].Z >> current.Level);
                                    
                                    if (z < DepthData[i]) {
                                        DepthData[i] = z;
                                        ColorData[i].RGB = color;
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
                                if (current.Z < DepthData[i]) goto OcclusionTestPassed;
                            }
                            current.MinY++;
                        }
                        continue;
                        OcclusionTestPassed:;
                        
                        var queue = ReverseQueues[nodeMask].Octants;
                        
                        int nextLevel = current.Level + 1;
                        int nodeExtentX = (ExtentX >> nextLevel);
                        int nodeExtentY = (ExtentY >> nextLevel);
                        
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
            private const int X4 = 26*S, X5 = 27*S, X6 = 28*S, X7 = 29*S;
            private const int Y4 = 30*S, Y5 = 31*S, Y6 = 32*S, Y7 = 33*S;
            private const int Z4 = 34*S, Z5 = 35*S, Z6 = 36*S, Z7 = 37*S;
            private const int W0 = 38*S, W1 = 39*S, W2 = 40*S, W3 = 41*S, W4 = 42*S, W5 = 43*S, W6 = 44*S, W7 = 45*S;
            private const int WC = 46*S;
            
            public const int Step = S;
            public const int WireCube = WC;
            
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
                
                // X-angles
                0b00111111, X0, X0, ZN, ZN, YN, YN, __, __, // X0
                0b11001111, ZN, ZN, X1, X1, __, __, YP, YP, // X1
                0b11111100, __, __, YP, YP, ZP, ZP, X2, X2, // X2
                0b11110011, YN, YN, __, __, X3, X3, ZP, ZP, // X3
                // Y-angles
                0b01011111, Y0, ZN, Y0, ZN, XN, __, XN, __, // Y0
                0b11110101, XN, __, XN, __, Y1, ZP, Y1, ZP, // Y1
                0b11111010, __, XP, __, XP, ZP, Y2, ZP, Y2, // Y2
                0b10101111, ZN, Y3, ZN, Y3, __, XP, __, XP, // Y3
                // Z-angles
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
                
                // X-edges
                0b00000011, X4, X4, __, __, __, __, __, __, // X4
                0b00001100, __, __, X5, X5, __, __, __, __, // X5
                0b11000000, __, __, __, __, __, __, X6, X6, // X6
                0b00110000, __, __, __, __, X7, X7, __, __, // X7
                // Y-edges
                0b00000101, Y4, __, Y4, __, __, __, __, __, // Y4
                0b01010000, __, __, __, __, Y5, __, Y5, __, // Y5
                0b10100000, __, __, __, __, __, Y6, __, Y6, // Y6
                0b00001010, __, Y7, __, Y7, __, __, __, __, // Y7
                // Z-edges
                0b00010001, Z4, __, __, __, Z4, __, __, __, // Z4
                0b00100010, __, Z5, __, __, __, Z5, __, __, // Z5
                0b10001000, __, __, __, Z6, __, __, __, Z6, // Z6
                0b01000100, __, __, Z7, __, __, __, Z7, __, // Z7
                
                // wireframe corners
                0b00010111, W0, X4, Y4, __, Z4, __, __, __, // W0
                0b00101011, X4, W1, __, Y7, __, Z5, __, __, // W1
                0b01001101, Y4, __, W2, X5, __, __, Z7, __, // W2
                0b10001110, __, Y7, X5, W3, __, __, __, Z6, // W3
                0b01110001, Z4, __, __, __, W4, X7, Y5, __, // W4
                0b10110010, __, Z5, __, __, X7, W5, __, Y6, // W5
                0b11010100, __, __, Z7, __, Y5, __, W6, X6, // W6
                0b11101000, __, __, __, Z6, __, Y6, X6, W7, // W7
                
                // wireframe cube
                0b11111111, W0, W1, W2, W3, W4, W5, W6, W7, // WC
            };
        }
    }
}
