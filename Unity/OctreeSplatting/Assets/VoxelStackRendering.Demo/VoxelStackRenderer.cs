// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VoxelStackRendering.Demo {
    public struct IterRange {
        public int Start, Stop, Step;
        public IterRange(int start, int stop, int step) {
            Start = start;
            Stop = stop;
            Step = step;
        }
    }
    
    public struct Vector3i {
        public int x, y, z;
    }
    
    public struct VoxelStackInfo {
        public int SpanStart;
        public ushort SpanCount;
        public ushort DataCount;
    }
    
    public class VoxelStackModel {
        public int SizeX;
        public int SizeY;
        public int SizeZ;
        public VoxelStackInfo[] Grid;
        public byte[] SpanData;
        
        public int ClampX(int x) {
            return Mathf.Clamp(x, 0, SizeX - 1);
        }
        public int ClampY(int y) {
            return Mathf.Clamp(y, 0, SizeY - 1);
        }
        public void ClampXY(ref int x, ref int y) {
            x = ClampX(x);
            y = ClampY(y);
        }
        
        public void FromHeightmap(Texture2D heightmap, Texture2D colormap = null) {
            FromHeightmap(heightmap.width, heightmap.height, heightmap.GetPixels32(), colormap?.GetPixels32());
        }
        
        public void FromHeightmap(int sizeX, int sizeY, Color32[] heightmap, Color32[] colormap = null) {
            if (colormap == null) colormap = heightmap;
            
            SizeX = sizeX;
            SizeY = sizeY;
            SizeZ = 255;
            
            Grid = new VoxelStackInfo[SizeX * SizeY];
            
            using (var stream = new MemoryStream()) {
                var writer = new BinaryWriter(stream);
                
                int pos = 0;
                
                for (int y = 0; y < SizeY; y++) {
                    for (int x = 0; x < SizeX; x++) {
                        int i = x + y * SizeX;
                        var h = CalcHeight(heightmap[i]);
                        var c = colormap[i];
                        
                        var hOther = Mathf.Min(Mathf.Min(CalcHeightXY(x-1, y), CalcHeightXY(x+1, y)),
                            Mathf.Min(CalcHeightXY(x, y-1), CalcHeightXY(x, y+1)));
                        
                        int hMin = Mathf.Min(hOther, h);
                        int hMax = h + 1;
                        int hSize = Mathf.Min(hMax - hMin, 255);
                        hMin = hMax - hSize;
                        
                        Grid[i].SpanStart = pos;
                        Grid[i].SpanCount = 3;
                        Grid[i].DataCount = (ushort)hSize;
                        
                        writer.Write((byte)hMin); writer.Write((byte)0); pos += 2;
                        writer.Write((byte)hSize); writer.Write((byte)255); pos += 2;
                        writer.Write((byte)(256-hMax)); writer.Write((byte)0); pos += 2;
                        
                        for (; hSize >= 0; hSize--) {
                            writer.Write(c.r);
                            writer.Write(c.g);
                            writer.Write(c.b);
                            pos += 3;
                        }
                    }
                }
                
                writer.Flush();
                SpanData = stream.ToArray();
            }
            
            int CalcHeight(Color32 hc) => (byte)((hc.r + hc.g + hc.b) / 3);
            int CalcHeightXY(int x, int y) {
                if ((x < 0) | (x >= SizeX) | (y < 0) | (y >= SizeY)) return 0;
                return CalcHeight(heightmap[x + y * SizeX]);
            }
        }
        
        public void FromOctree(string path, int maxLevel) {
            FromOctree(LoadOctree(path), maxLevel);
        }
        
        public void FromOctree(OctreeSplatting.OctreeNode[] octree, int maxLevel) {
            SizeX = SizeY = SizeZ = 1 << maxLevel;
            
            Grid = new VoxelStackInfo[SizeX * SizeY];
            
            var spansGrid = new List<int>[SizeX * SizeY];
            var colorsGrid = new List<OctreeSplatting.Color24>[SizeX * SizeY];
            
            for (int y = 0; y < SizeY; y++) {
                for (int x = 0; x < SizeX; x++) {
                    int i = x + y * SizeX;
                    spansGrid[i] = new List<int>();
                    colorsGrid[i] = new List<OctreeSplatting.Color24>();
                }
            }
            
            Traverse(0, 0, 0, 0, 0);
            
            using (var stream = new MemoryStream()) {
                var writer = new BinaryWriter(stream);
                
                int pos = 0;
                
                for (int y = 0; y < SizeY; y++) {
                    for (int x = 0; x < SizeX; x++) {
                        int i = x + y * SizeX;
                        
                        Grid[i].SpanStart = pos;
                        Grid[i].DataCount = 0;
                        Grid[i].SpanCount = 0;
                        
                        foreach (var (size, alpha) in EnumerateSpans(spansGrid[i])) {
                            writer.Write((byte)size); writer.Write((byte)alpha); pos += 2;
                            Grid[i].SpanCount++;
                        }
                        
                        foreach (var color in colorsGrid[i]) {
                            writer.Write((byte)color.R);
                            writer.Write((byte)color.G);
                            writer.Write((byte)color.B);
                            pos += 3;
                            Grid[i].DataCount++;
                        }
                    }
                }
                
                writer.Flush();
                SpanData = stream.ToArray();
            }
            
            IEnumerable<(byte size, byte alpha)> EnumerateSpans(List<int> spansList) {
                if (spansList.Count == 0) yield break;
                
                if (spansList[spansList.Count-1] < (SizeY-1)) {
                    spansList.Add(SizeY-1);
                }
                
                bool isEmpty = spansList[0] >= 0;
                for (int j = (isEmpty ? 0 : 1); j < spansList.Count; j++, isEmpty = !isEmpty) {
                    int spanSize = (j == 0 ? spansList[j] + 1 : spansList[j] - spansList[j - 1]);
                    byte alpha = (byte)(isEmpty ? 0 : 255);
                    
                    for (int h = 0; h < spanSize; h += 255) {
                        int hTop = h + 255;
                        if (hTop > spanSize) hTop = spanSize;
                        byte size = (byte)(hTop - h);
                        yield return (size, alpha);
                    }
                }
            }
            
            void Traverse(uint address, int level, int x, int y, int z) {
                ref var node = ref octree[address];
                
                if ((node.Mask == 0) | (level >= maxLevel)) {
                    int nodeSize = 1 << (maxLevel - level);
                    for (int nz = 0; nz < nodeSize; nz++) {
                        for (int ny = 0; ny < nodeSize; ny++) {
                            for (int nx = 0; nx < nodeSize; nx++) {
                                Insert(x+nx, y+ny, z+nz, node.Data);
                            }
                        }
                    }
                    return;
                }
                
                int sublevel = level + 1;
                int shift = maxLevel - sublevel;
                int octant = 0;
                for (int oz = 0; oz < 2; oz++) {
                    int subz = z + (oz << shift);
                    for (int oy = 0; oy < 2; oy++) {
                        int suby = y + (oy << shift);
                        for (int ox = 0; ox < 2; ox++) {
                            int subx = x + (ox << shift);
                            if ((node.Mask & (1 << octant)) != 0) {
                                Traverse(node.Address + (uint)octant, sublevel, subx, suby, subz);
                            }
                            octant++;
                        }
                    }
                }
            }
            
            void Insert(int x, int y, int z, OctreeSplatting.Color24 color) {
                int i = x + z * SizeX;
                var spansList = spansGrid[i];
                var colorsList = colorsGrid[i];
                
                if (spansList.Count == 0) {
                    spansList.Add(y - 1);
                    spansList.Add(y);
                } else {
                    int yPrev = spansList[spansList.Count - 1];
                    if ((y - yPrev) == 1) {
                        spansList[spansList.Count - 1] = y;
                    } else {
                        spansList.Add(y - 1);
                        spansList.Add(y);
                    }
                }
                
                colorsList.Add(color);
            }
        }
        
        private OctreeSplatting.OctreeNode[] LoadOctree(string path) {
            var asset = Resources.Load<TextAsset>(path);
            return asset ? FromBytes<OctreeSplatting.OctreeNode>(asset.bytes) : null;
        }
        
        private static T[] FromBytes<T>(byte[] bytes) where T : struct {
            if (bytes == null) return null;
            var result = new T[bytes.Length / Marshal.SizeOf(typeof(T))];
            var handle = GCHandle.Alloc(result, GCHandleType.Pinned);
            try {
                Marshal.Copy(bytes, 0, handle.AddrOfPinnedObject(), bytes.Length);
            } finally {
                if (handle.IsAllocated) handle.Free();
            }
            return result;
        }
    }
    
    public class VoxelStackRenderer : MonoBehaviour {
        private struct PixelData {
            public float R, G, B, Weight;
            public float Transparency;
            public int Skip;
        }
        
        private struct ShadowData {
            public int Opacity;
            public int Distance;
        }
        
        private struct ColumnInfo {
            public int MinX, MaxX, SpanY, Radius;
        }
        
        private struct SectorInfo {
            public IterRange X, Y; // tile's range
            public int OriginX, OriginY;
        }
        
        public Texture2D HeightmapTexture;
        public Texture2D ColormapTexture;
        
        public string OctreePath;
        public int OctreeMaxLevel = -1;
        
        public Color32 BackgroundColor = Color.black;
        
        public int PrecisionBits = 8;
        
        public float HeightmapScale = 1;
        
        public int AngularResolution = 256;
        public int VisibleRadius = 256;
        public float VisibleRadiusFade = 1;
        
        public Vector3 ObservationPoint;
        
        public float MovementSpeed = 1;
        
        public bool UseLineOfSight = false;
        public bool ShowViewpoint = true;
        
        public bool Downsample;
        
        public int ZoomSteps;
        
        private VoxelStackModel model;
        
        private Texture2D colorTex;
        private Color32[] colorBuf;
        private PixelData[] dataBuf;
        
        private ColumnInfo[] columnInfos;
        private int[] radiusInfos;
        private int[] radiusSpanInfos;
        private ShadowData[] sectorShadows;
        private SectorInfo[] sectorInfos = new SectorInfo[4];
        
        private int savedAngularResolution = -1;
        private int savedVisibleRadius = -1;
        private float savedVisibleRadiusFade = -1;
        
        private Camera cam;
        private Material material;
        
        private Vector3 lastMousePosition;
        
        private int screenW;
        private int screenH;
        
        private float cameraYaw;
        private float cameraPitch;
        private float zoomFactor = 0.125f;
        
        private float ZoomScale => (float)Mathf.Pow(2, ZoomSteps * zoomFactor);
        
        private System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        
        private void Start() {
            if (!Application.isEditor) Screen.SetResolution(640, 480, false);
            
            CalculateInfos(AngularResolution, VisibleRadius, VisibleRadiusFade);
            
            CreateModel();
            
            cam = GetComponent<Camera>();
            
            material = new Material(Shader.Find("UI/Default"));
            
            lastMousePosition = Input.mousePosition;
        }

        private void Update() {
            bool shouldRecalculate = ((AngularResolution != savedAngularResolution) |
                                      (VisibleRadius != savedVisibleRadius) |
                                      (VisibleRadiusFade != savedVisibleRadiusFade));
            if (shouldRecalculate) {
                CalculateInfos(AngularResolution, VisibleRadius, VisibleRadiusFade);
            }
            
            ProcessInput();
            
            screenW = cam.pixelWidth;
            screenH = cam.pixelHeight;
            
            ResizePixels(ref dataBuf, screenW, screenH);
            ResizePixels(ref colorBuf, screenW, screenH);
            ResizeTexture(ref colorTex, screenW, screenH);
            
            stopwatch.Restart();
            RenderFrame();
            stopwatch.Stop();
            
            colorTex.SetPixelData(colorBuf, 0);
            colorTex.Apply(false);
        }
        
        private void OnPostRender() {
            material.mainTexture = colorTex;
            material.SetPass(0);

            GL.PushMatrix();
            GL.LoadOrtho();

            GL.Begin(GL.QUADS);
            GL.TexCoord2(0, 0);
            GL.Vertex3(0, 0, 0);
            GL.TexCoord2(1, 0);
            GL.Vertex3(1, 0, 0);
            GL.TexCoord2(1, 1);
            GL.Vertex3(1, 1, 0);
            GL.TexCoord2(0, 1);
            GL.Vertex3(0, 1, 0);
            GL.End();

            GL.PopMatrix();
        }
        
        private void OnGUI() {
            int x = 0, y = 0, panelWidth = 250, lineHeight = 20;
            
            int camW = cam.pixelWidth, camH = cam.pixelHeight;
            DrawBox(new Rect(0, 0, panelWidth, lineHeight*2));
            GUI.Label(new Rect(x, y, panelWidth, lineHeight), $"{camW}x{camH}: {stopwatch.ElapsedMilliseconds}");
            y += lineHeight;
        }
        
        private static void DrawBox(Rect rect, int repeats = 2) {
            if (rect.width < 0) {
                rect.width = -rect.width;
                rect.x -= rect.width;
            }
            if (rect.height < 0) {
                rect.height = -rect.height;
                rect.y -= rect.height;
            }
            
            for (; repeats > 0; repeats--) {
                GUI.Box(rect, "");
            }
        }
        
        private void CreateModel() {
            model = new VoxelStackModel();
            if (string.IsNullOrEmpty(OctreePath) || (OctreeMaxLevel < 0)) {
                model.FromHeightmap(HeightmapTexture, ColormapTexture);
            } else {
                model.FromOctree(OctreePath, OctreeMaxLevel);
            }
        }
        
        private void ProcessInput() {
            var mousePosition = Input.mousePosition;
            var mouseDelta = mousePosition - lastMousePosition;
            lastMousePosition = mousePosition;
            
            if (Input.GetKeyDown(KeyCode.Space)) {
                UseLineOfSight = !UseLineOfSight;
            }
            if (Input.GetKeyDown(KeyCode.F12)) {
                ShowViewpoint = !ShowViewpoint;
            }
            
            var movement = new Vector3(
                IntKeyDown(KeyCode.A) - IntKeyDown(KeyCode.D),
                IntKeyDown(KeyCode.R) - IntKeyDown(KeyCode.F),
                IntKeyDown(KeyCode.W) - IntKeyDown(KeyCode.S)
            );
            
            if (Input.GetKey(KeyCode.LeftControl)) {
                movement *= 10f;
            } else if (Input.GetKey(KeyCode.LeftShift)) {
                movement *= 0.05f;
            }
            
            var camRotMat = Matrix4x4.Rotate(Quaternion.Euler(0, cameraYaw, 0));
            movement = camRotMat.MultiplyVector(movement);
            movement.x = -movement.x;
            (movement.y, movement.z) = (movement.z, movement.y);
            
            ObservationPoint += movement * MovementSpeed * Time.deltaTime;
            
            if (Input.GetMouseButton(1)) {
                const float sensitivity = 0.2f;
                cameraYaw -= mouseDelta.x * sensitivity;
                cameraPitch -= -mouseDelta.y * sensitivity;
            }
            
            ZoomSteps += (int)Input.mouseScrollDelta.y;
            
            int IntKeyDown(KeyCode keyCode) => Input.GetKey(keyCode) ? 1 : 0;
            // int IntKeyPressed(KeyCode keyCode) => Input.GetKeyDown(keyCode) ? 1 : 0;
        }
        
        private void RenderFrame() {
            Clear();
            
            var matrix = Matrix4x4.Translate(new Vector3(screenW, screenH) * 0.5f) *
                Matrix4x4.Rotate(Quaternion.Euler(90, 0, 0)) *
                Matrix4x4.Rotate(Quaternion.Euler(cameraPitch, cameraYaw, 0)) *
                Matrix4x4.Scale(Vector3.one * ZoomScale);
            
            if (UseLineOfSight) {
                DrawVis(matrix);
            } else {
                DrawSkip(matrix);
            }
            
            if (ShowViewpoint) {
                for (int dx = -1; dx <= 1; dx++) {
                    for (int dy = -1; dy <= 1; dy++) {
                        ref var pixel = ref dataBuf[(screenH/2 + dy) + (screenW/2 + dx) * screenH];
                        pixel.R = 0;
                        pixel.G = 255;
                        pixel.B = 0;
                        pixel.Weight = 1;
                        pixel.Transparency = 0;
                    }
                }
            }
            
            Blit();
        }
        
        private unsafe void Clear() {
            int bufSize = screenW * screenH;
            
            fixed (PixelData* dataPtr = dataBuf)
            {
                for (int y = 0, iY = 0; y < screenH; y++, iY++) {
                    for (int x = 0, i = iY; x < screenW; x++, i += screenH) {
                        dataPtr[i].Skip = y;
                        dataPtr[i].R = 0;
                        dataPtr[i].G = 0;
                        dataPtr[i].B = 0;
                        dataPtr[i].Weight = 0;
                        dataPtr[i].Transparency = 1f;
                    }
                }
            }
        }
        
        private unsafe void Blit() {
            // dataBuf is Y-major, colorBuf is X-major
            fixed (PixelData* dataPtr = dataBuf)
            fixed (Color32* colorPtr = colorBuf)
            {
                for (int y = 0, iY = 0, jY = 0; y < screenH; y++, iY++, jY += screenW) {
                    for (int x = 0, i = iY, j = jY; x < screenW; x++, i += screenH, j++) {
                        if (dataPtr[i].Weight > 0) {
                            float weight = (1f - dataPtr[i].Transparency) / dataPtr[i].Weight;
                            float weightBkg = dataPtr[i].Transparency;
                            colorPtr[j].r = (byte)(dataPtr[i].R * weight + BackgroundColor.r * weightBkg);
                            colorPtr[j].g = (byte)(dataPtr[i].G * weight + BackgroundColor.g * weightBkg);
                            colorPtr[j].b = (byte)(dataPtr[i].B * weight + BackgroundColor.b * weightBkg);
                            colorPtr[j].a = 255;
                        } else {
                            colorPtr[j] = BackgroundColor;
                            colorPtr[j].a = 255;
                        }
                    }
                }
                
                if (Downsample) {
                    int xmax = screenW & ~1, ymax = screenH & ~1;
                    for (int y = 0; y < ymax; y += 2) {
                        for (int x = 0; x < xmax; x += 2) {
                            int i00 = x + y * screenW;
                            ref var pix00 = ref colorPtr[i00];
                            ref var pix10 = ref colorPtr[i00 + 1];
                            ref var pix01 = ref colorPtr[i00 + screenW];
                            ref var pix11 = ref colorPtr[i00 + screenW + 1];
                            pix00.r = (byte)((pix00.r + pix10.r + pix01.r + pix11.r) >> 2);
                            pix00.g = (byte)((pix00.g + pix10.g + pix01.g + pix11.g) >> 2);
                            pix00.b = (byte)((pix00.b + pix10.b + pix01.b + pix11.b) >> 2);
                            pix10 = pix01 = pix11 = pix00;
                        }
                    }
                }
            }
        }
        
        private unsafe void DrawSkip(Matrix4x4 matrix) {
            int subpixelSize = 1 << PrecisionBits;
            
            matrix.m03 -= (matrix.m00 * ObservationPoint.x + matrix.m10 * ObservationPoint.y + matrix.m20 * ObservationPoint.z);
            matrix.m13 -= (matrix.m01 * ObservationPoint.x + matrix.m11 * ObservationPoint.y + matrix.m21 * ObservationPoint.z);
            matrix.m23 -= (matrix.m02 * ObservationPoint.x + matrix.m12 * ObservationPoint.y + matrix.m22 * ObservationPoint.z);
            
            matrix.m20 *= HeightmapScale;
            matrix.m21 *= HeightmapScale;
            matrix.m22 *= HeightmapScale;
            
            float depthScale = 1 << 16;
            
            Vector3i X, Y, Z, T;
            X.x = (int)(matrix.m00 * subpixelSize);
            X.y = (int)(matrix.m01 * subpixelSize);
            X.z = (int)(matrix.m02 * depthScale);
            Y.x = (int)(matrix.m10 * subpixelSize);
            Y.y = (int)(matrix.m11 * subpixelSize);
            Y.z = (int)(matrix.m12 * depthScale);
            Z.x = (int)(matrix.m20 * subpixelSize);
            Z.y = (int)(matrix.m21 * subpixelSize);
            Z.z = (int)(matrix.m22 * depthScale);
            T.x = (int)(matrix.m03 * subpixelSize);
            T.y = (int)(matrix.m13 * subpixelSize);
            T.z = (int)(matrix.m23 * depthScale);
            
            int yDataStep = (int)(subpixelSize / Mathf.Max(Mathf.Abs(matrix.m21), 1e-6f));
            
            int sizeX = model.SizeX;
            int sizeY = model.SizeY;
            int sizeZ = model.SizeZ;
            
            var grid = model.Grid;
            var spanData = model.SpanData;
            
            int my0 = 0, my1 = sizeY, dmy = 1;
            int mx0 = 0, mx1 = sizeX, dmx = 1;
            
            fixed (VoxelStackInfo* gridPtr = grid)
            fixed (byte* spanDataPtr = spanData)
            fixed (PixelData* dataBufPtr = dataBuf)
            {
                int iGY0 = 0, diGY = sizeX;
                int iGX0 = 0, diGX = 1;
                
                if (X.z > 0) {
                    mx0 = sizeX - 1; mx1 = -1; dmx = -1;
                    iGX0 = sizeX - 1; diGX = -diGX;
                    T.x += (sizeX - 1) * X.x;
                    T.y += (sizeX - 1) * X.y;
                    T.z += (sizeX - 1) * X.z;
                    X.x = -X.x;
                    X.y = -X.y;
                    X.z = -X.z;
                }
                
                if (Y.z > 0) {
                    my0 = sizeY - 1; my1 = -1; dmy = -1;
                    iGY0 = (sizeY - 1) * sizeX; diGY = -diGY;
                    T.x += (sizeY - 1) * Y.x;
                    T.y += (sizeY - 1) * Y.y;
                    T.z += (sizeY - 1) * Y.z;
                    Y.x = -Y.x;
                    Y.y = -Y.y;
                    Y.z = -Y.z;
                }
                
                int yMax = screenH - 1;
                int ZyHalf = Z.y / 2;
                Z.y = ZyHalf * 2;
                
                var pY = T;
                
                for (int my = my0, iGY = iGY0; my != my1; my += dmy, iGY += diGY) {
                    var pXY = pY;
                    pY.x += Y.x;
                    pY.y += Y.y;
                    pY.z += Y.z;
                    
                    for (int mx = mx0, iG = iGY + iGX0; mx != mx1; mx += dmx, iG += diGX) {
                        var pXYZ = pXY;
                        pXY.x += X.x;
                        pXY.y += X.y;
                        pXY.z += X.z;
                        
                        ref var gridCell = ref gridPtr[iG];
                        if (gridCell.SpanCount == 0) continue;
                        
                        int screenX = pXYZ.x >> PrecisionBits;
                        if ((screenX < 0) | (screenX >= screenW)) continue;
                        
                        const int SpanStep = 2;
                        const int DataStep = 3;
                        
                        int iSpan0 = gridCell.SpanStart;
                        int nSpans = gridCell.SpanCount;
                        int iData0 = iSpan0 + nSpans * SpanStep;
                        int nDatas = gridCell.DataCount;
                        
                        int iScreenX = screenX * screenH;
                        
                        int iSpanEnd = iData0;
                        int Zy = Z.y, Zz = Z.z;
                        
                        int diSpanStep = SpanStep;
                        int diDataStep = DataStep;
                        
                        if (Z.z >= 0) {
                            diSpanStep = -SpanStep;
                            diDataStep = -DataStep;
                            iSpanEnd = iSpan0 - SpanStep;
                            iSpan0 = iData0 - SpanStep;
                            iData0 = iData0 + (nDatas - 1) * DataStep;
                            pXYZ.y += (sizeY - 1) * Z.y;
                            pXYZ.z += (sizeY - 1) * Z.z;
                            Zy = -Zy;
                            Zz = -Zz;
                        }
                        
                        for (int iSpan = iSpan0, iData = iData0; iSpan != iSpanEnd; iSpan += diSpanStep) {
                            int spanSize = spanDataPtr[iSpan];
                            int spanAlpha = spanDataPtr[iSpan+1];
                            
                            if (spanAlpha == 0) {
                                pXYZ.y += spanSize * Zy;
                                pXYZ.z += spanSize * Zz;
                                continue;
                            }
                            
                            var iData2 = iData;
                            iData += diDataStep * spanSize;
                            
                            int y0 = (pXYZ.y + ZyHalf) >> PrecisionBits;
                            pXYZ.y += spanSize * Zy;
                            pXYZ.z += spanSize * Zz;
                            int y1 = (pXYZ.y - ZyHalf) >> PrecisionBits;
                            
                            int yStart = y0;
                            int yLen = (y1 >= y0 ? y1 - y0 + 1 : y1 - y0 - 1);
                            float yScale = spanSize / (float)yLen;
                            
                            if (y1 < y0) {
                                (y1, y0) = (y0, y1);
                            }
                            
                            if (y0 < 0) y0 = 0;
                            if (y1 > yMax) y1 = yMax;
                            
                            int y = y0;
                            int yEnd = y1 + 1;
                            while (y <= y1) {
                                var pixel = &dataBufPtr[y + iScreenX];
                                if (pixel->Skip > y1) break;
                                if (pixel->Skip > y) {
                                    y = pixel->Skip;
                                    pixel->Skip = yEnd;
                                    continue;
                                }
                                pixel->Skip = yEnd;
                                int diData = (int)((y - yStart) * yScale);
                                var spanColor = &spanDataPtr[iData2 + diData*diDataStep];
                                pixel->R = spanColor[0];
                                pixel->G = spanColor[1];
                                pixel->B = spanColor[2];
                                pixel->Weight = 1;
                                pixel->Transparency = 0;
                                y++;
                            }
                        }
                    }
                }
            }
        }
        
        private unsafe void DrawVis(Matrix4x4 matrix) {
            int subpixelSize = 1 << PrecisionBits;
            
            matrix.m03 -= (matrix.m00 * ObservationPoint.x + matrix.m10 * ObservationPoint.y + matrix.m20 * ObservationPoint.z);
            matrix.m13 -= (matrix.m01 * ObservationPoint.x + matrix.m11 * ObservationPoint.y + matrix.m21 * ObservationPoint.z);
            matrix.m23 -= (matrix.m02 * ObservationPoint.x + matrix.m12 * ObservationPoint.y + matrix.m22 * ObservationPoint.z);
            
            matrix.m20 *= HeightmapScale;
            matrix.m21 *= HeightmapScale;
            matrix.m22 *= HeightmapScale;
            
            float depthScale = 1 << 16;
            
            Vector3i X, Y, Z, T;
            X.x = (int)(matrix.m00 * subpixelSize);
            X.y = (int)(matrix.m01 * subpixelSize);
            X.z = (int)(matrix.m02 * depthScale);
            Y.x = (int)(matrix.m10 * subpixelSize);
            Y.y = (int)(matrix.m11 * subpixelSize);
            Y.z = (int)(matrix.m12 * depthScale);
            Z.x = (int)(matrix.m20 * subpixelSize);
            Z.y = (int)(matrix.m21 * subpixelSize);
            Z.z = (int)(matrix.m22 * depthScale);
            T.x = (int)(matrix.m03 * subpixelSize);
            T.y = (int)(matrix.m13 * subpixelSize);
            T.z = (int)(matrix.m23 * depthScale);
            
            int yDataStep = (int)(subpixelSize / Mathf.Max(Mathf.Abs(matrix.m21), 1e-6f));
            
            int yMax = screenH - 1;
            int ZyHalf = Z.y / 2;
            Z.y = ZyHalf * 2;
            
            int sizeX = model.SizeX;
            int sizeY = model.SizeY;
            int sizeZ = model.SizeZ;
            
            var grid = model.Grid;
            var spanData = model.SpanData;
            
            int obsX = (int)ObservationPoint.x;
            int obsY = (int)ObservationPoint.y;
            int obsZ = (int)ObservationPoint.z;
            int obsMinX = obsX - VisibleRadius;
            int obsMinY = obsY - VisibleRadius;
            int obsMaxX = obsX + VisibleRadius - 1;
            int obsMaxY = obsY + VisibleRadius - 1;
            
            var sectorPP = new SectorInfo() {
                X = new IterRange(model.ClampX(obsX), model.ClampX(obsMaxX)+1, 1),
                Y = new IterRange(model.ClampY(obsY), model.ClampY(obsMaxY)+1, 1),
                OriginX = obsX, OriginY = obsY,
            };
            var sectorNP = new SectorInfo() {
                X = new IterRange(model.ClampX(obsX)-1, model.ClampX(obsMinX)-1, -1),
                Y = new IterRange(model.ClampY(obsY), model.ClampY(obsMaxY)+1, 1),
                OriginX = obsX-1, OriginY = obsY,
            };
            var sectorPN = new SectorInfo() {
                X = new IterRange(model.ClampX(obsX), model.ClampX(obsMaxX)+1, 1),
                Y = new IterRange(model.ClampY(obsY)-1, model.ClampY(obsMinY)-1, -1),
                OriginX = obsX, OriginY = obsY-1,
            };
            var sectorNN = new SectorInfo() {
                X = new IterRange(model.ClampX(obsX)-1, model.ClampX(obsMinX)-1, -1),
                Y = new IterRange(model.ClampY(obsY)-1, model.ClampY(obsMinY)-1, -1),
                OriginX = obsX-1, OriginY = obsY-1,
            };
            sectorInfos[0] = sectorPP;
            sectorInfos[1] = sectorNP;
            sectorInfos[2] = sectorPN;
            sectorInfos[3] = sectorNN;
            
            fixed (VoxelStackInfo* gridPtr = grid)
            fixed (byte* spanDataPtr = spanData)
            fixed (PixelData* dataBufPtr = dataBuf)
            fixed (int* radiusInfosPtr = radiusInfos)
            fixed (ColumnInfo* columnInfosPtr = columnInfos)
            fixed (ShadowData* sectorShadowsPtr = sectorShadows)
            {
                var sectorRenderer = new SectorRenderer() {
                    screenW = screenW,
                    screenH = screenH,
                    PrecisionBits = PrecisionBits,
                    X = X,
                    Y = Y,
                    Z = Z,
                    T = T,
                    sizeX = sizeX,
                    sizeY = sizeY,
                    yDataStep = yDataStep,
                    yMax = yMax,
                    ZyHalf = ZyHalf,
                    
                    radius = VisibleRadius,
                    obs = new Vector3i{x = obsX, y = obsY, z = obsZ},
                    
                    angularResolutionX = AngularResolution,
                    angularResolutionY = AngularResolution * 2,
                    
                    gridPtr = gridPtr,
                    spanDataPtr = spanDataPtr,
                    dataBufPtr = dataBufPtr,
                    radiusInfosPtr = radiusInfosPtr,
                    columnInfosPtr = columnInfosPtr,
                    sectorShadowsPtr = sectorShadowsPtr,
                };
                
                for (int sectorI = 0; sectorI < 4; sectorI++) {
                    System.Array.Clear(sectorShadows, 0, sectorShadows.Length);
                    sectorRenderer.sectorInfo = sectorInfos[sectorI];
                    sectorRenderer.DrawSector();
                }
            }
        }
        
        private unsafe struct SectorRenderer {
            public int screenW, screenH;
            public int PrecisionBits;
            
            public Vector3i X, Y, Z, T;
            public int sizeX, sizeY;
            public int yDataStep;
            public int yMax;
            public int ZyHalf;
            
            public int radius;
            public Vector3i obs;
            public SectorInfo sectorInfo;
            
            public int angularResolutionX;
            public int angularResolutionY;
            
            public VoxelStackInfo* gridPtr;
            public byte* spanDataPtr;
            public PixelData* dataBufPtr;
            public int* radiusInfosPtr;
            public ColumnInfo* columnInfosPtr;
            public ShadowData* sectorShadowsPtr;
            
            public unsafe void DrawSector()
            {
                var rangeX = sectorInfo.X;
                var rangeY = sectorInfo.Y;
                
                int sectorW = (rangeX.Stop - rangeX.Start) * rangeX.Step;
                int sectorH = (rangeY.Stop - rangeY.Start) * rangeY.Step;
                if ((sectorW <= 0) | (sectorH <= 0)) return;
                
                var sectorX0 = (rangeX.Start - sectorInfo.OriginX) * rangeX.Step;
                if ((sectorX0 < 0) | (sectorX0 >= radius)) return;
                var sectorY0 = (rangeY.Start - sectorInfo.OriginY) * rangeY.Step;
                if ((sectorY0 < 0) | (sectorY0 >= radius)) return;
                
                var shadowTraceBuf = stackalloc int[angularResolutionY];
                
                int radiusColumnSize = radius * 2;
                
                var origin = T;
                origin.x += X.x * rangeX.Start + Y.x * rangeY.Start;
                origin.y += X.y * rangeX.Start + Y.y * rangeY.Start;
                origin.z += X.z * rangeX.Start + Y.z * rangeY.Start;
                
                var dX = X;
                dX.x *= rangeX.Step;
                dX.y *= rangeX.Step;
                dX.z *= rangeX.Step;
                
                var dY = Y;
                dY.x *= rangeY.Step;
                dY.y *= rangeY.Step;
                dY.z *= rangeY.Step;
                
                int yTop = yMax << PrecisionBits;
                
                var pY = origin;
                
                for (int tileY = rangeY.Start, secY = sectorY0; tileY != rangeY.Stop; tileY += rangeY.Step, secY++) {
                    var pXY = pY;
                    pY.x += dY.x;
                    pY.y += dY.y;
                    pY.z += dY.z;
                    
                    for (int tileX = rangeX.Start, secX = sectorX0; tileX != rangeX.Stop; tileX += rangeX.Step, secX++) {
                        var pXYZ = pXY;
                        pXY.x += dX.x;
                        pXY.y += dX.y;
                        pXY.z += dX.z;
                        
                        ref var gridCell = ref gridPtr[tileX + tileY * sizeX];
                        if (gridCell.SpanCount == 0) continue;
                        
                        int screenX = pXYZ.x >> PrecisionBits;
                        if ((screenX < 0) | (screenX >= screenW)) continue;
                        
                        ref var columnInfo = ref columnInfosPtr[secX + secY * radius];
                        if (columnInfo.SpanY == 0) break;
                        
                        int hSpanOfs = radius - obs.z;
                        int hSpanMin = obs.z - columnInfo.SpanY;
                        int hSpanMax = obs.z + columnInfo.SpanY - 1;
                        int ySpanMin = (pXYZ.y + hSpanMin * Z.y + ZyHalf) >> PrecisionBits;
                        int ySpanMax = (pXYZ.y + hSpanMax * Z.y + ZyHalf) >> PrecisionBits;
                        
                        if ((ySpanMin > yTop) | (ySpanMax < 0)) continue;
                        
                        if (ySpanMin != ySpanMax) {
                            float hyScale = 1f / Z.y;
                            int hyMin = (int)((0 - pXYZ.y) * hyScale);
                            int hyMax = (int)((yTop - pXYZ.y) * hyScale);
                            
                            if (ySpanMin < ySpanMax) {
                                if (hSpanMin < hyMin) hSpanMin = hyMin;
                                if (hSpanMax > hyMax) hSpanMax = hyMax;
                            } else {
                                if (hSpanMax < hyMin) hSpanMax = hyMin;
                                if (hSpanMin > hyMax) hSpanMin = hyMax;
                            }
                        }
                        
                        int shadowX = (columnInfo.MinX + columnInfo.MaxX) >> 1;
                        var shadowLine = sectorShadowsPtr + shadowX * angularResolutionY;
                        
                        var radiusInfoColumn = radiusInfosPtr + columnInfo.Radius * radiusColumnSize;
                        
                        const int SpanStep = 2;
                        const int DataStep = 3;
                        
                        int iSpan0 = gridCell.SpanStart;
                        int nSpans = gridCell.SpanCount;
                        int iData0 = iSpan0 + nSpans * SpanStep;
                        int nDatas = gridCell.DataCount;
                        
                        int iScreenX = screenX * screenH;
                        
                        int shadowTraceFront = 0;
                        
                        for (int iSpan = iSpan0, iData = iData0, h0 = 0, h1 = 0; iSpan < iData0; iSpan += SpanStep, h0 = h1) {
                            int spanSize = spanDataPtr[iSpan];
                            int spanAlpha = spanDataPtr[iSpan+1];
                            
                            h1 = h0 + spanSize;
                            
                            if (spanAlpha == 0) continue;
                            
                            var iData2 = iData;
                            iData += DataStep * spanSize;
                            
                            int h0vis = h0;
                            if (h0vis < hSpanMin) h0vis = hSpanMin;
                            int h1vis = h1;
                            if (h1vis > hSpanMax) h1vis = hSpanMax;
                            
                            if (h0vis > h1vis) continue;
                            
                            int spanMin = h0vis - h0;
                            int spanMax = h1vis - h0;
                            
                            int colMin = h0vis + hSpanOfs;
                            int colMax = h1vis + hSpanOfs;
                            
                            int y = pXYZ.y + h0vis * Z.y + ZyHalf;
                            
                            var alphaScale = spanAlpha / (255f * 255f);
                            
                            for (int spanI = iData2 + spanMin * DataStep, colI = colMin; colI < colMax; spanI += DataStep, colI++, y += Z.y) {
                                var radiusInfo = radiusInfoColumn[colI];
                                
                                int shadowY = radiusInfo >> 8;
                                
                                bool isInShadow = (shadowLine[shadowY].Opacity != 0);
                                isInShadow &= (columnInfo.Radius - shadowLine[shadowY].Distance > 4);
                                
                                if (isInShadow) continue;
                                
                                var voxelAlpha = (radiusInfo & 255) * alphaScale;
                                
                                var spanColor = &spanDataPtr[spanI];
                                var pixel = &dataBufPtr[(y >> PrecisionBits) + iScreenX];
                                pixel->R += spanColor[0] * voxelAlpha;
                                pixel->G += spanColor[1] * voxelAlpha;
                                pixel->B += spanColor[2] * voxelAlpha;
                                pixel->Weight += voxelAlpha;
                                pixel->Transparency *= 1f - voxelAlpha;
                            }
                            
                            if (colMax > colMin) {
                                int shadowYMin = radiusInfoColumn[colMin] >> 8;
                                if (colMin > 0) shadowYMin = (shadowYMin + (radiusInfoColumn[colMin-1] >> 8)) >> 1;
                                
                                int shadowYMax = radiusInfoColumn[colMax] >> 8;
                                if (colMax < radiusColumnSize-1) shadowYMax = (shadowYMax + (radiusInfoColumn[colMax+1] >> 8)) >> 1;
                                
                                shadowTraceBuf[shadowTraceFront++] = shadowYMin;
                                shadowTraceBuf[shadowTraceFront++] = shadowYMax;
                                shadowTraceBuf[shadowTraceFront++] = spanAlpha;
                            }
                        }
                        
                        int shadowXMinI = columnInfo.MinX * angularResolutionY;
                        int shadowXMaxI = columnInfo.MaxX * angularResolutionY;
                        for (int traceI = 0; traceI < shadowTraceFront; traceI += 3) {
                            int shadowYMin = shadowTraceBuf[traceI+0];
                            int shadowYMax = shadowTraceBuf[traceI+1];
                            int spanAlpha = shadowTraceBuf[traceI+2];
                            for (int ix = shadowXMinI; ix < shadowXMaxI; ix += angularResolutionY) {
                                var scanline = sectorShadowsPtr + ix;
                                for (int y = shadowYMin; y < shadowYMax; y++) {
                                    scanline[y].Opacity += spanAlpha;
                                    if (scanline[y].Distance == 0) {
                                        scanline[y].Distance = columnInfo.Radius;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        
        private void CalculateInfos(int angularResolution, int radius, float fade) {
            savedAngularResolution = angularResolution;
            savedVisibleRadius = radius;
            savedVisibleRadiusFade = fade;
            
            fade = Mathf.Max(fade, 0.001f);
            
            int angularResolutionH = angularResolution * 2;
            
            int diameter = radius * 2;
            radiusSpanInfos = new int[radius];
            radiusInfos = new int[radius * diameter];
            columnInfos = new ColumnInfo[radius * radius];
            sectorShadows = new ShadowData[angularResolution * angularResolutionH];
            
            float radiusSquared = radius * radius;
            for (int ir = 0; ir < radius; ir++) {
                float r = ir + 0.5f;
                float h = Mathf.Sqrt(radiusSquared - r * r);
                radiusSpanInfos[ir] = Mathf.RoundToInt(h);
                
                int iStart = ir * diameter;
                for (int ih = 0; ih < diameter; ih++) {
                    h = (ih - radius) + 0.5f;
                    float distance = Mathf.Sqrt(r * r + h * h);
                    float distanceFactor = Mathf.Clamp01((radius - distance) / fade);
                    float angle = Mathf.Atan2(h, r);
                    float angleFactor = (1f + (angle / Mathf.PI)) * 0.5f;
                    byte fadeValue = (byte)(distanceFactor * 255);
                    int angleIndex = Mathf.RoundToInt(angleFactor * angularResolutionH);
                    radiusInfos[iStart + ih] = fadeValue | (angleIndex << 8);
                }
            }
            
            float angularCoordScale = angularResolution / (Mathf.PI * 0.5f);
            
            for (int iy = 0; iy < radius; iy++) {
                float y = iy + 0.5f;
                for (int ix = 0; ix < radius; ix++) {
                    float x = ix + 0.5f;
                    
                    int ixy = ix + iy * radius;
                    
                    float r = Mathf.Sqrt(x * x + y * y);
                    int ir = Mathf.FloorToInt(r);
                    
                    if (ir >= radius) {
                        columnInfos[ixy].MinX = angularResolution;
                        columnInfos[ixy].MaxX = 0;
                        columnInfos[ixy].SpanY = 0;
                        columnInfos[ixy].Radius = ir;
                        continue;
                    }
                    
                    int aMin = Mathf.RoundToInt(Mathf.Atan2(ix, iy+1) * angularCoordScale);
                    int aMax = Mathf.RoundToInt(Mathf.Atan2(ix+1, iy) * angularCoordScale);
                    
                    columnInfos[ixy].MinX = aMin;
                    columnInfos[ixy].MaxX = aMax;
                    columnInfos[ixy].SpanY = radiusSpanInfos[ir];
                    columnInfos[ixy].Radius = ir;
                }
            }
        }
        
        private static void ResizeTexture(ref Texture2D texture, int w, int h) {
            if (texture && (w == texture.width) && (h == texture.height)) return;

            if (texture) Destroy(texture);

            texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
        }
        
        private static void ResizePixels<T>(ref T[] pixels, int w, int h) {
            int size = w * h;
            
            if ((pixels != null) && (pixels.Length >= size)) return;
            
            pixels = new T[size];
        }
    }
}