// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
	public class Renderbuffer {
		public struct StencilData {
			public ulong Self;
			public ulong Scene;
			public int Depth;
			public int Pad0;
		}
		
		public unsafe struct Pointers {
			public int Shift;
			public int SizeX;
			public int SizeY;
			public int SizeZ;
			public int TileShift;
			public int TilesX;
			public int TilesY;
			public int* Depth;
			public uint* Address;
			public uint* Instance;
			public Color32* Color;
			public StencilData* Stencil;
		}
		
		public const int TileShiftX = 3;
		public const int TileShiftY = 3;
		public const int TileSizeX = 1 << TileShiftX;
		public const int TileSizeY = 1 << TileShiftY;
		public const int TileMaskX = TileSizeX - 1;
		public const int TileMaskY = TileSizeY - 1;
		public const int TileArea = TileSizeX * TileSizeY;
		public const ulong StencilClear = ulong.MaxValue >> (64 - TileArea);
		
		public const int DepthBits = 24;
		public const int FarPlane = 1 << DepthBits;
		
		private int sizeX, sizeY, shiftX;
		private int tilesX, tilesY, tileShiftX;
		private byte[] data;
		private UnsafeRef dataRef;
		private Color32[] colorHistory;
		private Color32[] finalPixels;
		private InstanceInfo[] instanceInfos = new InstanceInfo[256];
		private uint instanceCount;
		private Color32 background;
		
		public int SizeX => sizeX;
		public int SizeY => sizeY;
		public int SizeZ => FarPlane;
		public int TilesX => tilesX;
		public int TilesY => tilesY;
		public Color32[] ColorPixels => finalPixels;
		public uint InstanceCount => instanceCount;
		
		public int DataSizeX => UseTemporalUpscaling ? sizeX >> 1 : sizeX;
		public int DataSizeY => UseTemporalUpscaling ? sizeY >> 1 : sizeY;
		
		public int FrameCount;
		
		public bool UseTemporalUpscaling;
		
		public unsafe void Resize(int width, int height) {
			if ((width <= 0) | (height <= 0)) return;
			if ((width == sizeX) & (height == sizeY)) return;
			
			sizeX = width;
			sizeY = height;
			
			var tiles = ToTiles(new Range2D {MaxX = sizeX - 1, MaxY = sizeY - 1});
			tilesX = tiles.MaxX + 1;
			tilesY = tiles.MaxY + 1;
			
			for (shiftX = 0; (1 << shiftX) < sizeX; shiftX++);
			for (tileShiftX = 0; (1 << tileShiftX) < tilesX; tileShiftX++);
			
			var tileArea = (1 << tileShiftX) * tilesY;
			var pixelArea = (1 << shiftX) * sizeY;
			data = new byte[sizeof(StencilData) * tileArea + 4 * sizeof(int) * pixelArea];
			
			finalPixels = new Color32[sizeX * sizeY];
			colorHistory = new Color32[finalPixels.Length];
		}
		
		public unsafe void Begin(Color32 background) {
			this.background = background;
			
			if (data == null) return;
			
			instanceCount = 0;
			
			dataRef.Set(data);
			
			var buffers = GetBuffers();
			
			var tiles = ToTiles(new Range2D {MaxX = sizeX - 1, MaxY = sizeY - 1});
			for (var ty = tiles.MinY; ty <= tiles.MaxY; ty++) {
				for (var tx = tiles.MinX; tx <= tiles.MaxX; tx++) {
					// TODO: mask the parts that are outside of the viewport
					var tileIndex = tx | (ty << buffers.TileShift);
					buffers.Stencil[tileIndex].Self = StencilClear;
					buffers.Stencil[tileIndex].Scene = StencilClear;
					buffers.Stencil[tileIndex].Depth = FarPlane;
				}
			}
			
			for (int y = 0; y < buffers.SizeY; y++) {
				int dataIndex = y << buffers.Shift;
				for (int x = 0; x < buffers.SizeX; x++, dataIndex++) {
					buffers.Depth[dataIndex] = FarPlane;
					buffers.Address[dataIndex] = uint.MaxValue;
					buffers.Instance[dataIndex] = uint.MaxValue;
					buffers.Color[dataIndex] = background;
				}
			}
		}
		
		public void AddInstanceInfo(in InstanceInfo instanceInfo) {
			if (instanceInfos.Length <= instanceCount) {
				System.Array.Resize(ref instanceInfos, instanceInfos.Length * 2);
			}
			instanceInfos[instanceCount] = instanceInfo;
			instanceCount++;
		}
		
		public void GetTileBufferInfo(out int shift, out int area) {
			shift = tileShiftX;
			area = (1 << tileShiftX) * tilesY;
		}
		
		public unsafe Pointers GetBuffers() {
			var buffers = new Pointers {
				Shift = shiftX,
				SizeX = sizeX,
				SizeY = sizeY,
				SizeZ = FarPlane,
				TileShift = tileShiftX,
				TilesX = tilesX,
				TilesY = tilesY,
			};
			
			var dataPtr = (byte*)dataRef;
			var tileArea = (1 << tileShiftX) * tilesY;
			var pixelArea = (1 << shiftX) * sizeY;
			buffers.Stencil = (StencilData*)dataPtr;
			dataPtr += sizeof(StencilData) * tileArea;
			buffers.Depth = (int*)dataPtr;
			dataPtr += sizeof(int) * pixelArea;
			buffers.Address = (uint*)dataPtr;
			dataPtr += sizeof(uint) * pixelArea;
			buffers.Instance = (uint*)dataPtr;
			dataPtr += sizeof(uint) * pixelArea;
			buffers.Color = (Color32*)dataPtr;
			
			return buffers;
		}
		
		public unsafe void End() {
			if (data == null) return;
			
			var step = UseTemporalUpscaling ? 2 : 1;
			GetSamplingOffset(out int startX, out int startY);
			
			int* hitoryCoefs = stackalloc int[2*2];
			for (int frameOffset = -3; frameOffset <= 0; frameOffset++) {
				GetSamplingOffset(out int dx, out int dy, frameOffset);
				hitoryCoefs[dx | (dy << 1)] = ((4 + frameOffset) * 255) / 4;
			}
			
			var buffers = GetBuffers();
			fixed (Color32* finalPtr = finalPixels, historyPtr = colorHistory)
			{
				for (int y = startY, yData = 0; y < sizeY; y += step, yData++) {
					int dataIndex = yData << shiftX;
					int colorIndex = startX + (y * sizeX);
					for (int x = startX; x < sizeX; x += step, colorIndex += step, dataIndex++) {
						// var color = background;
						
						// var instance = buffers.Instance[dataIndex];
						// if (instance < instanceCount) {
						// 	var instanceInfo = instanceInfos[instance];
						// 	var address = buffers.Address[dataIndex];
						// 	if (address < instanceInfo.Octree.Length) {
						// 		color.RGB = instanceInfo.Octree[address].Data;
						// 		color.A = 255;
						// 	}
						// }
						
						var color = buffers.Color[dataIndex];
						
						if (UseTemporalUpscaling) {
							var deltaR = color.R - historyPtr[colorIndex].R;
							if (deltaR < 0) deltaR = -deltaR;
							var deltaG = color.G - historyPtr[colorIndex].G;
							if (deltaG < 0) deltaG = -deltaG;
							var deltaB = color.B - historyPtr[colorIndex].B;
							if (deltaB < 0) deltaB = -deltaB;
							var deltaMax = (deltaR > deltaG ? deltaR : deltaG);
							if (deltaB > deltaMax) deltaMax = deltaB;
							
							for (var dy = 0; dy < 2; dy++) {
								for (var dx = 0; dx < 2; dx++) {
									var factor = (deltaMax * hitoryCoefs[dx | (dy << 1)] + 255) >> 8;
									var factorInv = 255 - factor;
									var i = (x ^ dx) + (y ^ dy) * sizeX;
									finalPtr[i].R = (byte)((color.R * factor + finalPtr[i].R * factorInv + 255) >> 8);
									finalPtr[i].G = (byte)((color.G * factor + finalPtr[i].G * factorInv + 255) >> 8);
									finalPtr[i].B = (byte)((color.B * factor + finalPtr[i].B * factorInv + 255) >> 8);
								}
							}
						}
						
						finalPtr[colorIndex] = color;
						historyPtr[colorIndex] = color;
					}
				}
			}
			
			FrameCount++;
			
			dataRef.Clear();
		}
		
		public Range2D ToTiles(Range2D range) {
			range.MinX = ((range.MinX < 0) ? 0 : range.MinX) >> TileShiftX;
			range.MinY = ((range.MinY < 0) ? 0 : range.MinY) >> TileShiftY;
			range.MaxX = ((range.MaxX >= sizeX) ? sizeX - 1 : range.MaxX) >> TileShiftX;
			range.MaxY = ((range.MaxY >= sizeY) ? sizeY - 1 : range.MaxY) >> TileShiftY;
			return range;
		}
		
		public void GetSamplingOffset(out float x, out float y, int frameOffset = 0) {
			GetSamplingOffset(out int ix, out int iy, frameOffset);
			x = (0.5f - ix) * 0.5f;
			y = (0.5f - iy) * 0.5f;
		}
		
		public void GetSamplingOffset(out int x, out int y, int frameOffset = 0) {
			x = 0; y = 0;
			
			if (!UseTemporalUpscaling) return;
			
			switch ((FrameCount + frameOffset) & 0b11) {
				case 0: x = 0; y = 0; return;
				case 1: x = 1; y = 1; return;
				case 2: x = 1; y = 0; return;
				case 3: x = 0; y = 1; return;
			}
		}
	}
}
