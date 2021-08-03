// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.Numerics;

namespace OctreeSplatting.Demo {
    public class Object3D {
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
        
        private void UpdateMatrix() {
            matrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation);
            matrix.Translation = position;
            Matrix4x4.Invert(matrix, out inverse);
            updated = true;
        }
    }
}
