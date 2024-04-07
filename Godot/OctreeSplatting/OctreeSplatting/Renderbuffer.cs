// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public class Renderbuffer {
        public unsafe struct Pointers {
            public int Shift;
            public int SizeX;
            public int SizeY;
            public int SizeZ;
            public int MinX;
            public int MinY;
            public int* Depth;
            public Color32* Color;
        }
        
        public const int DepthBits = 24;
        
        private int sizeX, sizeY;
        private int tilesX, tilesY;
        private int[] depthData;
        private Color32[] colorData;
        private UnsafeRef depthDataRef;
        private UnsafeRef colorDataRef;
        private Color32[] finalPixels;
        
        public int SizeX => sizeX;
        public int SizeY => sizeY;
        public int SizeZ => 1 << DepthBits;
        public Color32[] ColorPixels => finalPixels;
        
        public int DataSizeX => UseTemporalUpscaling ? sizeX >> 1 : sizeX;
        public int DataSizeY => UseTemporalUpscaling ? sizeY >> 1 : sizeY;
        
        public int FrameCount;
        
        public bool UseTemporalUpscaling;
        
        public void Resize(int width, int height) {
            if ((width <= 0) | (height <= 0)) return;
            if ((width == sizeX) & (height == sizeY)) return;
            
            sizeX = width;
            sizeY = height;
            
            var tiles = ToTiles(new Range2D {MaxX = sizeX - 1, MaxY = sizeY - 1});
            tilesX = tiles.MaxX + 1;
            tilesY = tiles.MaxY + 1;
            depthData = new int[32 * 32 * tilesX * tilesY];
            colorData = new Color32[32 * 32 * tilesX * tilesY];
            
            finalPixels = new Color32[sizeX * sizeY];
        }
        
        public unsafe void Begin(Color32 background) {
            if (colorData == null) return;
            
            depthDataRef.Set(depthData);
            colorDataRef.Set(colorData);
            
            var tiles = ToTiles(new Range2D {MaxX = sizeX - 1, MaxY = sizeY - 1});
            for (var ty = tiles.MinY; ty <= tiles.MaxY; ty++) {
                for (var tx = tiles.MinX; tx <= tiles.MaxX; tx++) {
                    var buffers = GetBuffers(tx, ty);
                    for (int y = 0; y < buffers.SizeY; y++) {
                        int dataIndex = y << buffers.Shift;
                        for (int x = 0; x < buffers.SizeX; x++, dataIndex++) {
                            buffers.Depth[dataIndex] = buffers.SizeZ;
                            buffers.Color[dataIndex] = background;
                        }
                    }
                }
            }
        }
        
        public unsafe Pointers GetBuffers(int tx, int ty) {
            var pixelOffset = 32 * 32 * (tx + tilesX * ty);
            return new Pointers {
                Shift = 5,
                SizeX = 32,
                SizeY = 32,
                SizeZ = 1 << DepthBits,
                MinX = tx * 32,
                MinY = ty * 32,
                Depth = (int*)depthDataRef + pixelOffset,
                Color = (Color32*)colorDataRef + pixelOffset,
            };
        }
        
        public unsafe void End() {
            if (colorData == null) return;
            
            var step = UseTemporalUpscaling ? 2 : 1;
            GetSamplingOffset(out int startX, out int startY);
            
            var tiles = ToTiles(new Range2D {MaxX = sizeX - 1, MaxY = sizeY - 1});
            fixed (Color32* finalPtr = finalPixels)
            {
                for (var ty = tiles.MinY; ty <= tiles.MaxY; ty++) {
                    for (var tx = tiles.MinX; tx <= tiles.MaxX; tx++) {
                        var buffers = GetBuffers(tx, ty);
                        var tileStep = buffers.SizeX * step;
                        var xMin = tx * tileStep + startX;
                        var xMax = xMin + tileStep;
                        var yMin = ty * tileStep + startY;
                        var yMax = yMin + tileStep;
                        if (xMax > sizeX) xMax = sizeX;
                        if (yMax > sizeY) yMax = sizeY;
                        for (int y = yMin, yData = 0; y < yMax; y += step, yData++) {
                            int dataIndex = yData << buffers.Shift;
                            int colorIndex = xMin + (y * sizeX);
                            for (int x = xMin; x < xMax; x += step, colorIndex += step, dataIndex++) {
                                var dataPixel = buffers.Color + dataIndex;
                                var finalPixel = finalPtr + colorIndex;
                                *finalPixel = *dataPixel;
                            }
                        }
                    }
                }
            }
            
            FrameCount++;
            
            depthDataRef.Clear();
            colorDataRef.Clear();
        }
        
        public Range2D ToTiles(Range2D range) {
            range.MinX = ((range.MinX < 0) ? 0 : range.MinX) >> 5;
            range.MinY = ((range.MinY < 0) ? 0 : range.MinY) >> 5;
            range.MaxX = ((range.MaxX >= sizeX) ? sizeX - 1 : range.MaxX) >> 5;
            range.MaxY = ((range.MaxY >= sizeY) ? sizeY - 1 : range.MaxY) >> 5;
            return range;
        }
        
        public void GetSamplingOffset(out float x, out float y) {
            GetSamplingOffset(out int ix, out int iy);
            x = (0.5f - ix) * 0.5f;
            y = (0.5f - iy) * 0.5f;
        }
        
        public void GetSamplingOffset(out int x, out int y) {
            x = 0; y = 0;
            
            if (!UseTemporalUpscaling) return;
            
            switch (FrameCount & 0b11) {
                case 0: x = 0; y = 0; return;
                case 1: x = 1; y = 1; return;
                case 2: x = 1; y = 0; return;
                case 3: x = 0; y = 1; return;
            }
        }
    }
}
