// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.Numerics;

namespace OctreeSplatting.Demo {
    public class Object3D {
        public OctreeNode[] Octree;
        public Vector3[] Cage;
        public Matrix4x4 RenderingMatrix;
        
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
        
        public Object3D(OctreeNode[] octree = null, Vector3[] cage = null) {
            Octree = octree;
            Cage = cage;
            
            if (Cage == null) ResetCage();
        }
        
        public void ResetCage() {
            if ((Cage == null) || (Cage.Length < 8)) {
                Cage = new Vector3[8];
            }
            
            Cage[0] = new Vector3(-1, -1, -1);
            Cage[1] = new Vector3(+1, -1, -1);
            Cage[2] = new Vector3(-1, +1, -1);
            Cage[3] = new Vector3(+1, +1, -1);
            Cage[4] = new Vector3(-1, -1, +1);
            Cage[5] = new Vector3(+1, -1, +1);
            Cage[6] = new Vector3(-1, +1, +1);
            Cage[7] = new Vector3(+1, +1, +1);
        }
        
        private void UpdateMatrix() {
            matrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation);
            matrix.Translation = position;
            Matrix4x4.Invert(matrix, out inverse);
            updated = true;
        }
    }
}
