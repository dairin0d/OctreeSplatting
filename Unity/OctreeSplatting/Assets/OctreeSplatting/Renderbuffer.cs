// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public class Renderbuffer {
        public const int DepthBits = 24;
        
        private int shiftX;
        private int sizeX, sizeY;
        private PixelData[] dataPixels;
        private Color32[] colorPixels;
        private Color32[] finalPixels;
        
        public int ShiftX => shiftX;
        public int SizeX => sizeX;
        public int SizeY => sizeY;
        public int SizeZ => 1 << DepthBits;
        public PixelData[] DataPixels => dataPixels;
        public Color32[] ColorPixels => finalPixels;
        
        public int DataSizeX => UseTemporalUpscaling ? sizeX >> 1 : sizeX;
        public int DataSizeY => UseTemporalUpscaling ? sizeY >> 1 : sizeY;
        
        public int FrameCount;
        
        public bool UseTemporalUpscaling;
        public bool UseTemporalBlending;
        
        public void Resize(int width, int height) {
            if ((width <= 0) | (height <= 0)) return;
            if ((width == sizeX) & (height == sizeY)) return;
            
            sizeX = width;
            sizeY = height;
            
            for (shiftX = 0; (1 << shiftX) < sizeX; shiftX++);
            
            dataPixels = new PixelData[(1 << shiftX) * sizeY];
            colorPixels = new Color32[sizeX * sizeY];
            finalPixels = new Color32[sizeX * sizeY];
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
            
            int subStepX = startX == 0 ? 1 : -1;
            int subStepY = startY == 0 ? sizeX : -sizeX;
            
            fixed (PixelData* dataPtr = dataPixels)
            fixed (Color32* colorPtr = colorPixels)
            fixed (Color32* finalPtr = finalPixels)
            {
                for (int y = startY, yData = 0; y < sizeY; y += step, yData++) {
                    int dataIndex = yData << shiftX;
                    int colorIndex = startX + (y * sizeX);
                    for (int x = startX; x < sizeX; x += step, colorIndex += step, dataIndex++) {
                        var dataPixel = dataPtr + dataIndex;
                        var colorPixel = colorPtr + colorIndex;
                        var finalPixel = finalPtr + colorIndex;
                        
                        byte originalR = colorPixel->R;
                        byte originalG = colorPixel->G;
                        byte originalB = colorPixel->B;
                        
                        *colorPixel = dataPixel->Color32;
                        *finalPixel = *colorPixel;
                        
                        if (UseTemporalUpscaling & UseTemporalBlending) {
                            int deltaR = colorPixel->R - originalR;
                            if (deltaR < 0) deltaR = -deltaR;
                            int deltaG = colorPixel->G - originalG;
                            if (deltaG < 0) deltaG = -deltaG;
                            int deltaB = colorPixel->B - originalB;
                            if (deltaB < 0) deltaB = -deltaB;
                            int deltaSum = deltaR + deltaG + deltaB;
                            int weight = (deltaSum <= 255 ? deltaSum : 255) >> 1;
                            int inverse = 255 - weight;
                            
                            for (int subY = 0; subY >= -1; subY--) {
                                for (int subX = 0; subX >= -1; subX--) {
                                    if ((subX | subY) == 0) continue;
                                    int subOffset = (subStepX & subX) + (subStepY & subY);
                                    var colorPixel2 = colorPixel + subOffset;
                                    var finalPixel2 = finalPixel + subOffset;
                                    finalPixel2->R = (byte)((colorPixel2->R * inverse + colorPixel->R * weight + 255) >> 8);
                                    finalPixel2->G = (byte)((colorPixel2->G * inverse + colorPixel->G * weight + 255) >> 8);
                                    finalPixel2->B = (byte)((colorPixel2->B * inverse + colorPixel->B * weight + 255) >> 8);
                                }
                            }
                        }
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
