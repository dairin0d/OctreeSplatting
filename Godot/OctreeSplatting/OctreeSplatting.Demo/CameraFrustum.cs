// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System;
using System.Numerics;

namespace OctreeSplatting.Demo {
    public class CameraFrustum {
        private bool recalculate = true;

        private Vector2 aperture = Vector2.One;
        public Vector2 Aperture {
            get { return aperture; }
            set { aperture = value; recalculate = true; }
        }

        private Vector3 focus = Vector3.UnitZ;
        public Vector3 Focus {
            get { return focus; }
            set { focus = value; recalculate = true; }
        }

        private float near = 0.1f;
        public float Near {
            get { return near; }
            set { near = value; recalculate = true; }
        }

        private float far = 1000f;
        public float Far {
            get { return far; }
            set { far = value; recalculate = true; }
        }

        private float perspective = 1f;
        public float Perspective {
            get { return perspective; }
            set { perspective = Math.Min(Math.Max(value, 0f), 1f); recalculate = true; }
        }

        private Matrix4x4 matrix;
        public Matrix4x4 Matrix {
            get {
                if (recalculate) {
                    if (perspective == 0f) {
                        CalculateMatrix(out matrix, false);
                    } else if (perspective == 1f) {
                        CalculateMatrix(out matrix, true);
                    } else {
                        CalculateMatrix(out var ortho, false);
                        CalculateMatrix(out var persp, true);
                        matrix = Matrix4x4.Lerp(ortho, persp, perspective);
                    }
                    recalculate = false;
                }
                return matrix;
            }
        }

        private void CalculateMatrix(out Matrix4x4 result, bool usePerspective) {
            float nearToFocus = usePerspective ? near / focus.Z : 1f;
            float x = focus.X * nearToFocus;
            float y = focus.Y * nearToFocus;
            float w2 = (aperture.X * nearToFocus) * 0.5f;
            float h2 = (aperture.Y * nearToFocus) * 0.5f;
            float left = x - w2, right = x + w2;
            float bottom = y - h2, top = y + h2;
            
            if (usePerspective) {
                // result = default;
                // result.M11 = (2 * near) / (right - left);
                // result.M22 = (2 * near) / (top - bottom);
                // result.M31 = (right + left) / (right - left);
                // result.M32 = (top + bottom) / (top - bottom);
                // result.M33 = -(far + near) / (far - near);
                // result.M34 = -1;
                // result.M43 = -(2 * far * near) / (far - near);
                result = Matrix4x4.CreatePerspectiveOffCenter(left, right, bottom, top, near, far);
            } else {
                // result = Matrix4x4.Identity;
                // result.M11 = 2 / (right - left);
                // result.M22 = 2 / (top - bottom);
                // result.M33 = -2 / (far - near);
                // result.M41 = -(right + left) / (right - left);
                // result.M42 = -(top + bottom) / (top - bottom);
                // result.M43 = -(far + near) / (far - near);
                result = Matrix4x4.CreateOrthographicOffCenter(left, right, bottom, top, near, far);
            }
        }
    }
}
