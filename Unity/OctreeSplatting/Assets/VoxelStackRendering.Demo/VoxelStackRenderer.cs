// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using UnityEngine;

namespace VoxelStackRendering.Demo {
    public class VoxelStackRenderer : MonoBehaviour {
        private struct IterRange {
            public int Start, Stop, Step;
            public IterRange(int start, int stop, int step) {
                Start = start;
                Stop = stop;
                Step = step;
            }
        }
        
        private struct Vector3i {
            public int x, y, z;
        }
        
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
            
            fixed (VoxelStackModel.ColumnInfo* gridPtr = grid)
            fixed (byte* spanDataPtr = spanData)
            fixed (PixelData* dataBufPtr = dataBuf)
            {
                // Grid indices and index increments in X and Y directions
                int iGX0 = 0, diGX = 1;
                int iGY0 = 0, diGY = sizeX;
                
                // Make sure we traverse the grid in front-to-back order
                if (X.z > 0) {
                    iGX0 = sizeX - 1; diGX = -diGX;
                    T.x += (sizeX - 1) * X.x;
                    T.y += (sizeX - 1) * X.y;
                    T.z += (sizeX - 1) * X.z;
                    X.x = -X.x;
                    X.y = -X.y;
                    X.z = -X.z;
                }
                if (Y.z > 0) {
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
                
                for (int yCount = sizeY, iGY = iGY0; yCount > 0; yCount--, iGY += diGY) {
                    var pXY = pY; // pXY is the projected position of the grid cell's floor
                    pY.x += Y.x;
                    pY.y += Y.y;
                    pY.z += Y.z;
                    
                    for (int xCount = sizeX, iG = iGY + iGX0; xCount > 0; xCount--, iG += diGX) {
                        var pXYZ = pXY; // pXYZ is the projected position of a span's first (or last) voxel
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
                        
                        // Make sure the voxel spans are traversed in front-to-back order
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
            
            fixed (VoxelStackModel.ColumnInfo* gridPtr = grid)
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
            
            public VoxelStackModel.ColumnInfo* gridPtr;
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
                    var pXY = pY; // pXY is the projected position of the grid cell's floor
                    pY.x += dY.x;
                    pY.y += dY.y;
                    pY.z += dY.z;
                    
                    for (int tileX = rangeX.Start, secX = sectorX0; tileX != rangeX.Stop; tileX += rangeX.Step, secX++) {
                        var pXYZ = pXY; // pXYZ is the projected position of a span's first voxel
                        pXY.x += dX.x;
                        pXY.y += dX.y;
                        pXY.z += dX.z;
                        
                        ref var gridCell = ref gridPtr[tileX + tileY * sizeX];
                        if (gridCell.SpanCount == 0) continue;
                        
                        int screenX = pXYZ.x >> PrecisionBits;
                        if ((screenX < 0) | (screenX >= screenW)) continue;
                        
                        ref var columnInfo = ref columnInfosPtr[secX + secY * radius];
                        if (columnInfo.SpanY == 0) break;
                        
                        int hSpanOffset = radius - obs.z;
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
                            
                            int h0Clamped = h0;
                            if (h0Clamped < hSpanMin) h0Clamped = hSpanMin;
                            int h1Clamped = h1;
                            if (h1Clamped > hSpanMax) h1Clamped = hSpanMax;
                            
                            if (h0Clamped > h1Clamped) continue;
                            
                            int colMin = h0Clamped + hSpanOffset;
                            int colMax = h1Clamped + hSpanOffset;
                            
                            int y = pXYZ.y + h0Clamped * Z.y + ZyHalf;
                            
                            var alphaScale = spanAlpha / (255f * 255f);
                            
                            for (int spanI = iData2 + (h0Clamped - h0) * DataStep, colI = colMin; colI < colMax; spanI += DataStep, colI++, y += Z.y) {
                                // radiusInfo contains:
                                // 0..7 bits: opacity factor (for smooth fading near the edges of the vision range)
                                // 8..31 bits: voxel's Y coordinate in the shadow buffer
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