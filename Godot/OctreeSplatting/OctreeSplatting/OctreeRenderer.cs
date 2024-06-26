// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

// #define USE_PROFILING

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
        
        private struct Fragment {
            public int X, Y, Z;
            public uint Address;
        }
        
        // For some reason, using bool (or BoolByte) inside a struct
        // that defines array elements would lead to a crash when
        // running a Godot build (or at least not rendering anything).
        private struct TileTraceInfo {
            public int Next;
            public byte UpdateStencil;
            public byte UpdateDepth;
            public byte Pad0, Pad1;
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
        public Octree Octree;
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
        
        private Fragment[] traceBuffer;
        private TileTraceInfo[] traceTiles;
        private int firstTile = -1;
        private int lastTile = -1;
        
        private UnsafeRef queuesRef;
        private UnsafeRef traceBufferRef;
        private UnsafeRef traceTilesRef;
        
        #if USE_PROFILING
        private static SpinTimer spinTimer = new SpinTimer();
        #endif
        
        public void Begin(Renderbuffer renderbuffer, Range2D viewport) {
            Renderbuffer = renderbuffer;
            Viewport = viewport;
            
            Begin();
        }
        
        public void Begin() {
            InitializeTraceBuffer();
            
            queuesRef.Set(OctantOrder.SparseQueues);
            traceBufferRef.Set(traceBuffer);
            traceTilesRef.Set(traceTiles);
            
            #if USE_PROFILING
            spinTimer.Reset();
            spinTimer.StartCounter();
            #endif
            Timing.Start();
        }
        
        public void Finish() {
            #if USE_PROFILING
            spinTimer.StopCounter();
            #endif
            Timing.Stop();
            
            queuesRef.Clear();
            traceBufferRef.Clear();
            traceTilesRef.Clear();
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
            
            int j = region.MinX + (region.MinY << buffers.Shift);
            int jEnd = region.MinX + (region.MaxY << buffers.Shift);
            int iEnd = region.MaxX + (region.MinY << buffers.Shift);
            int jStep = 1 << buffers.Shift;
            for (; j <= jEnd; j += jStep, iEnd += jStep) {
                for (int i = j; i <= iEnd; i++) {
                    if (z < buffers.Depth[i]) return false;
                }
                lastY++;
            }
            
            return true;
            
            // For models that occupy a small amount of screen-space,
            // this might be less effective than per-pixel depth test
            
            // var lookupPtrs = Lookups.GetPointers();
            // var stencilX = lookupPtrs.StencilX;
            // var stencilY = lookupPtrs.StencilY;
            
            // var txMin = region.MinX & ~Renderbuffer.TileMaskX;
            // var txMax = region.MaxX & ~Renderbuffer.TileMaskX;
            // var tyMin = region.MinY & ~Renderbuffer.TileMaskY;
            // var tyMax = region.MaxY & ~Renderbuffer.TileMaskY;
            // {
            //     var tileRow = tyMin >> Renderbuffer.TileShiftY;
            //     var tileCol = txMin >> Renderbuffer.TileShiftY;
            //     for (var ty = tyMin; ty <= tyMax; ty += Renderbuffer.TileSizeY, tileRow++) {
            //         var tile = buffers.Stencil + (tileCol + (tileRow << buffers.TileShift));
            //         for (var tx = txMin; tx <= txMax; tx += Renderbuffer.TileSizeX, tile++) {
            //             if (z < tile->Depth) return false;
            //             var pixelMask = tile->Scene &
            //                 (stencilX[region.MinX - tx] ^ stencilX[region.MaxX - tx + 1]) &
            //                 (stencilY[region.MinY - ty] ^ stencilY[region.MaxY - ty + 1]);
            //             if (pixelMask != 0) return false;
            //         }
            //         lastY = ty + Renderbuffer.TileSizeY;
            //     }
            // }
            
            // return true;
        }
        
        public unsafe Result Render(bool cleanup = true) {
            DrawnPixels = 0;
            
            var maxLevel = CalculateMaxLevel();
            if (maxLevel < 0) return Result.TooBig;
            
            CalculateIntMatrix(maxLevel);
            
            CalculateRootInfo();
            
            var dilation = (int)(Math.Max(AbsoluteDilation, 0) * SubpixelSize);
            dilation += (int)(Math.Min(Math.Max(RelativeDilation, 0), 1) * Math.Max(extentX, extentY));
            
            // Trying to support dilated cubes is quite troublesome.
            // Better make sure that they're mutually exclusive.
            if (Shape == SplatShape.Cube) dilation = 0;
            
            CalculateRootRect(dilation - SubpixelHalf);
            
            if (rootInfo.Z < 0) return Result.TooClose;
            if ((rootInfo.MaxX < rootInfo.MinX) | (rootInfo.MaxY < rootInfo.MinY)) return Result.Culled;
            
            var queues = OctantOrder.SparseQueues;
            var forwardKey = OctantOrder.Key(in Matrix);
            var reverseKey = forwardKey ^ 0b11100000000;
            
            var nodeStackPtr = stackalloc StackItem[MaxSubdivisions * 8];
            
            nodeStackPtr[0] = rootInfo;
            
            var deltasPtr = stackalloc Delta[8];
            CalculateDeltas(deltasPtr);
            
            // var mapThreshold8 = (MapThreshold * 3) / 2;
            var mapThreshold8 = MapThreshold * 2;
            var useMap8 = (mapThreshold8 > MapThreshold);
            
            var mapX = stackalloc byte[MapSize];
            var mapY = stackalloc byte[MapSize];
            var mapX8 = stackalloc ulong[MapSize];
            var mapY8 = stackalloc ulong[MapSize];
            var mapShift = CalculateMaps(deltasPtr, mapX, mapY, mapX8, mapY8, useMap8);
            
            var instanceInfo = new InstanceInfo {
                Matrix = Matrix,
                Octree = Octree,
                RootAddress = RootAddress,
            };
            Renderbuffer.AddInstanceInfo(instanceInfo);
            
            Renderbuffer.GetTileBufferInfo(out var tileShift, out var tileArea);
            
            var buffers = Renderbuffer.GetBuffers();
            
            var queuesPtr = (OctantOrder.Queue*)queuesRef;
            var traceBufferPtr = (Fragment*)traceBufferRef;
            var traceTilesPtr = (TileTraceInfo*)traceTilesRef;
            {
                var unsafeRenderer = new OctreeRendererUnsafe {
                    Viewport = Viewport,
                    Buffers = buffers,
                    InstanceIndex = Renderbuffer.InstanceCount - 1,
                    
                    Octree = Octree.GetPointers(),
                    
                    ExtentX = extentX,
                    ExtentY = extentY,
                    ExtentZ = extentZ,
                    Deltas = deltasPtr,
                    NodeStack = nodeStackPtr,
                    
                    ForwardQueues = queuesPtr + forwardKey,
                    ReverseQueues = queuesPtr + reverseKey,
                    
                    TraceBuffer = traceBufferPtr,
                    TraceTiles = traceTilesPtr,
                    FirstTile = firstTile,
                    LastTile = lastTile,
                    
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
                
                DrawnPixels = unsafeRenderer.Render(cleanup);
                
                firstTile = unsafeRenderer.FirstTile;
                lastTile = unsafeRenderer.LastTile;
            }
            
            return Result.Rendered;
        }
        
        private void InitializeTraceBuffer() {
            int viewportArea = (Viewport.SizeX + 1) * (Viewport.SizeY + 1);
            int traceBufferSize = (traceBuffer != null ? traceBuffer.Length : 0);
            
            if (viewportArea <= traceBufferSize) return;
            
            if (traceBufferSize == 0) traceBufferSize = 1;
            
            while (traceBufferSize < viewportArea) traceBufferSize *= 2;
            
            traceBuffer = new Fragment[traceBufferSize];
            
            Renderbuffer.GetTileBufferInfo(out var tileShift, out var tileArea);
            traceTiles = new TileTraceInfo[tileArea];
            firstTile = -1;
            lastTile = -1;
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
                        
                        var subMask = ((ulong)1 << subOctant) << (octant * 8);
                        
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
            
            public Octree.Pointers Octree;
            
            public int ExtentX, ExtentY, ExtentZ;
            public Delta* Deltas;
            public StackItem* NodeStack;
            
            public OctantOrder.Queue* ForwardQueues;
            public OctantOrder.Queue* ReverseQueues;
            
            public Fragment* TraceBuffer;
            public TileTraceInfo* TraceTiles;
            public int FirstTile;
            public int LastTile;
            
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
            
            public int Render(bool cleanup = true) {
                if (cleanup) UpdateTiles();
                
                int mapHalf = (MapSize << MapShift) >> 1;
                
                var stackTop = NodeStack;
                
                var traceFront = TraceBuffer;
                
                fullQueue = ForwardQueues[255].Octants;
                mask8Bit0 = 0;
                mask8Bit1 = 0;
                mask8Bit2 = 0;
                for (uint item = 0, queue = fullQueue; item < 8; item++, queue >>= 4) {
                    var octantMask = (ulong)255 << (int)((queue & 7) * 8);
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
                
                var f = default(Fragment);
                
                while (stackTop >= NodeStack) {
                    // We need a copy anyway for subnode processing
                    var current = *stackTop;
                    --stackTop;
                    
                    {
                        #if USE_PROFILING
                        var time = spinTimer.Ticks;
                        #endif
                        
                        var txMin = current.MinX & ~Renderbuffer.TileMaskX;
                        var txMax = current.MaxX & ~Renderbuffer.TileMaskX;
                        var tyMin = current.MinY & ~Renderbuffer.TileMaskY;
                        var tyMax = current.MaxY & ~Renderbuffer.TileMaskY;
                        var tileRow = tyMin >> Renderbuffer.TileShiftY;
                        var tileCol = txMin >> Renderbuffer.TileShiftX;
                        for (var ty = tyMin; ty <= tyMax; ty += Renderbuffer.TileSizeY, tileRow++) {
                            var tile = Buffers.Stencil + (tileCol + (tileRow << Buffers.TileShift));
                            for (var tx = txMin; tx <= txMax; tx += Renderbuffer.TileSizeX, tile++) {
                                var pixelMask = tile->Self &
                                    (stencilX[current.MinX - tx] ^ stencilX[current.MaxX - tx + 1]) &
                                    (stencilY[current.MinY - ty] ^ stencilY[current.MaxY - ty + 1]);
                                // This is apparently faster here than masking by delta-depth sign
                                if (current.Z >= tile->Depth) pixelMask &= tile->Scene;
                                if (pixelMask != 0) goto OcclusionTestPassed;
                            }
                            current.MinY = ty + Renderbuffer.TileSizeY;
                        }
                        #if USE_PROFILING
                        Timing.Occlusion += spinTimer.Ticks - time;
                        #endif
                        continue;
                        OcclusionTestPassed:;
                        #if USE_PROFILING
                        Timing.Occlusion += spinTimer.Ticks - time;
                        #endif
                    }
                    
                    var nodeMask = Octree.Mask[current.Address];
                    
                    const int TSX = Renderbuffer.TileShiftX;
                    const int TSY = Renderbuffer.TileShiftY;
                    const int TMX = Renderbuffer.TileMaskX;
                    const int TMY = Renderbuffer.TileMaskY;
                    
                    if (current.MaxSize < 1) {
                        #if USE_PROFILING
                        var time = spinTimer.Ticks;
                        #endif
                        
                        if ((nodeMask == 0) | (MapThreshold > 1)) {
                            f.X = current.MinX;
                            f.Y = current.MinY;
                            var stencilIndex = (f.X >> TSX) + ((f.Y >> TSY) << Buffers.TileShift);
                            var stencilMask = 1UL << ((f.X & TMX) + ((f.Y & TMY) << TSX));
                            Buffers.Stencil[stencilIndex].Self &= ~stencilMask;
                            
                            f.Z = current.Z;
                            f.Address = current.Address;
                            *(traceFront++) = f;
                        } else {
                            int mx = ((current.MinX << SubpixelBits) + SubpixelHalf) - (current.X - (mapHalf >> current.Level));
                            int my = ((current.MinY << SubpixelBits) + SubpixelHalf) - (current.Y - (mapHalf >> current.Level));
                            int mapShift = MapShift - current.Level;
                            int mask = MapX[mx >> mapShift] & MapY[my >> mapShift] & nodeMask;
                            
                            if (mask != 0) {
                                f.X = current.MinX;
                                f.Y = current.MinY;
                                var stencilIndex = (f.X >> TSX) + ((f.Y >> TSY) << Buffers.TileShift);
                                var stencilMask = 1UL << ((f.X & TMX) + ((f.Y & TMY) << TSX));
                                Buffers.Stencil[stencilIndex].Self &= ~stencilMask;
                                
                                var nodeAddress = Octree.Addr[current.Address];
                                
                                var octant = ForwardQueues[mask].Octants & 7;
                                f.Z = current.Z + (Deltas[octant].Z >> current.Level);
                                f.Address = nodeAddress + octant;
                                *(traceFront++) = f;
                            }
                        }
                        
                        #if USE_PROFILING
                        Timing.Pixel += spinTimer.Ticks - time;
                        #endif
                    } else if ((nodeMask == 0) | (current.Level >= MaxLevel)) {
                        #if USE_PROFILING
                        var time = spinTimer.Ticks;
                        #endif
                        
                        if (current.MaxSize > 1) {
                            current.Z += ExtentZ >> current.Level;
                        } else {
                            current.Z += ExtentZ >> current.Level;
                        }
                        
                        f.Z = current.Z;
                        f.Address = current.Address;
                        
                        for (f.Y = current.MinY; f.Y <= current.MaxY; f.Y++) {
                            for (f.X = current.MinX; f.X <= current.MaxX; f.X++) {
                                var stencilIndex = (f.X >> TSX) + ((f.Y >> TSY) << Buffers.TileShift);
                                var stencilMask = 1UL << ((f.X & TMX) + ((f.Y & TMY) << TSX));
                                if ((Buffers.Stencil[stencilIndex].Self & stencilMask) != 0) {
                                    Buffers.Stencil[stencilIndex].Self &= ~stencilMask;
                                    *(traceFront++) = f;
                                }
                            }
                        }
                        
                        #if USE_PROFILING
                        Timing.Leaf += spinTimer.Ticks - time;
                        #endif
                    } else if (current.MaxSize < MapThreshold) {
                        #if USE_PROFILING
                        var time = spinTimer.Ticks;
                        #endif
                        
                        int mapStartX = ((current.MinX << SubpixelBits) + SubpixelHalf) - (current.X - (mapHalf >> current.Level));
                        int mapStartY = ((current.MinY << SubpixelBits) + SubpixelHalf) - (current.Y - (mapHalf >> current.Level));
                        int mapShift = MapShift - current.Level;
                        
                        var nodeAddress = Octree.Addr[current.Address];
                        
                        int my, mx;
                        for (my = mapStartY, f.Y = current.MinY; f.Y <= current.MaxY; f.Y++, my += SubpixelSize) {
                            int maskY = MapY[my >> mapShift] & nodeMask;
                            for (mx = mapStartX, f.X = current.MinX; f.X <= current.MaxX; f.X++, mx += SubpixelSize) {
                                int mask = MapX[mx >> mapShift] & maskY;
                                
                                if (mask == 0) continue;
                                
                                var stencilIndex = (f.X >> TSX) + ((f.Y >> TSY) << Buffers.TileShift);
                                var stencilMask = 1UL << ((f.X & TMX) + ((f.Y & TMY) << TSX));
                                if ((Buffers.Stencil[stencilIndex].Self & stencilMask) != 0) {
                                    Buffers.Stencil[stencilIndex].Self &= ~stencilMask;
                                    
                                    var octant = ForwardQueues[mask].Octants & 7;
                                    f.Z = current.Z + (Deltas[octant].Z >> current.Level);
                                    f.Address = nodeAddress + octant;
                                    *(traceFront++) = f;
                                }
                            }
                        }
                        
                        #if USE_PROFILING
                        Timing.Map += spinTimer.Ticks - time;
                        #endif
                    } else {
                        if (current.MaxSize < MapThreshold8) {
                            #if USE_PROFILING
                            var time = spinTimer.Ticks;
                            #endif
                            
                            var nodeAddress = Octree.Addr[current.Address];
                            
                            ulong mask8 = 0;
                            for (int octant = 0, mshift = 0; octant < 8; octant++, mshift += 8) {
                                var octantMask = Octree.Mask[nodeAddress + octant];
                                if ((octantMask == 0) & (((nodeMask >> octant) & 1) != 0)) octantMask = 255;
                                mask8 |= ((ulong)octantMask) << mshift;
                            }
                            
                            int mapStartX = ((current.MinX << SubpixelBits) + SubpixelHalf) - (current.X - (mapHalf >> current.Level));
                            int mapStartY = ((current.MinY << SubpixelBits) + SubpixelHalf) - (current.Y - (mapHalf >> current.Level));
                            int mapShift = MapShift - current.Level;
                            
                            #if USE_PROFILING
                            var time2 = spinTimer.Ticks;
                            Timing.Map8Pre += time2 - time;
                            #endif
                            
                            var useSimple = (current.MaxSize < MapThreshold8);
                            
                            int my, mx;
                            for (my = mapStartY, f.Y = current.MinY; f.Y <= current.MaxY; f.Y++, my += SubpixelSize) {
                                var maskY = MapY8[my >> mapShift] & mask8;
                                for (mx = mapStartX, f.X = current.MinX; f.X <= current.MaxX; f.X++, mx += SubpixelSize) {
                                    var mask = MapX8[mx >> mapShift] & maskY;
                                    
                                    if (mask == 0) continue;
                                    
                                    var stencilIndex = (f.X >> TSX) + ((f.Y >> TSY) << Buffers.TileShift);
                                    var stencilMask = 1UL << ((f.X & TMX) + ((f.Y & TMY) << TSX));
                                    if ((Buffers.Stencil[stencilIndex].Self & stencilMask) != 0) {
                                        Buffers.Stencil[stencilIndex].Self &= ~stencilMask;
                                        
                                        octant8Bit2.BoolValue = (mask & mask8Bit2) == 0;
                                        mask &= mask8Bit2 ^ unchecked((ulong)(-octant8Bit2.ByteValue));
                                        octant8Bit1.BoolValue = (mask & mask8Bit1) == 0;
                                        mask &= mask8Bit1 ^ unchecked((ulong)(-octant8Bit1.ByteValue));
                                        octant8Bit0.BoolValue = (mask & mask8Bit0) == 0;
                                        int queueItem8 = octant8Bit0.ByteValue |
                                                        (octant8Bit1.ByteValue << 1) |
                                                        (octant8Bit2.ByteValue << 2);
                                        var octant8 = (fullQueue >> (queueItem8 << 2)) & 7;
                                        
                                        f.Z = current.Z + (Deltas[octant8].Z >> current.Level);
                                        
                                        var octantMask = Octree.Mask[nodeAddress + octant8];
                                        if ((octantMask == 0) | useSimple) {
                                            f.Address = nodeAddress + octant8;
                                        } else {
                                            var address = Octree.Addr[nodeAddress + octant8];
                                            var octant = ForwardQueues[octantMask].Octants & 7;
                                            f.Address = address + octant;
                                        }
                                        
                                        *(traceFront++) = f;
                                    }
                                }
                            }
                            
                            #if USE_PROFILING
                            var time3 = spinTimer.Ticks;
                            Timing.Map8Loop += time3 - time2;
                            Timing.Map8 += time3 - time;
                            #endif
                            continue;
                        }
                        
                        // {
                        //     int j = current.MinX + (current.MinY << Buffers.Shift);
                        //     int jEnd = current.MinX + (current.MaxY << Buffers.Shift);
                        //     int iEnd = current.MaxX + (current.MinY << Buffers.Shift);
                        //     int jStep = 1 << Buffers.Shift;
                        //     for (; j <= jEnd; j += jStep, iEnd += jStep) {
                        //         for (int i = j; i <= iEnd; i++) {
                        //             if (current.Z < Buffers.Depth[i]) goto OcclusionTestPassed;
                        //         }
                        //         current.MinY++;
                        //     }
                        //     continue;
                        //     OcclusionTestPassed:;
                        // }
                        
                        {
                        #if USE_PROFILING
                        var time = spinTimer.Ticks;
                        #endif
                        
                        var nodeAddress = Octree.Addr[current.Address];
                        
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
                            stackTop->Address = nodeAddress + octant;
                            stackTop->Level = nextLevel;
                        }
                        
                        #if USE_PROFILING
                        Timing.Stack += spinTimer.Ticks - time;
                        #endif
                        }
                    }
                }
                
                {
                    #if USE_PROFILING
                    var time = spinTimer.Ticks;
                    #endif
                    
                    const int FarPlane = Renderbuffer.FarPlane;
                    
                    for (var fragment = TraceBuffer; fragment != traceFront; fragment++) {
                        // We need to clear self-stencil even if fragments are rejected by depth
                        var tx = fragment->X >> Renderbuffer.TileShiftX;
                        var ty = fragment->Y >> Renderbuffer.TileShiftY;
                        var tileIndex = tx + (ty << Buffers.TileShift);
                        if (TraceTiles[tileIndex].UpdateStencil == 0) {
                            TraceTiles[tileIndex].UpdateStencil = 1;
                            TraceTiles[tileIndex].Next = -1;
                            if (LastTile < 0) {
                                FirstTile = tileIndex;
                            } else {
                                TraceTiles[LastTile].Next = tileIndex;
                            }
                            LastTile = tileIndex;
                        }
                        
                        int i = fragment->X + (fragment->Y << Buffers.Shift);
                        
                        if (fragment->Z < Buffers.Depth[i]) {
                            // Skip max depth recalculation if the pixel
                            // we're about to overwrite was at far plane
                            if (Buffers.Depth[i] == FarPlane) {
                                var tile = Buffers.Stencil + tileIndex;
                                if ((tile->Depth == FarPlane) | (fragment->Z > tile->Depth)) {
                                    tile->Depth = fragment->Z;
                                }
                            } else {
                                TraceTiles[tileIndex].UpdateDepth = 1;
                            }
                            
                            Buffers.Depth[i] = fragment->Z;
                            Buffers.Instance[i] = InstanceIndex;
                            Buffers.Address[i] = fragment->Address;
                        }
                    }
                    
                    #if USE_PROFILING
                    Timing.Write += spinTimer.Ticks - time;
                    #endif
                }
                
                return (int)(traceFront - TraceBuffer);
            }
            
            private void UpdateTiles() {
                const int FarPlane = Renderbuffer.FarPlane;
                
                var tileRowMask = (1 << Buffers.TileShift) - 1;
                
                var nextTile = FirstTile;
                while (nextTile >= 0) {
                    var tileIndex = nextTile;
                    nextTile = TraceTiles[tileIndex].Next;
                    TraceTiles[tileIndex].Next = -1;
                    
                    var tile = Buffers.Stencil + tileIndex;
                    
                    if (TraceTiles[tileIndex].UpdateStencil != 0) {
                        TraceTiles[tileIndex].UpdateStencil = 0;
                        
                        tile->Scene &= tile->Self;
                        tile->Self = Renderbuffer.StencilClear;
                    }
                    
                    if (TraceTiles[tileIndex].UpdateDepth != 0) {
                        TraceTiles[tileIndex].UpdateDepth = 0;
                        
                        var maxDepth = -1;
                        var txMin = (tileIndex & tileRowMask) << Renderbuffer.TileShiftX;
                        var tyMin = (tileIndex >> Buffers.TileShift) << Renderbuffer.TileShiftY;
                        var tyMax = tyMin + Renderbuffer.TileSizeY;
                        for (var y = tyMin; y < tyMax; y++) {
                            var iMin = txMin + (y << Buffers.Shift);
                            var iMax = iMin + Renderbuffer.TileSizeX;
                            for (var i = iMin; i < iMax; i++) {
                                if ((Buffers.Depth[i] < FarPlane) & (Buffers.Depth[i] > maxDepth)) {
                                    maxDepth = Buffers.Depth[i];
                                }
                            }
                        }
                        if (maxDepth >= 0) {
                            tile->Depth = maxDepth;
                        }
                    }
                }
                
                FirstTile = -1;
                LastTile = -1;
            }
        }
    }
}
