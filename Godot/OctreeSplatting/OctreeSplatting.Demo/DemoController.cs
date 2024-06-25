// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System;
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace OctreeSplatting.Demo {
    public class DemoController {
        private Object3D player;
        private Object3D playerModel;
        private Object3D playerCamera;
        private CameraFrustum cameraFrustum;
        
        private float ApertureSize => (float)Math.Pow(2, -zoomSteps * zoomFactor);
        private float DistanceScale => (float)Math.Pow(2, distanceSteps * zoomFactor);
        
        private float zoomFactor = 0.125f;
        private int distanceSteps = 56;
        private int zoomSteps = -12;
        private float cameraPitch = -37;
        private float cameraYaw = -47;
        private bool usePerspective = false;
        
        private Quaternion modelRotation = Quaternion.CreateFromYawPitchRoll(0, (float)Math.PI, 0);
        
        private int gridExtent = 2;
        private float gridOffset = 1.2f;

        private List<Object3D> models = new List<Object3D>();

        private List<Object3D> sortedModels = new List<Object3D>();

        private System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();

        private RenderingJob[] renderJobs;
        private Task[] renderTasks;
        private Renderbuffer renderbuffer;

        private System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        private System.Diagnostics.Stopwatch stopwatchBegin = new System.Diagnostics.Stopwatch();
        private System.Diagnostics.Stopwatch stopwatchEnd = new System.Diagnostics.Stopwatch();
        private double averageFrameTime;
        private long frameCount;
        private Dictionary<int, int> timeHistogram = new Dictionary<int, int>();
        private int timeCountMax;
        private int mostProbableTime;

        private Color32 background = new Color32 {R = 50, G = 80, B = 80, A = 255};

        public string ViewInfo => $"P={(int)cameraPitch}, Y={(int)cameraYaw}, Z={zoomSteps}";
        //public string TimeInfo => $"{FrameTime} ({MostProbableTime}) ms @ {ThreadCount} thread(s)";
        public string TimeInfo => $"{FrameTime}; {stopwatchBegin.ElapsedMilliseconds}, {stopwatchEnd.ElapsedMilliseconds}";
        public int FrameTime => (int)stopwatch.ElapsedMilliseconds;
        public double AverageFrameTime => averageFrameTime;
        public int MostProbableTime => mostProbableTime;

        public int ThreadCount = 1;

        public int MaxLevel = -1;
        public float AbsoluteDilation = 0;
        public float RelativeDilation = 0;
        public bool UseUpscaling = false;
        public SplatShape Shape = SplatShape.Rectangle;
        public bool ShowBounds = false;
        public float MaxDistortion = 1;
        public bool UseMapAt3 = false;
        
        public float EffectiveNear = 0;

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

        public DemoController(OctreeNode[] octree, OctreeNode[] playerOctree = null) {
            timer.Start();
            
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
            cameraFrustum.Near = 0.001f;
            cameraFrustum.Far = 1000;
            
            SwitchToOrthographic();
            
            playerModel = new Object3D(playerOctree);
            playerModel.Rotation = modelRotation;
            playerModel.Scale = Vector3.One * 0.025f;
            // models.Add(playerModel);
            
            for (int ix = -gridExtent; ix <= gridExtent; ix++) {
                for (int iz = -gridExtent; iz <= gridExtent; iz++) {
                    var object3d = new Object3D(octree);
                    object3d.Position = new Vector3(ix, 0, iz) * gridOffset;
                    object3d.Rotation = modelRotation;
                    models.Add(object3d);
                }
            }
        }
        
        public void AssignOctrees(OctreeNode[] octree, OctreeNode[] playerOctree = null) {
            foreach (var model in models) {
                model.Octree = octree;
            }
            playerModel.Octree = playerOctree;
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
                
                playerModel.Position = player.Position;
                playerModel.Rotation = player.Rotation * modelRotation;
            }
        }
        
        public void SwitchToPerspective() {
            usePerspective = true;
            distanceSteps = -8;
            cameraFrustum.Perspective = 0.98f;
            cameraFrustum.Near = 0.001f;
            cameraFrustum.Far = 100;
        }
        
        public void SwitchToOrthographic() {
            usePerspective = false;
            distanceSteps = 56;
            cameraFrustum.Perspective = 0;
            cameraFrustum.Near = 0.001f;
            cameraFrustum.Far = 1000;
        }
        
        public void RenderFrame() {
            var degreesToRadians = (float)Math.PI / 180f;
            var pitchRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, cameraPitch * degreesToRadians);
            var yawRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, cameraYaw * degreesToRadians);
            playerCamera.Rotation = yawRotation * pitchRotation;
            var cameraForward = -playerCamera.AxisZ;
            var cameraDistance = ApertureSize * DistanceScale;
            if (usePerspective) {
                playerCamera.Position = player.Position - (cameraForward * cameraDistance);
            } else {
                playerCamera.Position = player.Position - (cameraForward * cameraFrustum.Far * 0.5f);
            }
            cameraFrustum.Focus = Vector3.UnitZ * cameraDistance;
            
            float time = timer.ElapsedMilliseconds / 1000f;
            var deformRotation = Matrix4x4.CreateFromYawPitchRoll((float)Math.Sin(time*1.5f), 0, 0);
            playerModel.ResetCage();
            // playerModel.Cage[2] = Vector3.Transform(playerModel.Cage[2], deformRotation);
            // playerModel.Cage[3] = Vector3.Transform(playerModel.Cage[3], deformRotation);
            // playerModel.Cage[6] = Vector3.Transform(playerModel.Cage[6], deformRotation);
            // playerModel.Cage[7] = Vector3.Transform(playerModel.Cage[7], deformRotation);
            
            stopwatch.Restart();
            
            renderbuffer.UseTemporalUpscaling = UseUpscaling;
            
            stopwatchBegin.Restart();
            renderbuffer.Begin(background);
            stopwatchBegin.Stop();
            
            DrawOctrees(playerCamera.Inverse, cameraFrustum.Matrix);
            
            stopwatchEnd.Restart();
            renderbuffer.End();
            stopwatchEnd.Stop();
            
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
            
            float effectiveNear = zIntercept / zSlope;
            float effectiveNearMax = renderbuffer.SizeZ * Math.Min(Math.Max(EffectiveNear, 0), 1);
            if (!(effectiveNear < effectiveNearMax)) effectiveNear = effectiveNearMax;
            effectiveNear = -effectiveNear;
            
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
                renderJob.ShowBounds = ShowBounds;
                renderJob.MaxDistortion = MaxDistortion * (renderbuffer.UseTemporalUpscaling ? 0.5f : 1f);
                renderJob.UseMapAt3 = UseMapAt3;
                renderJob.EffectiveNear = effectiveNear;
                
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
            private struct SubdivisionData {
                public uint Address;
                public int MinY;
            }
            
            public Renderbuffer Renderbuffer;
            public Range2D Viewport;
            public List<Object3D> SortedModels;
            
            private OctreeRenderer renderer = new OctreeRenderer();
            private CageSubdivider<SubdivisionData> subdivider = new CageSubdivider<SubdivisionData>();
            
            private ProjectedVertex[] subdivCage = new ProjectedVertex[8];
            private Vector3 screenSize, screenCenter;
            
            private int maxLevel;
            
            private SubdivisionDecider subdivisionDecider;
            
            private bool reuseStencil;
            private bool isAffine;
            
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
            public bool ShowBounds = false;
            
            public float EffectiveNear;
            
            public float MaxDistortion = 1;
            
            public bool UseMapAt3 = false;
            
            // public float DistortionAbsoluteDilation = 0.25f;
            public float DistortionAbsoluteDilation = 0;
            // public float DistortionRelativeDilation = 0.025f;
            public float DistortionRelativeDilation = 0;
            
            public void Render() {
                renderer.Begin(Renderbuffer, Viewport);
                
                try {
                    RenderMain();
                } finally {
                    renderer.Finish();
                }
            }
            
            public void RenderMain() {
                Renderbuffer.GetSamplingOffset(out float sampleX, out float sampleY);
                
                screenSize = new Vector3(Renderbuffer.DataSizeX, Renderbuffer.DataSizeY, Renderbuffer.SizeZ);
                screenCenter = screenSize * 0.5f;
                
                if (Renderbuffer.UseTemporalUpscaling) {
                    screenCenter.X += sampleX;
                    screenCenter.Y += sampleY;
                    renderer.MapThreshold = 1;
                } else {
                    renderer.MapThreshold = UseMapAt3 ? 3 : 2;
                }
                
                maxLevel = (MaxLevel < 0) ? int.MaxValue : MaxLevel;
                
                subdivisionDecider.IsCube = IsCube();
                
                for (int objectID = 0; objectID < SortedModels.Count; objectID++) {
                    var object3d = SortedModels[objectID];
                    
                    renderer.Octree = object3d.Octree;
                    renderer.RootAddress = 0;
                    
                    ref var node = ref renderer.Octree[renderer.RootAddress];
                    
                    var rootSizeX = object3d.ProjectedMax.X - object3d.ProjectedMin.X;
                    var rootSizeY = object3d.ProjectedMax.Y - object3d.ProjectedMin.Y;
                    
                    subdivisionDecider.IsLeaf = IsLeaf(node.Mask, 0);
                    subdivisionDecider.IsTooClose = IsTooClose(object3d.ProjectedMin.Z);
                    subdivisionDecider.IsTooBig = IsTooBig(rootSizeX, rootSizeY);
                    subdivisionDecider.IsDistorted = false;
                    
                    var decision = subdivisionDecider.Evaluate();
                    if (decision == SubdivisionDecision.Cull) continue;
                    
                    renderer.MaxLevel = maxLevel;
                    renderer.AbsoluteDilation = AbsoluteDilation;
                    renderer.RelativeDilation = RelativeDilation;
                    renderer.Shape = Shape;
                    renderer.ShowBounds = ShowBounds;
                    
                    if (decision == SubdivisionDecision.Render) {
                        float distortion = CageToMatrix(object3d.ProjectedCage, ref renderer.Matrix);
                        renderer.Matrix.M41 += screenCenter.X;
                        renderer.Matrix.M42 += screenCenter.Y;
                        
                        subdivisionDecider.IsDistorted = IsDistorted(distortion);
                        decision = subdivisionDecider.Evaluate();
                        
                        if (decision == SubdivisionDecision.Render) {
                            var result = renderer.Render();
                            if (result >= OctreeRenderer.Result.Culled) continue;
                            
                            subdivisionDecider.IsTooBig = (result == OctreeRenderer.Result.TooBig);
                            subdivisionDecider.IsTooClose = (result == OctreeRenderer.Result.TooClose);
                            decision = subdivisionDecider.Evaluate();
                        }
                    }
                    
                    if (decision == SubdivisionDecision.Subdivide) {
                        isAffine = object3d.IsAffine();
                        reuseStencil = false;
                        
                        var mask = subdivisionDecider.IsLeaf ? (byte)255 : node.Mask;
                        
                        var subdivisionData = new SubdivisionData {
                            Address = renderer.RootAddress,
                            MinY = Viewport.MinY,
                        };
                        subdivider.Subdivide(object3d.ProjectedCage, subdivisionData,
                            mask, SubdivisionCallback);
                    }
                }
            }
            
            private byte SubdivisionCallback(CageSubdivider<SubdivisionData>.State state) {
                Vector3 min = default, max = default;
                
                for (int i = 0; i < 8; i++) {
                    var vertex = state.Grid[state.Indices[i]];
                    subdivCage[i] = vertex;
                    
                    // We *have* to calculate bounds from the
                    // actual corners, instead of the matrix
                    if (i == 0) {
                        min.X = max.X = vertex.Projection.X;
                        min.Y = max.Y = vertex.Projection.Y;
                        min.Z = max.Z = vertex.Position.Z;
                    } else {
                        if (min.X > vertex.Projection.X) min.X = vertex.Projection.X;
                        if (min.Y > vertex.Projection.Y) min.Y = vertex.Projection.Y;
                        if (min.Z > vertex.Position.Z) min.Z = vertex.Position.Z;
                        if (max.X < vertex.Projection.X) max.X = vertex.Projection.X;
                        if (max.Y < vertex.Projection.Y) max.Y = vertex.Projection.Y;
                        if (max.Z < vertex.Position.Z) max.Z = vertex.Position.Z;
                    }
                }
                
                min.X += screenCenter.X;
                min.Y += screenCenter.Y;
                max.X += screenCenter.X;
                max.Y += screenCenter.Y;
                
                if ((max.X <= 0) | (min.X >= screenSize.X)) return 0;
                if ((max.Y <= 0) | (min.Y >= screenSize.Y)) return 0;
                if ((max.Z <= EffectiveNear) | (min.Z >= screenSize.Z)) return 0;
                
                if (min.Z > EffectiveNear) {
                    var region = new Range2D() {
                        MinX = (int)(min.X + 0.5f),
                        MinY = (int)(min.Y + 0.5f),
                        MaxX = (int)(max.X - 0.5f),
                        MaxY = (int)(max.Y - 0.5f),
                    };
                    if (region.MinY < state.ParentData.MinY) region.MinY = state.ParentData.MinY;
                    if (renderer.IsOccluded(region, (int)min.Z, out state.Data.MinY)) return 0;
                } else {
                    state.Data.MinY = state.ParentData.MinY;
                }
                
                float distortion = CageToMatrix(subdivCage, ref renderer.Matrix);
                renderer.Matrix.M41 += screenCenter.X;
                renderer.Matrix.M42 += screenCenter.Y;
                renderer.Matrix.M43 -= EffectiveNear;
                
                ref var node = ref renderer.Octree[state.ParentData.Address];
                
                subdivisionDecider.IsLeaf = IsLeaf(node.Mask, state.Level);
                subdivisionDecider.IsTooClose = IsTooClose(min.Z);
                subdivisionDecider.IsTooBig = IsTooBig(max.X - min.X, max.Y - min.Y);
                subdivisionDecider.IsDistorted = IsDistorted(distortion);
                
                var decision = subdivisionDecider.Evaluate();
                if (decision == SubdivisionDecision.Cull) return 0;
                
                byte subnodeMask;
                if ((node.Mask != 0) & (state.Level <= maxLevel)) {
                    state.Data.Address = node.Address + state.Octant;
                    subnodeMask = renderer.Octree[state.Data.Address].Mask;
                    
                    subdivisionDecider.IsLeaf = IsLeaf(subnodeMask, state.Level);
                    decision = subdivisionDecider.Evaluate();
                    if (decision == SubdivisionDecision.Cull) return 0;
                } else {
                    state.Data.Address = state.ParentData.Address;
                    subnodeMask = 255;
                }
                
                if (decision == SubdivisionDecision.Render) {
                    float dilationUpscale = 1 << state.Level;
                    renderer.RootAddress = state.Data.Address;
                    renderer.MaxLevel = Math.Max(maxLevel - state.Level, 0);
                    renderer.AbsoluteDilation = Math.Max(AbsoluteDilation * dilationUpscale, distortion * DistortionAbsoluteDilation);
                    renderer.RelativeDilation = Math.Max(RelativeDilation * dilationUpscale, distortion * DistortionRelativeDilation);
                    
                    var result = renderer.Render(!reuseStencil);
                    if (result == OctreeRenderer.Result.Rendered) reuseStencil = isAffine;
                    if (result >= OctreeRenderer.Result.Culled) return 0;
                    
                    subdivisionDecider.IsTooBig = (result == OctreeRenderer.Result.TooBig);
                    subdivisionDecider.IsTooClose = (result == OctreeRenderer.Result.TooClose);
                    decision = subdivisionDecider.Evaluate();
                    if (decision == SubdivisionDecision.Cull) return 0;
                }
                
                return subdivisionDecider.IsLeaf ? (byte)255 : subnodeMask;
            }
            
            private static float CageToMatrix(ProjectedVertex[] cage, ref Matrix4x4 matrix) {
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
                
                // Theoretically, checking the distortion of any 2 axes should be enough?
                float distortion, maxDistortion = 0f;
                distortion = XMinX + XMaxX; if (distortion < 0f) distortion = -distortion;
                if (distortion > maxDistortion) maxDistortion = distortion;
                distortion = XMinY + XMaxY; if (distortion < 0f) distortion = -distortion;
                if (distortion > maxDistortion) maxDistortion = distortion;
                distortion = YMinX + YMaxX; if (distortion < 0f) distortion = -distortion;
                if (distortion > maxDistortion) maxDistortion = distortion;
                distortion = YMinY + YMaxY; if (distortion < 0f) distortion = -distortion;
                if (distortion > maxDistortion) maxDistortion = distortion;
                distortion = ZMinX + ZMaxX; if (distortion < 0f) distortion = -distortion;
                if (distortion > maxDistortion) maxDistortion = distortion;
                distortion = ZMinY + ZMaxY; if (distortion < 0f) distortion = -distortion;
                if (distortion > maxDistortion) maxDistortion = distortion;
                
                return maxDistortion;
            }
            
            private bool IsCube() {
                return (Shape == SplatShape.Cube);
            }
            private bool IsLeaf(byte mask, int level) {
                return (mask == 0) | (level >= maxLevel);
            }
            private bool IsTooClose(float z) {
                return (z <= EffectiveNear);
            }
            private bool IsTooBig(float sizeX, float sizeY) {
                return (sizeX >= OctreeRenderer.MaxSizeInPixels) || (sizeY >= OctreeRenderer.MaxSizeInPixels);
            }
            private bool IsDistorted(float distortion) {
                return (distortion > MaxDistortion);
            }
            
            private enum SubdivisionDecision {
                Cull, Subdivide, Render
            }
            
            private struct SubdivisionDecider {
                public bool IsCube;
                public bool IsLeaf;
                public bool IsTooClose;
                public bool IsTooBig;
                public bool IsDistorted;
                
                // The "out of viewport" condition is handled elsewhere
                public SubdivisionDecision Evaluate() {
                    if (IsTooClose) return IsLeaf ? SubdivisionDecision.Cull : SubdivisionDecision.Subdivide;
                    if (IsTooBig) return SubdivisionDecision.Subdivide;
                    if (IsDistorted & (IsCube | !IsLeaf)) return SubdivisionDecision.Subdivide;
                    return SubdivisionDecision.Render;
                }
            }
        }
    }
}
