// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public class Renderbuffer {
        public const int DepthBits = 24;
        
        private int shiftX;
        private int sizeX, sizeY;
        private PixelData[] dataPixels;
        private Color32[] colorPixels;
        
        public int ShiftX => shiftX;
        public int SizeX => sizeX;
        public int SizeY => sizeY;
        public int SizeZ => 1 << DepthBits;
        public PixelData[] DataPixels => dataPixels;
        public Color32[] ColorPixels => colorPixels;
        
        public int DataSizeX => UseTemporalUpscaling ? sizeX >> 1 : sizeX;
        public int DataSizeY => UseTemporalUpscaling ? sizeY >> 1 : sizeY;
        
        public int FrameCount;
        
        public bool UseTemporalUpscaling;
        
        public void Resize(int width, int height) {
            if ((width <= 0) | (height <= 0)) return;
            if ((width == sizeX) & (height == sizeY)) return;
            
            sizeX = width;
            sizeY = height;
            
            for (shiftX = 0; (1 << shiftX) < sizeX; shiftX++);
            
            dataPixels = new PixelData[(1 << shiftX) * sizeY];
            colorPixels = new Color32[sizeX * sizeY];
        }
        
        public unsafe void Begin(Color32 background) {
            if (dataPixels == null) return;
            
            var defaultValue = default(PixelData);
            defaultValue.Depth = SizeZ;
            defaultValue.Color32 = background;
            
            fixed (PixelData* dataPtr = dataPixels)
            {
                for (int y = 0; y < sizeY; y++) {
                    int dataIndex = y << shiftX;
                    for (int x = 0; x < sizeX; x++, dataIndex++) {
                        dataPtr[dataIndex] = defaultValue;
                    }
                }
            }
        }
        
        public unsafe void End() {
            if (dataPixels == null) return;
            if (colorPixels == null) return;
            
            int step = UseTemporalUpscaling ? 2 : 1;
            GetSamplingOffset(out int startX, out int startY);
            
            fixed (PixelData* dataPtr = dataPixels)
            fixed (Color32* colorPtr = colorPixels)
            {
                for (int y = startY, yData = 0; y < sizeY; y += step, yData++) {
                    int dataIndex = yData << shiftX;
                    int colorIndex = startX + (y * sizeX);
                    for (int x = startX; x < sizeX; x += step, colorIndex += step, dataIndex++) {
                        var dataPixel = dataPtr + dataIndex;
                        var colorPixel = colorPtr + colorIndex;
                        
                        *colorPixel = dataPixel->Color32;
                    }
                }
            }
            
            FrameCount++;
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
