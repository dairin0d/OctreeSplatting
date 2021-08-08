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
            
            fixed (PixelData* dataPtr = dataPixels)
            fixed (Color32* colorPtr = colorPixels)
            {
                for (int y = 0; y < sizeY; y++) {
                    int dataIndex = y << shiftX;
                    int colorIndex = y * sizeX;
                    for (int x = 0; x < sizeX; x++, dataIndex++, colorIndex++) {
                        colorPtr[colorIndex] = dataPtr[dataIndex].Color32;
                    }
                }
            }
        }
    }
}
