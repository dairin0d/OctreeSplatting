// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System;
using System.Numerics;
using System.Collections.Generic;

namespace OctreeSplatting.Demo {
    public class DemoController {
        private Object3D player;
        private Object3D playerCamera;
        private CameraFrustum cameraFrustum;
        
        private float ApertureSize => (float)Math.Pow(2, -zoomSteps * zoomFactor);
        private float DistanceScale => (float)Math.Pow(2, distanceSteps * zoomFactor);
        
        private float zoomFactor = 0.125f;
        private int distanceSteps = 16;
        private int zoomSteps = -12;
        private float cameraPitch = -37;
        private float cameraYaw = -47;
        
        private Quaternion modelRotation = Quaternion.CreateFromYawPitchRoll(0, (float)Math.PI, 0);
        
        private int gridExtent = 2;
        private float gridOffset = 1.2f;

        private List<(Object3D, OctreeSplatting.OctreeNode[])> models = new List<(Object3D, OctreeSplatting.OctreeNode[])>();

        private List<(Object3D, OctreeSplatting.OctreeNode[], Matrix4x4)> sortedModels = new List<(Object3D, OctreeSplatting.OctreeNode[], Matrix4x4)>();

        private OctreeSplatting.OctreeRenderer renderer;
        private OctreeSplatting.Renderbuffer renderbuffer;

        private System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        private double averageFrameTime;
        private long frameCount;
        private Dictionary<int, int> timeHistogram = new Dictionary<int, int>();
        private int timeCountMax;
        private int mostProbableTime;

        private OctreeSplatting.Color32 background = new OctreeSplatting.Color32 {R = 50, G = 80, B = 80, A = 255};

        public string ViewInfo => $"P={(int)cameraPitch}, Y={(int)cameraYaw}, Z={zoomSteps}";
        public string TimeInfo => $"{FrameTime} ({MostProbableTime}) ms/frame";
        public int FrameTime => (int)stopwatch.ElapsedMilliseconds;
        public double AverageFrameTime => averageFrameTime;
        public int MostProbableTime => mostProbableTime;

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

        public DemoController(OctreeSplatting.OctreeNode[] octree) {
            renderer = new OctreeSplatting.OctreeRenderer();
            //renderer.MaxLevel = 0;
            
            renderbuffer = new OctreeSplatting.Renderbuffer();
            
            player = new Object3D();
            
            playerCamera = new Object3D();
            cameraFrustum = new CameraFrustum();
            cameraFrustum.Perspective = 0;
            cameraFrustum.Far = 10000;

            for (int ix = -gridExtent; ix <= gridExtent; ix++) {
                for (int iz = -gridExtent; iz <= gridExtent; iz++) {
                    var obj3D = new Object3D();
                    obj3D.Position = new Vector3(ix, 0, iz) * gridOffset;
                    obj3D.Rotation = modelRotation;
                    models.Add((obj3D, octree));
                }
            }
        }
        
        public (int, int, OctreeSplatting.Color32[]) GetImageData() {
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

            renderbuffer.Begin(background);
            DrawOctrees(playerCamera.Inverse, cameraFrustum.Matrix);
            renderbuffer.End();
            
            stopwatch.Stop();
            
            UpdateTimeInfo();
        }

        private void UpdateTimeInfo() {
            frameCount++;
            averageFrameTime = (averageFrameTime * (frameCount-1) + stopwatch.ElapsedMilliseconds) / frameCount;
            
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
            renderer.Viewport.MinX = 0;
            renderer.Viewport.MinY = 0;
            renderer.Viewport.MaxX = renderbuffer.SizeX - 1;
            renderer.Viewport.MaxY = renderbuffer.SizeY - 1;
            renderer.BufferShift = renderbuffer.ShiftX;
            renderer.Pixels = renderbuffer.DataPixels;
            
            var near = cameraFrustum.Near;
            var far = cameraFrustum.Far;
            var projectionOffset = new Vector3(1, 1, -near);
            var projectionScale = new Vector3(
                0.5f * renderbuffer.SizeX,
                0.5f * renderbuffer.SizeY,
                0.5f * renderbuffer.SizeZ / (far - near)
            );
            
            sortedModels.Clear();
            
            foreach (var (object3d, octree) in models) {
                if (octree == null) continue;
                
                var matrixMV = object3d.Matrix * viewMatrix;
                
                // make sure that forward direction is positive
                var columnZ = -(new Vector4(matrixMV.M13, matrixMV.M23, matrixMV.M33, matrixMV.M43));
                var extentZ = Math.Abs(columnZ.X) + Math.Abs(columnZ.Y) + Math.Abs(columnZ.Z);
                var minZ = columnZ.W - extentZ;
                var maxZ = columnZ.W + extentZ;
                if ((maxZ <= near) | (minZ >= far)) continue;
                
                sortedModels.Add((object3d, octree, matrixMV));
            }
            
            sortedModels.Sort((itemA, itemB) => {
                return -itemA.Item3.M43.CompareTo(itemB.Item3.M43);
            });
            
            for (int objectID = 0; objectID < sortedModels.Count; objectID++) {
                var (object3d, octree, matrixMV) = sortedModels[objectID];
                
                var matrixMVP = matrixMV * projectionMatrix;
                
                CalculateScreenSpaceMatrix(in matrixMV, in matrixMVP,
                    projectionOffset, projectionScale, ref renderer.Matrix);
                renderer.Octree = octree;
                renderer.RootAddress = 0;
                
                renderer.Render();
            }
        }
        
        private void CalculateScreenSpaceMatrix(in Matrix4x4 matrixMV, in Matrix4x4 matrixMVP,
            Vector3 offset, Vector3 scale, ref Matrix4x4 result)
        {
            var row1 = GetRow(matrixMVP, 1);
            var row2 = GetRow(matrixMVP, 2);
            var row3 = GetRow(matrixMVP, 3);
            var row4 = GetRow(matrixMVP, 4);
            var origin = row4; origin /= origin.W;
            var axisXP = row4 + row1; axisXP /= axisXP.W;
            var axisYP = row4 + row2; axisYP /= axisYP.W;
            var axisZP = row4 + row3; axisZP /= axisZP.W;
            var axisXN = row4 - row1; axisXN /= axisXN.W;
            var axisYN = row4 - row2; axisYN /= axisYN.W;
            var axisZN = row4 - row3; axisZN /= axisZN.W;
            origin.Z = -(matrixMV.M43);
            axisXP.Z = -(matrixMV.M43 + matrixMV.M13);
            axisYP.Z = -(matrixMV.M43 + matrixMV.M23);
            axisZP.Z = -(matrixMV.M43 + matrixMV.M33);
            axisXN.Z = -(matrixMV.M43 - matrixMV.M13);
            axisYN.Z = -(matrixMV.M43 - matrixMV.M23);
            axisZN.Z = -(matrixMV.M43 - matrixMV.M33);
            
            var T = To3D(origin);
            var X = -To3D(axisXP - axisXN) * 0.5f;
            var Y = -To3D(axisYP - axisYN) * 0.5f;
            var Z = -To3D(axisZP - axisZN) * 0.5f;
            
            T = (T + offset) * scale;
            X *= scale;
            Y *= scale;
            Z *= scale;
            
            result.M11 = X.X; result.M12 = X.Y; result.M13 = X.Z;
            result.M21 = Y.X; result.M22 = Y.Y; result.M23 = Y.Z;
            result.M31 = Z.X; result.M32 = Z.Y; result.M33 = Z.Z;
            result.M41 = T.X; result.M42 = T.Y; result.M43 = T.Z;
        }
        
        private Vector4 GetRow(in Matrix4x4 matrix, int row) {
            switch (row) {
                case 1: return new Vector4(matrix.M11, matrix.M12, matrix.M13, matrix.M14);
                case 2: return new Vector4(matrix.M21, matrix.M22, matrix.M23, matrix.M24);
                case 3: return new Vector4(matrix.M31, matrix.M32, matrix.M33, matrix.M34);
                case 4: return new Vector4(matrix.M41, matrix.M42, matrix.M43, matrix.M44);
            }
            return default;
        }
        
        private Vector3 To3D(Vector4 vector) {
            return new Vector3(vector.X, vector.Y, vector.Z);
        }
        
        private void UpdateCameraAperture() {
            var scale = ApertureSize / Math.Max(renderbuffer.SizeX, renderbuffer.SizeY);
            cameraFrustum.Aperture = new Vector2(renderbuffer.SizeX * scale, renderbuffer.SizeY * scale);
        }
    }
}
