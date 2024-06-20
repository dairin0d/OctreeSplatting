// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public static class Lookups {
        public unsafe struct Pointers {
            // For convenience, these are offset by StencilMargin,
            // so they can be indexed directly by the pixel coords
            public ulong* StencilX;
            public ulong* StencilY;
            
            public BlitSequence* BlitSequences;
        }
        
        public struct BlitSequence {
            public ushort XSamples;
            public ushort YSamples;
            public int Count;
            public uint XCoords;
            public uint YCoords;
        }
        
        public const int StencilMargin = 1024;
        
        public static ulong[] StencilX;
        public static ulong[] StencilY;
        public static BlitSequence[] BlitSequences;
        
        private static bool hasPointers;
        private static Pointers pointers;
        private static UnsafeRef refStencilX;
        private static UnsafeRef refStencilY;
        private static UnsafeRef refBlitSequences;
        
        static Lookups() {
            // StencilX = MakeStencil(Renderbuffer.TileSizeX, 0x0101010101010101UL, 1);
            // StencilY = MakeStencil(Renderbuffer.TileSizeY, 0xFFUL, 8);
            StencilX = MakeStencil(Renderbuffer.TileSizeX, 0x1111UL, 1);
            StencilY = MakeStencil(Renderbuffer.TileSizeY, 0xFUL, 4);
            BlitSequences = MakeBlitSequences();
        }
        
        public static unsafe Pointers GetPointers() {
            if (!hasPointers) {
                refStencilX.Set(StencilX);
                refStencilY.Set(StencilY);
                refBlitSequences.Set(BlitSequences);
                pointers = new Pointers {
                    StencilX = ((ulong*)refStencilX) + StencilMargin,
                    StencilY = ((ulong*)refStencilY) + StencilMargin,
                    BlitSequences = (BlitSequence*)refBlitSequences,
                };
                hasPointers = true;
            }
            return pointers;
        }
        
        public static void FreePointers() {
            if (!hasPointers) return;
            pointers = default;
            refStencilX.Clear();
            refStencilY.Clear();
            refBlitSequences.Clear();
            hasPointers = false;
        }
        
        private static ulong[] MakeStencil(int size, ulong mask, int step) {
            var result = new ulong[size + 1 + StencilMargin * 2];
            for (int i = 1; i < size; i++) {
                result[StencilMargin + i] = result[StencilMargin + i - 1] | (mask << ((i-1) * step));
            }
            // for (int i = 1; i < size; i++) {
            //     result[StencilMargin + i] = RearrangeStencil(result[StencilMargin + i]);
            // }
            System.Array.Fill(result, ulong.MaxValue, StencilMargin + size, StencilMargin + 1);
            return result;
        }
        
        private static ulong RearrangeStencil(ulong mask) {
            ulong newMask = 0;
            newMask |= GetStencilQuad(mask, 0) << 0;
            newMask |= GetStencilQuad(mask, 4) << 16;
            newMask |= GetStencilQuad(mask, 32) << 32;
            newMask |= GetStencilQuad(mask, 36) << 48;
            return newMask;
        }
        
        private static ulong GetStencilQuad(ulong mask, int offset) {
            ulong quad = 0;
            for (var row = 0; row < 4; row++, offset += 8) {
                quad |= ((mask >> offset) & 0b1111) << (row * 4);
            }
            return quad;
        }
        
        private static BlitSequence[] MakeBlitSequences()
        {
            var result = new BlitSequence[1 << (4*4)];
            
            for (var mask = 0; mask < result.Length; mask++) {
                var count = 0;
                var xcoords = 0;
                var ycoords = 0;
                var xmask = 0;
                var ymask = 0;
                var xsamples = 0;
                var ysamples = 0;
                
                for (var y = 0; y < 4; y++) {
                    for (var x = 0; x < 4; x++) {
                        var i = x + y * 4;
                        if ((mask & (1 << i)) == 0) continue;
                        xmask |= 1 << x;
                        ymask |= 1 << y;
                        xcoords |= x << (count * 2);
                        ycoords |= y << (count * 2);
                        count++;
                    }
                }
                
                for (var x = 3; x >= 0; x--) {
                    if ((xmask & (1 << x)) == 0) continue;
                    xsamples = (xsamples << 3) | (x | 0b100);
                }
                
                for (var y = 3; y >= 0; y--) {
                    if ((ymask & (1 << y)) == 0) continue;
                    ysamples = (ysamples << 3) | (y | 0b100);
                }
                
                result[mask] = new BlitSequence {
                    XSamples = (ushort)xsamples,
                    YSamples = (ushort)ysamples,
                    Count = count,
                    XCoords = unchecked((uint)xcoords),
                    YCoords = unchecked((uint)ycoords),
                };
            }
            
            return result;
        }
    }
}
