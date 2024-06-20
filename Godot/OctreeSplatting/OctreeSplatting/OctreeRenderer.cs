// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

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
        
        public int DrawnPixels;
        
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
        
        public void Begin(Renderbuffer renderbuffer, Range2D viewport) {
            Renderbuffer = renderbuffer;
            Viewport = viewport;
            
            Begin();
        }
        
        public void Begin() {
            queuesRef.Set(OctantOrder.SparseQueues);
        }
        
        public void Finish() {
            octreeRef.Clear();
            queuesRef.Clear();
            Lookups.FreePointers();
        }
        
        public unsafe bool IsOccluded(Range2D region, int z, out int lastY) {
            if (region.MinX < Viewport.MinX) region.MinX = Viewport.MinX;
            if (region.MinY < Viewport.MinY) region.MinY = Viewport.MinY;
            if (region.MaxX > Viewport.MaxX) region.MaxX = Viewport.MaxX;
            if (region.MaxY > Viewport.MaxY) region.MaxY = Viewport.MaxY;
            
            lastY = region.MinY;
            
            if (z < 0) return false;
            
            var buffers = Renderbuffer.GetBuffers();
            
            var lookupPtrs = Lookups.GetPointers();
            var stencilX = lookupPtrs.StencilX;
            var stencilY = lookupPtrs.StencilY;
            
            var txMin = region.MinX & ~Renderbuffer.TileMaskX;
            var txMax = region.MaxX & ~Renderbuffer.TileMaskX;
            var tyMin = region.MinY & ~Renderbuffer.TileMaskY;
            var tyMax = region.MaxY & ~Renderbuffer.TileMaskY;
            {
                var tileRow = tyMin >> Renderbuffer.TileShiftY;
                var tileCol = txMin >> Renderbuffer.TileShiftY;
                for (var ty = tyMin; ty <= tyMax; ty += Renderbuffer.TileSizeY, tileRow++) {
                    var stencilTile = buffers.Stencil + (tileCol + (tileRow << buffers.TileShift));
                    for (var tx = txMin; tx <= txMax; tx += Renderbuffer.TileSizeX, stencilTile++) {
                        var pixelMask = stencilTile[0] &
                            (stencilX[region.MinX - tx] ^ stencilX[region.MaxX - tx + 1]) &
                            (stencilY[region.MinY - ty] ^ stencilY[region.MaxY - ty + 1]);
                        if (pixelMask != 0) return false;
                    }
                    lastY = ty + Renderbuffer.TileSizeY;
                }
            }
            
            return true;
        }
        
        public unsafe Result Render() {
            DrawnPixels = 0;
            
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
            
            var queues = OctantOrder.SparseQueues;
            int forwardKey = OctantOrder.Key(in Matrix);
            int reverseKey = forwardKey ^ 0b11100000000;
            
            StackItem* nodeStackPtr = stackalloc StackItem[MaxSubdivisions * 8];
            
            nodeStackPtr[0] = rootInfo;
            
            Delta* deltasPtr = stackalloc Delta[8];
            CalculateDeltas(deltasPtr);
            
            int mapThreshold8 = MapThreshold * 2;
            bool useMap8 = (mapThreshold8 > MapThreshold);
            
            byte* mapX = stackalloc byte[MapSize];
            byte* mapY = stackalloc byte[MapSize];
            ulong* mapX8 = stackalloc ulong[MapSize];
            ulong* mapY8 = stackalloc ulong[MapSize];
            int mapShift = CalculateMaps(deltasPtr, mapX, mapY, mapX8, mapY8, useMap8);
            
            octreeRef.Set(Octree);
            
            var instanceInfo = new InstanceInfo {
                Matrix = Matrix,
                Octree = Octree,
                RootAddress = RootAddress,
            };
            Renderbuffer.AddInstanceInfo(instanceInfo);
            
            var buffers = Renderbuffer.GetBuffers();
            
            var octreePtr = (OctreeNode*)octreeRef;
            var queuesPtr = (OctantOrder.Queue*)queuesRef;
            {
                var unsafeRenderer = new OctreeRendererUnsafe {
                    Viewport = Viewport,
                    Buffers = buffers,
                    InstanceIndex = Renderbuffer.InstanceCount - 1,
                    
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
                    
                    LookupPtrs = Lookups.GetPointers(),
                    
                    MaxLevel = Math.Min(MaxLevel >= 0 ? MaxLevel : int.MaxValue, maxLevel+1),
                    
                    Dilation = dilation - SubpixelHalf,
                    
                    Shape = Shape,
                    
                    BoundsColor = BoundsColor,
                    ShowBounds = ShowBounds,
                };
                
                DrawnPixels = unsafeRenderer.Render();
            }
            
            return Result.Rendered;
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
            public Renderbuffer.Pointers Buffers;
            public uint InstanceIndex;
            
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
            
            public Lookups.Pointers LookupPtrs;
            
            public int MaxLevel;
            
            public int Dilation;
            
            public SplatShape Shape;
            
            public Color24 BoundsColor;
            public bool ShowBounds;
            
            public int Render() {
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
                
                var stencilX = LookupPtrs.StencilX;
                var stencilY = LookupPtrs.StencilY;
                
                var bufferRowMask = (1 << Buffers.Shift) - 1;
                
                var tileRowSize = 1 << Buffers.TileShift;
                
                ulong* mapEvalX = stackalloc ulong[4];
                ulong* mapEvalY = stackalloc ulong[4];
                
                // "Cube test" (to check the effects of dataset & cache misses)
                var node = Octree[stackTop[0].Address];
                node.Mask = 255;
                
                while (stackTop >= NodeStack) {
                    // We need a copy anyway for subnode processing
                    var current = *stackTop;
                    --stackTop;
                    
                    // ref var node = ref Octree[current.Address];
                    
                    if ((node.Mask == 0) | (current.Level >= MaxLevel)) {
                        if (current.MaxSize > 1) {
                            current.Z += ExtentZ >> current.Level;
                        } else {
                            current.Z += ExtentZ >> current.Level;
                        }
                        
                        int j = current.MinX + (current.MinY << Buffers.Shift);
                        int jEnd = current.MinX + (current.MaxY << Buffers.Shift);
                        int iEnd = current.MaxX + (current.MinY << Buffers.Shift);
                        int jStep = 1 << Buffers.Shift;
                        for (; j <= jEnd; j += jStep, iEnd += jStep) {
                            for (int i = j; i <= iEnd; i++) {
                                if (current.Z < Buffers.Depth[i]) {
                                    var ix = i & bufferRowMask;
                                    var iy = i >> Buffers.Shift;
                                    var tx = ix & ~Renderbuffer.TileMaskX;
                                    var ty = iy & ~Renderbuffer.TileMaskY;
                                    var txi = ix >> Renderbuffer.TileShiftX;
                                    var tyi = iy >> Renderbuffer.TileShiftY;
                                    var ti = txi + (tyi << Buffers.TileShift);
                                    var pixelMask =
                                        (stencilX[ix - tx] ^ stencilX[ix - tx + 1]) &
                                        (stencilY[iy - ty] ^ stencilY[iy - ty + 1]);
                                    Buffers.Stencil[ti] &= ~pixelMask;
                                    
                                    Buffers.Depth[i] = current.Z;
                                    Buffers.Instance[i] = InstanceIndex;
                                    Buffers.Address[i] = current.Address;
                                }
                            }
                        }
                    } else {
                        if (current.MaxSize < 4) {
                            var localMask = 0UL;
                            {
                                var txMin = current.MinX >> Renderbuffer.TileShiftX;
                                var txMax = current.MaxX >> Renderbuffer.TileShiftX;
                                var tyMin = current.MinY >> Renderbuffer.TileShiftY;
                                var tyMax = current.MaxY >> Renderbuffer.TileShiftY;
                                var stencilTile = Buffers.Stencil + (txMin + (tyMin << Buffers.TileShift));
                                var tx = current.MinX & ~Renderbuffer.TileMaskX;
                                var ty = current.MinY & ~Renderbuffer.TileMaskY;
                                var pixelMask = 0UL;
                                switch ((txMax-txMin) | ((tyMax-tyMin) << 1)) {
                                    case 0:
                                        pixelMask = stencilTile[0] &
                                            (stencilX[current.MinX - tx] ^ stencilX[current.MaxX - tx + 1]) &
                                            (stencilY[current.MinY - ty] ^ stencilY[current.MaxY - ty + 1]);
                                        pixelMask >>= (current.MinX - tx);
                                        pixelMask >>= (current.MinY - ty) * 4;
                                        localMask = pixelMask;
                                        break;
                                    case 1:
                                        pixelMask = stencilTile[0] &
                                            (stencilX[current.MinX - tx] ^ stencilX[current.MaxX - tx + 1]) &
                                            (stencilY[current.MinY - ty] ^ stencilY[current.MaxY - ty + 1]);
                                        pixelMask >>= (current.MinX - tx);
                                        pixelMask >>= (current.MinY - ty) * 4;
                                        localMask = pixelMask;
                                        
                                        tx += Renderbuffer.TileSizeX;
                                        pixelMask = stencilTile[1] &
                                            (stencilX[current.MinX - tx] ^ stencilX[current.MaxX - tx + 1]) &
                                            (stencilY[current.MinY - ty] ^ stencilY[current.MaxY - ty + 1]);
                                        pixelMask <<= (tx - current.MinX);
                                        pixelMask >>= (current.MinY - ty) * 4;
                                        localMask |= pixelMask;
                                        break;
                                    case 2:
                                        pixelMask = stencilTile[0] &
                                            (stencilX[current.MinX - tx] ^ stencilX[current.MaxX - tx + 1]) &
                                            (stencilY[current.MinY - ty] ^ stencilY[current.MaxY - ty + 1]);
                                        pixelMask >>= (current.MinX - tx);
                                        pixelMask >>= (current.MinY - ty) * 4;
                                        localMask = pixelMask;
                                        
                                        ty += Renderbuffer.TileSizeY;
                                        pixelMask = stencilTile[tileRowSize] &
                                            (stencilX[current.MinX - tx] ^ stencilX[current.MaxX - tx + 1]) &
                                            (stencilY[current.MinY - ty] ^ stencilY[current.MaxY - ty + 1]);
                                        pixelMask >>= (current.MinX - tx);
                                        pixelMask <<= (ty - current.MinY) * 4;
                                        localMask |= pixelMask;
                                        break;
                                    case 3:
                                        pixelMask = stencilTile[0] &
                                            (stencilX[current.MinX - tx] ^ stencilX[current.MaxX - tx + 1]) &
                                            (stencilY[current.MinY - ty] ^ stencilY[current.MaxY - ty + 1]);
                                        pixelMask >>= (current.MinX - tx);
                                        pixelMask >>= (current.MinY - ty) * 4;
                                        localMask = pixelMask;
                                        
                                        tx += Renderbuffer.TileSizeX;
                                        pixelMask = stencilTile[1] &
                                            (stencilX[current.MinX - tx] ^ stencilX[current.MaxX - tx + 1]) &
                                            (stencilY[current.MinY - ty] ^ stencilY[current.MaxY - ty + 1]);
                                        pixelMask <<= (tx - current.MinX);
                                        pixelMask >>= (current.MinY - ty) * 4;
                                        localMask |= pixelMask;
                                        
                                        ty += Renderbuffer.TileSizeY;
                                        pixelMask = stencilTile[tileRowSize+1] &
                                            (stencilX[current.MinX - tx] ^ stencilX[current.MaxX - tx + 1]) &
                                            (stencilY[current.MinY - ty] ^ stencilY[current.MaxY - ty + 1]);
                                        pixelMask <<= (tx - current.MinX);
                                        pixelMask <<= (ty - current.MinY) * 4;
                                        localMask |= pixelMask;
                                        
                                        tx -= Renderbuffer.TileSizeX;
                                        pixelMask = stencilTile[tileRowSize] &
                                            (stencilX[current.MinX - tx] ^ stencilX[current.MaxX - tx + 1]) &
                                            (stencilY[current.MinY - ty] ^ stencilY[current.MaxY - ty + 1]);
                                        pixelMask >>= (current.MinX - tx);
                                        pixelMask <<= (ty - current.MinY) * 4;
                                        localMask |= pixelMask;
                                        break;
                                }
                                
                                if (localMask == 0) continue;
                            }
                            
                            ulong mask8 = 0;
                            for (int octant = 0, mshift = 0; octant < 8; octant++, mshift += 8) {
                                var octantMask = Octree[node.Address + octant].Mask;
                                if ((octantMask == 0) & (((node.Mask >> octant) & 1) != 0)) octantMask = 255;
                                mask8 |= ((ulong)octantMask) << mshift;
                            }
                            
                            int mapStartX = ((current.MinX << SubpixelBits) + SubpixelHalf) - (current.X - (mapHalf >> current.Level));
                            int mapStartY = ((current.MinY << SubpixelBits) + SubpixelHalf) - (current.Y - (mapHalf >> current.Level));
                            int mapShift = MapShift - current.Level;
                            
                            var blitSeq = LookupPtrs.BlitSequences[localMask];
                            
                            for (; blitSeq.XSamples != 0; blitSeq.XSamples >>= 3) {
                                var i = blitSeq.XSamples & 3;
                                mapEvalX[i] = MapX8[(mapStartX + i*SubpixelSize) >> mapShift];
                            }
                            for (; blitSeq.YSamples != 0; blitSeq.YSamples >>= 3) {
                                var i = blitSeq.YSamples & 3;
                                mapEvalY[i] = MapY8[(mapStartY + i*SubpixelSize) >> mapShift] & mask8;
                            }
                            
                            for (; blitSeq.Count > 0; blitSeq.Count--, blitSeq.XCoords >>= 2, blitSeq.YCoords >>= 2) {
                                var x = (blitSeq.XCoords & 3);
                                var y = (blitSeq.YCoords & 3);
                                ulong mask = mapEvalX[x] & mapEvalY[y];
                                if (mask == 0) continue;
                                var ix = current.MinX+x;
                                var iy = current.MinY+y;
                                var i = ix + (iy << Buffers.Shift);
                                
                                octant8Bit2.BoolValue = (mask & mask8Bit2) == 0;
                                mask &= mask8Bit2 ^ unchecked((ulong)(-octant8Bit2.ByteValue));
                                octant8Bit1.BoolValue = (mask & mask8Bit1) == 0;
                                mask &= mask8Bit1 ^ unchecked((ulong)(-octant8Bit1.ByteValue));
                                octant8Bit0.BoolValue = (mask & mask8Bit0) == 0;
                                int queueItem8 = octant8Bit0.ByteValue |
                                                (octant8Bit1.ByteValue << 1) |
                                                (octant8Bit2.ByteValue << 2);
                                var octant8 = (fullQueue >> (queueItem8 << 2)) & 7;
                                
                                int z = current.Z + (Deltas[octant8].Z >> current.Level);
                                
                                if (z < Buffers.Depth[i]) {
                                    var txi = ix >> Renderbuffer.TileShiftX;
                                    var tyi = iy >> Renderbuffer.TileShiftY;
                                    var ti = txi + (tyi << Buffers.TileShift);
                                    var pixelMask = 1UL << (int)(((ix & Renderbuffer.TileMaskX) |
                                                    ((iy & Renderbuffer.TileMaskY) << Renderbuffer.TileShiftX)));
                                    Buffers.Stencil[ti] &= ~pixelMask;
                                    
                                    Buffers.Depth[i] = z;
                                    Buffers.Instance[i] = InstanceIndex;
                                    Buffers.Address[i] = node.Address + octant8;
                                    
                                    // var octantMask = Octree[node.Address + octant8].Mask;
                                    // if (octantMask == 0) {
                                    //     Buffers.Address[i] = node.Address + octant8;
                                    // } else {
                                    //     var address = Octree[node.Address + octant8].Address;
                                    //     var octant = ForwardQueues[octantMask].Octants & 7;
                                    //     Buffers.Address[i] = address + octant;
                                    // }
                                }
                            }
                            
                            continue;
                        }
                        
                        {
                            var txMin = current.MinX & ~Renderbuffer.TileMaskX;
                            var txMax = current.MaxX & ~Renderbuffer.TileMaskX;
                            var tyMin = current.MinY & ~Renderbuffer.TileMaskY;
                            var tyMax = current.MaxY & ~Renderbuffer.TileMaskY;
                            var tileX = txMin >> Renderbuffer.TileShiftX;
                            var tileY = tyMin >> Renderbuffer.TileShiftY;
                            for (var ty = tyMin; ty <= tyMax; ty += Renderbuffer.TileSizeY, tileY++) {
                                var stencilTile = Buffers.Stencil + (tileX + (tileY << Buffers.TileShift));
                                for (var tx = txMin; tx <= txMax; tx += Renderbuffer.TileSizeX, stencilTile++) {
                                    var pixelMask = stencilTile[0] &
                                        (stencilX[current.MinX - tx] ^ stencilX[current.MaxX - tx + 1]) &
                                        (stencilY[current.MinY - ty] ^ stencilY[current.MaxY - ty + 1]);
                                    if (pixelMask != 0) goto OcclusionTestPassed;
                                }
                                current.MinY = ty + Renderbuffer.TileSizeY;
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
                
                return 0; // we currently don't count the drawn pixels
            }
        }
    }
}
