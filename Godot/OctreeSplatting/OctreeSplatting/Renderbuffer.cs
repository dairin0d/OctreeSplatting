// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
	public class Renderbuffer {
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
			public ulong* Stencil;
		}
		
		public const int TileShiftX = 3;
		public const int TileShiftY = 3;
		public const int TileSizeX = 1 << TileShiftX;
		public const int TileSizeY = 1 << TileShiftY;
		public const int TileArea = TileSizeX * TileSizeY;
		
		public const int DepthBits = 24;
		
		private int sizeX, sizeY, shiftX;
		private int tilesX, tilesY, tileShiftX;
		private byte[] data;
		private UnsafeRef dataRef;
		private Color32[] finalPixels;
		private InstanceInfo[] instanceInfos = new InstanceInfo[256];
		private uint instanceCount;
		private Color32 background;
		
		public int SizeX => sizeX;
		public int SizeY => sizeY;
		public int SizeZ => 1 << DepthBits;
		public Color32[] ColorPixels => finalPixels;
		public uint InstanceCount => instanceCount;
		
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
			
			for (shiftX = 0; (1 << shiftX) < sizeX; shiftX++);
			for (tileShiftX = 0; (1 << tileShiftX) < tilesX; tileShiftX++);
			
			var tileArea = (1 << tileShiftX) * tilesY;
			var pixelArea = (1 << shiftX) * sizeY;
			data = new byte[sizeof(ulong) * tileArea + 3 * sizeof(int) * pixelArea];
			
			finalPixels = new Color32[sizeX * sizeY];
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
					buffers.Stencil[tileIndex] = ulong.MaxValue;
				}
			}
			
			for (int y = 0; y < buffers.SizeY; y++) {
				int dataIndex = y << buffers.Shift;
				for (int x = 0; x < buffers.SizeX; x++, dataIndex++) {
					buffers.Depth[dataIndex] = buffers.SizeZ;
					buffers.Address[dataIndex] = uint.MaxValue;
					buffers.Instance[dataIndex] = uint.MaxValue;
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
		
		public unsafe Pointers GetBuffers() {
			var buffers = new Pointers {
				Shift = shiftX,
				SizeX = sizeX,
				SizeY = sizeY,
				SizeZ = 1 << DepthBits,
				TileShift = tileShiftX,
				TilesX = tilesX,
				TilesY = tilesY,
			};
			
			var dataPtr = (byte*)dataRef;
			var tileArea = (1 << tileShiftX) * tilesY;
			var pixelArea = (1 << shiftX) * sizeY;
			buffers.Stencil = (ulong*)dataPtr;
			dataPtr += sizeof(ulong) * tileArea;
			buffers.Depth = (int*)dataPtr;
			dataPtr += sizeof(int) * pixelArea;
			buffers.Address = (uint*)dataPtr;
			dataPtr += sizeof(uint) * pixelArea;
			buffers.Instance = (uint*)dataPtr;
			
			return buffers;
		}
		
		public unsafe void End() {
			if (data == null) return;
			
			var step = UseTemporalUpscaling ? 2 : 1;
			GetSamplingOffset(out int startX, out int startY);
			
			var buffers = GetBuffers();
			fixed (Color32* finalPtr = finalPixels)
			{
				for (int y = startY, yData = 0; y < sizeY; y += step, yData++) {
					int dataIndex = yData << shiftX;
					int colorIndex = startX + (y * sizeX);
					for (int x = startX; x < sizeX; x += step, colorIndex += step, dataIndex++) {
						var instance = buffers.Instance[dataIndex];
						if (instance < instanceCount) {
							var instanceInfo = instanceInfos[instance];
							var address = buffers.Address[dataIndex];
                            if (address < instanceInfo.Octree.Length) {
                                finalPtr[colorIndex].RGB = instanceInfo.Octree[address].Data;
                                finalPtr[colorIndex].A = 255;
                            } else {
                                finalPtr[colorIndex] = background;
                            }
						} else {
							finalPtr[colorIndex] = background;
						}
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
