// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System;
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace OctreeSplatting.Demo {
    public class DemoController {
        private Object3D player;
        private Object3D playerCamera;
        private CameraFrustum cameraFrustum;
        
        private float ApertureSize => (float)Math.Pow(2, -zoomSteps * zoomFactor);
        private float DistanceScale => (float)Math.Pow(2, distanceSteps * zoomFactor);
        
        private float zoomFactor = 0.125f;
        private int distanceSteps = 56;
        private int zoomSteps = -12;
        private float cameraPitch = -37;
        private float cameraYaw = -47;
        
        private Quaternion modelRotation = Quaternion.CreateFromYawPitchRoll(0, (float)Math.PI, 0);
        
        private int gridExtent = 2;
        private float gridOffset = 1.2f;

        private List<Object3D> models = new List<Object3D>();

        private List<Object3D> sortedModels = new List<Object3D>();

        private RenderingJob[] renderJobs;
        private Task[] renderTasks;
        private Renderbuffer renderbuffer;

        private System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        private double averageFrameTime;
        private long frameCount;
        private Dictionary<int, int> timeHistogram = new Dictionary<int, int>();
        private int timeCountMax;
        private int mostProbableTime;

        private Color32 background = new Color32 {R = 50, G = 80, B = 80, A = 255};

        public string ViewInfo => $"P={(int)cameraPitch}, Y={(int)cameraYaw}, Z={zoomSteps}";
        public string TimeInfo => $"{FrameTime} ({MostProbableTime}) ms @ {ThreadCount} thread(s)";
        public int FrameTime => (int)stopwatch.ElapsedMilliseconds;
        public double AverageFrameTime => averageFrameTime;
        public int MostProbableTime => mostProbableTime;

        public int ThreadCount = 1;

        public int MaxLevel = -1;
        public float AbsoluteDilation = 0;
        public float RelativeDilation = 0;
        public bool UseUpscaling = false;
        public SplatShape Shape = SplatShape.Rectangle;

        public int Zoom {
            get => zoomSteps;
            set {
                zoomSteps = value;
                UpdateCameraAperture();
            }
        }
        
        public float Perspective {
            get => cameraFrustum.Perspective;
            set => cameraFrustum.Perspective = Math.Min(Math.Max(value, 0), 1);
        }
        
        public float CameraPitch {
            get => cameraPitch;
            set => cameraPitch = Math.Min(Math.Max(value, -89), 89);
        }
        
        public float CameraYaw {
            get => cameraYaw;
            set => cameraYaw = value % 360f;
        }

        public DemoController(OctreeNode[] octree) {
            renderJobs = new RenderingJob[16];
            renderTasks = new Task[renderJobs.Length];
            for (int i = 0; i < renderJobs.Length; i++) {
                renderJobs[i] = new RenderingJob();
            }
            
            renderbuffer = new Renderbuffer();
            
            player = new Object3D();
            
            playerCamera = new Object3D();
            cameraFrustum = new CameraFrustum();
            cameraFrustum.Perspective = 0;
            cameraFrustum.Far = 10000;

            for (int ix = -gridExtent; ix <= gridExtent; ix++) {
                for (int iz = -gridExtent; iz <= gridExtent; iz++) {
                    var object3d = new Object3D(octree);
                    object3d.Position = new Vector3(ix, 0, iz) * gridOffset;
                    object3d.Rotation = modelRotation;
                    models.Add(object3d);
                }
            }
        }
        
        public (int, int, Color32[]) GetImageData() {
            return (renderbuffer.SizeX, renderbuffer.SizeY, renderbuffer.ColorPixels);
        }
        
        public void Resize(int sizeX, int sizeY) {
            renderbuffer.Resize(sizeX, sizeY);
            UpdateCameraAperture();
        }
        
        public void MoveCamera(float x, float y, float z) {
            var movement = new Vector3(x, y, z);
            
            if (movement.LengthSquared() > 0) {
                var cameraForward = -playerCamera.AxisZ;
                player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.Atan2(cameraForward.X, cameraForward.Z));
                player.Position += player.AxisX * movement.X + player.AxisY * movement.Y + player.AxisZ * movement.Z;
            }
        }
        
        public void RenderFrame() {
            var degreesToRadians = (float)Math.PI / 180f;
            var pitchRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, cameraPitch * degreesToRadians);
            var yawRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, cameraYaw * degreesToRadians);
            playerCamera.Rotation = yawRotation * pitchRotation;
            var cameraForward = -playerCamera.AxisZ;
            var cameraDistance = ApertureSize * DistanceScale;
            playerCamera.Position = player.Position - (cameraForward * cameraDistance);
            cameraFrustum.Focus = Vector3.UnitZ * cameraDistance;
            
            stopwatch.Restart();

            renderbuffer.UseTemporalUpscaling = UseUpscaling;

            renderbuffer.Begin(background);
            DrawOctrees(playerCamera.Inverse, cameraFrustum.Matrix);
            renderbuffer.End();
            
            stopwatch.Stop();
            
            UpdateTimeInfo();
        }

        private void UpdateCameraAperture() {
            var scale = ApertureSize / Math.Max(renderbuffer.SizeX, renderbuffer.SizeY);
            cameraFrustum.Aperture = new Vector2(renderbuffer.SizeX * scale, renderbuffer.SizeY * scale);
        }
        
        private void UpdateTimeInfo() {
            frameCount++;
            averageFrameTime = (averageFrameTime * (frameCount-1) + stopwatch.ElapsedMilliseconds) / frameCount;
            
            // Periodically clear to adapt to changing conditions
            if ((frameCount % 500) == 0) {
                timeHistogram.Clear();
                timeCountMax = 0;
            }
            
            int time = (int)stopwatch.ElapsedMilliseconds;
            if (!timeHistogram.TryGetValue(time, out int timeCount)) timeCount = 0;
            timeCount++;
            timeHistogram[time] = timeCount;
            
            if (timeCount > timeCountMax) {
                timeCountMax = timeCount;
                mostProbableTime = int.MaxValue;
                foreach (var kv in timeHistogram) {
                    if (kv.Value != timeCountMax) continue;
                    mostProbableTime = Math.Min(mostProbableTime, kv.Key);
                }
            }
        }

        private void DrawOctrees(Matrix4x4 viewMatrix, Matrix4x4 projectionMatrix) {
            GatherVisibleModels(viewMatrix, projectionMatrix);
            
            var vNear = Vector4.Transform(new Vector3(0, 0, cameraFrustum.Near), projectionMatrix);
            var vFar = Vector4.Transform(new Vector3(0, 0, cameraFrustum.Far), projectionMatrix);
            vNear.Z = 0;
            vFar.Z = renderbuffer.SizeZ;
            var zSlope = Math.Abs(vFar.W - vNear.W) / (vFar.Z - vNear.Z);
            var zIntercept = Math.Abs(vNear.W) - zSlope * vNear.Z;
            
            ThreadCount = Math.Min(Math.Max(ThreadCount, 1), renderJobs.Length);
            
            int yStep = (renderbuffer.DataSizeY + ThreadCount - 1) / ThreadCount;
            
            for (int jobIndex = 0, y = 0; jobIndex < ThreadCount; jobIndex++, y += yStep) {
                var renderJob = renderJobs[jobIndex];
                
                renderJob.Viewport.MinX = 0;
                renderJob.Viewport.MinY = y;
                renderJob.Viewport.MaxX = renderbuffer.DataSizeX - 1;
                renderJob.Viewport.MaxY = Math.Min(renderbuffer.DataSizeY, y + yStep) - 1;
                renderJob.Renderbuffer = renderbuffer;
                renderJob.SortedModels = sortedModels;
                
                renderJob.MaxLevel = MaxLevel;
                renderJob.AbsoluteDilation = AbsoluteDilation;
                renderJob.RelativeDilation = RelativeDilation;
                renderJob.Shape = Shape;
                
                renderJob.ZIntercept = zIntercept;
                renderJob.ZSlope = zSlope;
                
                if (ThreadCount > 1) {
                    renderTasks[jobIndex] = new Task(renderJob.Render);
                    renderTasks[jobIndex].Start();
                } else {
                    // Main-thread stuff is easier to debug
                    renderJob.Render();
                }
            }
            
            if (ThreadCount > 1) {
                for (int jobIndex = 0; jobIndex < ThreadCount; jobIndex++) {
                    renderTasks[jobIndex].Wait();
                }
            }
        }
        
        private void GatherVisibleModels(Matrix4x4 viewMatrix, Matrix4x4 projectionMatrix) {
            var near = cameraFrustum.Near;
            var far = cameraFrustum.Far;
            
            var offset = new Vector3(0, 0, -near);
            var scale = new Vector3(
                0.5f * renderbuffer.DataSizeX,
                0.5f * renderbuffer.DataSizeY,
                renderbuffer.SizeZ / (far - near)
            );
            
            float zMax = renderbuffer.SizeZ;
            
            sortedModels.Clear();
            
            foreach (var object3d in models) {
                if (object3d.Octree == null) continue;
                
                ProjectCage(object3d, ref viewMatrix, ref projectionMatrix, offset, scale);
                
                // I use !(...) here to guard against NaN values
                if (!(object3d.ProjectedMin.Z < zMax)) continue;
                if (!(object3d.ProjectedMax.Z > 0)) continue;
                if (!(object3d.ProjectedMin.X < scale.X)) continue;
                if (!(object3d.ProjectedMax.X > -scale.X)) continue;
                if (!(object3d.ProjectedMin.Y < scale.Y)) continue;
                if (!(object3d.ProjectedMax.Y > -scale.Y)) continue;
                
                sortedModels.Add(object3d);
            }
            
            sortedModels.Sort((itemA, itemB) => {
                return itemA.ProjectedMin.Z.CompareTo(itemB.ProjectedMin.Z);
            });
        }
        
        private void ProjectCage(Object3D object3d, ref Matrix4x4 viewMatrix,
            ref Matrix4x4 projectionMatrix, Vector3 offset, Vector3 scale)
        {
            var matrixMV = object3d.Matrix * viewMatrix;
            
            // We expect that projection matrix projects (x,y) to (-1..1) normalized space.
            // scale.X is half-width, scale.Y is half-height of the render target.
            for (int i = 0; i < 8; i++) {
                // I'm not sure why the vertex position has to be
                // negated here, but the results will be wrong without it
                var position = Vector3.Transform(-object3d.Cage[i], matrixMV);
                var projection = Vector4.Transform(position, projectionMatrix);
                var inverseW = 1f / projection.W;
                
                ref var vertex = ref object3d.ProjectedCage[i];
                vertex.Position.X = (projection.X + offset.X) * scale.X;
                vertex.Position.Y = (projection.Y + offset.Y) * scale.Y;
                vertex.Position.Z = (-position.Z + offset.Z) * scale.Z;
                vertex.Projection.X = vertex.Position.X * inverseW;
                vertex.Projection.Y = vertex.Position.Y * inverseW;
                
                if (i == 0) {
                    object3d.ProjectedMin.X = object3d.ProjectedMax.X = vertex.Projection.X;
                    object3d.ProjectedMin.Y = object3d.ProjectedMax.Y = vertex.Projection.Y;
                    object3d.ProjectedMin.Z = object3d.ProjectedMax.Z = vertex.Position.Z;
                } else {
                    if (object3d.ProjectedMin.X > vertex.Projection.X) object3d.ProjectedMin.X = vertex.Projection.X;
                    if (object3d.ProjectedMin.Y > vertex.Projection.Y) object3d.ProjectedMin.Y = vertex.Projection.Y;
                    if (object3d.ProjectedMin.Z > vertex.Position.Z) object3d.ProjectedMin.Z = vertex.Position.Z;
                    if (object3d.ProjectedMax.X < vertex.Projection.X) object3d.ProjectedMax.X = vertex.Projection.X;
                    if (object3d.ProjectedMax.Y < vertex.Projection.Y) object3d.ProjectedMax.Y = vertex.Projection.Y;
                    if (object3d.ProjectedMax.Z < vertex.Position.Z) object3d.ProjectedMax.Z = vertex.Position.Z;
                }
            }
        }
        
        private class RenderingJob {
            public Renderbuffer Renderbuffer;
            public Range2D Viewport;
            public List<Object3D> SortedModels;
            
            private OctreeRenderer renderer = new OctreeRenderer();
            private CageSubdivider<uint> subdivider = new CageSubdivider<uint>();
            
            private ProjectedVertex[] subdivCage = new ProjectedVertex[8];
            private Vector3 screenSize, screenCenter;
            
            public float ZIntercept {
                get => subdivider.ZIntercept;
                set => subdivider.ZIntercept = value;
            }
            public float ZSlope {
                get => subdivider.ZSlope;
                set => subdivider.ZSlope = value;
            }
            
            public int MaxLevel = -1;
            public float AbsoluteDilation = 0;
            public float RelativeDilation = 0;
            public SplatShape Shape = SplatShape.Rectangle;
            
            public void Render() {
                renderer.Viewport = Viewport;
                renderer.BufferShift = Renderbuffer.ShiftX;
                renderer.Pixels = Renderbuffer.DataPixels;
                
                Renderbuffer.GetSamplingOffset(out float sampleX, out float sampleY);
                
                screenSize = new Vector3(Renderbuffer.DataSizeX, Renderbuffer.DataSizeY, Renderbuffer.SizeZ);
                screenCenter = screenSize * 0.5f;
                
                if (Renderbuffer.UseTemporalUpscaling) {
                    screenCenter.X += sampleX;
                    screenCenter.Y += sampleY;
                    renderer.MapThreshold = 1;
                } else {
                    renderer.MapThreshold = 2;
                }
                
                for (int objectID = 0; objectID < SortedModels.Count; objectID++) {
                    var object3d = SortedModels[objectID];
                    
                    renderer.Octree = object3d.Octree;
                    renderer.RootAddress = 0;
                    
                    renderer.MaxLevel = MaxLevel;
                    renderer.AbsoluteDilation = AbsoluteDilation;
                    renderer.RelativeDilation = RelativeDilation;
                    renderer.Shape = Shape;
                    
                    var rootSizeX = object3d.ProjectedMax.X - object3d.ProjectedMin.X;
                    var rootSizeY = object3d.ProjectedMax.Y - object3d.ProjectedMin.Y;
                    var rootSizeMax = Math.Max(rootSizeX, rootSizeY);
                    
                    bool sizeCondition = (rootSizeMax >= OctreeRenderer.MaxSizeInPixels);
                    bool zCondition = (object3d.ProjectedMin.Z <= 0);
                    bool shouldSubdivide = sizeCondition | zCondition;
                    
                    if (!shouldSubdivide) {
                        CageToMatrix(object3d.ProjectedCage, ref renderer.Matrix);
                        renderer.Matrix.M41 += screenCenter.X;
                        renderer.Matrix.M42 += screenCenter.Y;
                        
                        shouldSubdivide = !renderer.Render();
                        shouldSubdivide &= (rootSizeMax > 1);
                    }
                    
                    if (shouldSubdivide) {
                        subdivider.Subdivide(object3d.ProjectedCage, renderer.RootAddress,
                            renderer.Octree[renderer.RootAddress].Mask, SubdivisionCallback);
                    }
                }
            }
            
            private byte SubdivisionCallback(CageSubdivider<uint>.State state) {
                for (int i = 0; i < 8; i++) {
                    subdivCage[i] = state.Grid[state.Indices[i]];
                }
                
                CageToMatrix(subdivCage, ref renderer.Matrix);
                renderer.Matrix.M41 += screenCenter.X;
                renderer.Matrix.M42 += screenCenter.Y;
                
                var center = renderer.Matrix.Translation;
                var extents = GetMatrixExtents(in renderer.Matrix);
                var min = center - extents;
                var max = center + extents;
                
                if ((max.X <= 0) | (min.X >= screenSize.X)) return 0;
                if ((max.Y <= 0) | (min.Y >= screenSize.Y)) return 0;
                if ((max.Z <= 0) | (min.Z >= screenSize.Z)) return 0;
                
                renderer.MaxLevel = (MaxLevel < 0) ? -1 : Math.Max(MaxLevel - state.Level, 0);
                
                ref var node = ref renderer.Octree[state.ParentData];
                bool isLeaf = (node.Mask == 0) | (renderer.MaxLevel == 0);
                
                // Don't use isLeaf for this check
                byte subnodeMask;
                if (node.Mask != 0) {
                    state.Data = node.Address + state.Octant;
                    subnodeMask = renderer.Octree[state.Data].Mask;
                } else {
                    state.Data = state.ParentData;
                    subnodeMask = 255;
                }
                
                // If a node intersects the near plane, either
                // subdivide it or cull (if it's a leaf node)
                if (min.Z <= 0) return isLeaf ? (byte)0 : subnodeMask;
                
                var maxSize = Math.Max(extents.X, extents.Y) * 2;
                if (maxSize >= OctreeRenderer.MaxSizeInPixels) return subnodeMask;
                
                renderer.RootAddress = state.Data;
                if (renderer.Render()) return 0;
                
                // Still failed to render, despite our checks (they may lack precision).
                // If node is larger than a pixel, continue subdividing.
                return (maxSize > 1) ? subnodeMask : (byte)0;
            }
            
            private static Vector3 GetMatrixExtents(in Matrix4x4 matrix) {
                return new Vector3(
                    Math.Abs(matrix.M11) + Math.Abs(matrix.M21) + Math.Abs(matrix.M31),
                    Math.Abs(matrix.M12) + Math.Abs(matrix.M22) + Math.Abs(matrix.M32),
                    Math.Abs(matrix.M13) + Math.Abs(matrix.M23) + Math.Abs(matrix.M33)
                );
            }
            
            private static void CageToMatrix(ProjectedVertex[] cage, ref Matrix4x4 matrix) {
                var TMinX = cage[0].Projection.X;
                var XMinX = cage[1].Projection.X - TMinX;
                var YMinX = cage[2].Projection.X - TMinX;
                var ZMinX = cage[4].Projection.X - TMinX;
                var TMaxX = cage[7].Projection.X;
                var XMaxX = cage[6].Projection.X - TMaxX;
                var YMaxX = cage[5].Projection.X - TMaxX;
                var ZMaxX = cage[3].Projection.X - TMaxX;
                
                var TMinY = cage[0].Projection.Y;
                var XMinY = cage[1].Projection.Y - TMinY;
                var YMinY = cage[2].Projection.Y - TMinY;
                var ZMinY = cage[4].Projection.Y - TMinY;
                var TMaxY = cage[7].Projection.Y;
                var XMaxY = cage[6].Projection.Y - TMaxY;
                var YMaxY = cage[5].Projection.Y - TMaxY;
                var ZMaxY = cage[3].Projection.Y - TMaxY;
                
                var TMinZ = cage[0].Position.Z;
                var XMinZ = cage[1].Position.Z - TMinZ;
                var YMinZ = cage[2].Position.Z - TMinZ;
                var ZMinZ = cage[4].Position.Z - TMinZ;
                var TMaxZ = cage[7].Position.Z;
                var XMaxZ = cage[6].Position.Z - TMaxZ;
                var YMaxZ = cage[5].Position.Z - TMaxZ;
                var ZMaxZ = cage[3].Position.Z - TMaxZ;
                
                matrix.M11 = (XMinX - XMaxX) * 0.25f;
                matrix.M12 = (XMinY - XMaxY) * 0.25f;
                matrix.M13 = (XMinZ - XMaxZ) * 0.25f;
                
                matrix.M21 = (YMinX - YMaxX) * 0.25f;
                matrix.M22 = (YMinY - YMaxY) * 0.25f;
                matrix.M23 = (YMinZ - YMaxZ) * 0.25f;
                
                matrix.M31 = (ZMinX - ZMaxX) * 0.25f;
                matrix.M32 = (ZMinY - ZMaxY) * 0.25f;
                matrix.M33 = (ZMinZ - ZMaxZ) * 0.25f;
                
                matrix.M41 = (TMinX + TMaxX) * 0.5f;
                matrix.M42 = (TMinY + TMaxY) * 0.5f;
                matrix.M43 = (TMinZ + TMaxZ) * 0.5f;
            }
        }
    }
}
