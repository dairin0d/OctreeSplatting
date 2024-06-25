// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.Numerics;

namespace OctreeSplatting.Demo {
    public class Object3D {
        public OctreeNode[] Octree;
        public readonly Vector3[] Cage;
        
        public readonly ProjectedVertex[] ProjectedCage;
        public Vector3 ProjectedMin, ProjectedMax;
        
        private Vector3 position = Vector3.Zero;
        private Quaternion rotation = Quaternion.Identity;
        private Vector3 scale = Vector3.One;
        private bool updated = false;
        private Matrix4x4 matrix;
        private Matrix4x4 inverse;

        public Vector3 Position {
            get => position;
            set { position = value; updated = false; }
        }
        public Quaternion Rotation {
            get => rotation;
            set { rotation = value; updated = false; }
        }
        public Vector3 Scale {
            get => scale;
            set { scale = value; updated = false; }
        }
        public Matrix4x4 Matrix {
            get {
                if (!updated) UpdateMatrix();
                return matrix;
            }
        }
        public Matrix4x4 Inverse {
            get {
                if (!updated) UpdateMatrix();
                return inverse;
            }
        }
        
        public Vector3 AxisX {
            get {
                if (!updated) UpdateMatrix();
                return new Vector3(matrix.M11, matrix.M12, matrix.M13);
            }
        }
        public Vector3 AxisY {
            get {
                if (!updated) UpdateMatrix();
                return new Vector3(matrix.M21, matrix.M22, matrix.M23);
            }
        }
        public Vector3 AxisZ {
            get {
                if (!updated) UpdateMatrix();
                return new Vector3(matrix.M31, matrix.M32, matrix.M33);
            }
        }
        
        public Object3D(OctreeNode[] octree = null) {
            Octree = octree;
            Cage = new Vector3[8];
            ProjectedCage = new ProjectedVertex[8];
            ResetCage();
        }
        
        public void ResetCage() {
            Cage[0] = new Vector3(-1, -1, -1);
            Cage[1] = new Vector3(+1, -1, -1);
            Cage[2] = new Vector3(-1, +1, -1);
            Cage[3] = new Vector3(+1, +1, -1);
            Cage[4] = new Vector3(-1, -1, +1);
            Cage[5] = new Vector3(+1, -1, +1);
            Cage[6] = new Vector3(-1, +1, +1);
            Cage[7] = new Vector3(+1, +1, +1);
        }
        
        public bool IsAffine(float tolerance = 1e-8f) {
            var TMinX = Cage[0].X;
            var XMinX = Cage[1].X - TMinX;
            var YMinX = Cage[2].X - TMinX;
            var ZMinX = Cage[4].X - TMinX;
            var TMaxX = Cage[7].X;
            var XMaxX = Cage[6].X - TMaxX;
            var YMaxX = Cage[5].X - TMaxX;
            var ZMaxX = Cage[3].X - TMaxX;
            
            var TMinY = Cage[0].Y;
            var XMinY = Cage[1].Y - TMinY;
            var YMinY = Cage[2].Y - TMinY;
            var ZMinY = Cage[4].Y - TMinY;
            var TMaxY = Cage[7].Y;
            var XMaxY = Cage[6].Y - TMaxY;
            var YMaxY = Cage[5].Y - TMaxY;
            var ZMaxY = Cage[3].Y - TMaxY;
            
            var TMinZ = Cage[0].Z;
            var XMinZ = Cage[1].Z - TMinZ;
            var YMinZ = Cage[2].Z - TMinZ;
            var ZMinZ = Cage[4].Z - TMinZ;
            var TMaxZ = Cage[7].Z;
            var XMaxZ = Cage[6].Z - TMaxZ;
            var YMaxZ = Cage[5].Z - TMaxZ;
            var ZMaxZ = Cage[3].Z - TMaxZ;
            
            float distortion, maxDistortion = 0f;
            distortion = XMinX + XMaxX; if (distortion < 0f) distortion = -distortion;
            if (distortion > maxDistortion) maxDistortion = distortion;
            distortion = XMinY + XMaxY; if (distortion < 0f) distortion = -distortion;
            if (distortion > maxDistortion) maxDistortion = distortion;
            distortion = XMinZ + XMaxZ; if (distortion < 0f) distortion = -distortion;
            if (distortion > maxDistortion) maxDistortion = distortion;
            distortion = YMinX + YMaxX; if (distortion < 0f) distortion = -distortion;
            if (distortion > maxDistortion) maxDistortion = distortion;
            distortion = YMinY + YMaxY; if (distortion < 0f) distortion = -distortion;
            if (distortion > maxDistortion) maxDistortion = distortion;
            distortion = YMinZ + YMaxZ; if (distortion < 0f) distortion = -distortion;
            if (distortion > maxDistortion) maxDistortion = distortion;
            distortion = ZMinX + ZMaxX; if (distortion < 0f) distortion = -distortion;
            if (distortion > maxDistortion) maxDistortion = distortion;
            distortion = ZMinY + ZMaxY; if (distortion < 0f) distortion = -distortion;
            if (distortion > maxDistortion) maxDistortion = distortion;
            distortion = ZMinZ + ZMaxZ; if (distortion < 0f) distortion = -distortion;
            if (distortion > maxDistortion) maxDistortion = distortion;
            
            return maxDistortion <= tolerance;
        }
        
        private void UpdateMatrix() {
            matrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation);
            matrix.Translation = position;
            Matrix4x4.Invert(matrix, out inverse);
            updated = true;
        }
    }
}
