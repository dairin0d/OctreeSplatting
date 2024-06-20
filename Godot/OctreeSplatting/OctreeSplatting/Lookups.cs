// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public static class Lookups {
        public unsafe struct Pointers {
            // For convenience, these are offset by StencilMargin,
            // so they can be indexed directly by the pixel coords
            public ulong* StencilX;
            public ulong* StencilY;
        }
        
        public const int StencilMargin = 1024;
        
        public static ulong[] StencilX;
        public static ulong[] StencilY;
        
        private static bool hasPointers;
        private static Pointers pointers;
        private static UnsafeRef refStencilX;
        private static UnsafeRef refStencilY;
        
        static Lookups() {
            StencilX = MakeStencil(Renderbuffer.TileSizeX, 0x0101010101010101UL, 1);
            StencilY = MakeStencil(Renderbuffer.TileSizeY, 0xFFUL, 8);
        }
        
        public static unsafe Pointers GetPointers() {
            if (!hasPointers) {
                refStencilX.Set(StencilX);
                refStencilY.Set(StencilY);
                pointers = new Pointers {
                    StencilX = ((ulong*)refStencilX) + StencilMargin,
                    StencilY = ((ulong*)refStencilY) + StencilMargin,
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
            hasPointers = false;
        }
        
        private static ulong[] MakeStencil(int size, ulong mask, int step) {
            var result = new ulong[size + 1 + StencilMargin * 2];
            for (int i = 1; i < size; i++) {
                result[StencilMargin + i] = result[StencilMargin + i - 1] | (mask << ((i-1) * step));
            }
            System.Array.Fill(result, ulong.MaxValue, StencilMargin + size, StencilMargin + 1);
            return result;
        }
    }
}
