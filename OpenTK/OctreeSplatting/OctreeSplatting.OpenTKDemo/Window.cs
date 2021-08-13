// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.IO;
using System.Runtime.InteropServices;

using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

#if USE_OPENGL4
using OpenTK.Graphics.OpenGL4;
#else
using OpenTK.Graphics.OpenGL;
#endif

namespace OctreeSplatting.OpenTKDemo {
    public class Window : GameWindow {
        private OctreeSplatting.Demo.DemoController demoController;
        
        private Mesh quad;

        private Shader shader;

        private Texture colorbuffer;

        private string initialTitle;

        private const string NoDatasetMessage = "ERROR: DemoOctree dataset not found!";

        public Window(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
            : base(gameWindowSettings, nativeWindowSettings)
        {
        }

        protected override void OnLoad() {
            base.OnLoad();

            initialTitle = Title;

            var octree = LoadOctree($"../../Unity/OctreeSplatting/Assets/Resources/DemoOctree.bytes");
            var characterOctree = LoadOctree($"../../Unity/OctreeSplatting/Assets/Resources/CharacterOctree.bytes");
            
            if (octree == null) {
                Title = NoDatasetMessage;
                System.Console.WriteLine(NoDatasetMessage);
                return;
            }
            
            demoController = new OctreeSplatting.Demo.DemoController(octree, characterOctree);
            
            colorbuffer = new Texture();
            colorbuffer.Use(TextureUnit.Texture0);
            colorbuffer.SetFilter(false);
            colorbuffer.SetWrap(TextureWrapMode.ClampToEdge);

            shader = Shader.Load("Shaders/shader.vert", "Shaders/shader.frag");
            shader.Use();

            quad = new Mesh(vertices: new float[] {
                -1, -1, 0, 0, 0,
                -1, +1, 0, 0, 1,
                +1, +1, 0, 1, 1,
                +1, -1, 0, 1, 0,
            }, indices: new uint[] {
                0, 3, 1, 1, 3, 2,
            });
            shader.SetAttribute("attribPos", 3, VertexAttribPointerType.Float, false, 5*sizeof(float), 0*sizeof(float));
            shader.SetAttribute("attribUV", 2, VertexAttribPointerType.Float, false, 5*sizeof(float), 3*sizeof(float));

            // Make the mouse cursor invisible and captured so we can have proper FPS-camera movement.
            CursorGrabbed = true;
        }

        protected override void OnRenderFrame(FrameEventArgs e) {
            base.OnRenderFrame(e);
            
            if (demoController == null) return;

            GL.ClearColor(0, 0, 0, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);

            shader.Use();

            shader.SetUniform("texture0", 0);

            demoController.RenderFrame();
            
            var (sizeX, sizeY, pixels) = demoController.GetImageData();
            
            colorbuffer.Use(TextureUnit.Texture0);
            colorbuffer.SetPixels(PixelInternalFormat.Rgba, sizeX, sizeY,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels, false);
            
            shader.SetUniform("mvpMatrix", OpenTK.Mathematics.Matrix4.CreateOrthographic(2, 2, -1, 1));
            quad.Use();
            quad.Draw();

            //Title = $"{initialTitle}: {demoController.TimeInfo}; {demoController.ViewInfo}";
            Title = $"{initialTitle}: {demoController.TimeInfo}; P={demoController.Perspective}";

            SwapBuffers();
        }

        protected override void OnUpdateFrame(FrameEventArgs e) {
            base.OnUpdateFrame(e);
            
            if (demoController == null) return;

            if (!IsFocused) return;

            var keyboard = KeyboardState;

            if (keyboard.IsKeyPressed(Keys.Escape)) {
                Close();
                return;
            }
            
            if (keyboard.IsKeyPressed(Keys.F1)) {
                demoController.Shape = SplatShape.Point;
            } else if (keyboard.IsKeyPressed(Keys.F2)) {
                demoController.Shape = SplatShape.Rectangle;
            } else if (keyboard.IsKeyPressed(Keys.F3)) {
                demoController.Shape = SplatShape.Square;
            } else if (keyboard.IsKeyPressed(Keys.F4)) {
                demoController.Shape = SplatShape.Circle;
            } else if (keyboard.IsKeyPressed(Keys.F5)) {
                demoController.Shape = SplatShape.Cube;
            }

            if (keyboard.IsKeyPressed(Keys.F12)) {
                demoController.ShowBounds = !demoController.ShowBounds;
            }

            if (keyboard.IsKeyPressed(Keys.Enter)) {
                demoController.UseUpscaling = !demoController.UseUpscaling;
            }

            demoController.ThreadCount += IntKeyPressed(keyboard, Keys.Period) - IntKeyPressed(keyboard, Keys.Comma);

            demoController.MaxLevel += IntKeyPressed(keyboard, Keys.RightBracket) - IntKeyPressed(keyboard, Keys.LeftBracket);
            demoController.MaxLevel = System.Math.Max(demoController.MaxLevel, -1);

            demoController.RelativeDilation += (IntKeyDown(keyboard, Keys.Equal) - IntKeyDown(keyboard, Keys.Minus)) / (float)(1 << 14);
            demoController.RelativeDilation = System.Math.Max(demoController.RelativeDilation, 0);
            
            demoController.EffectiveNear += (IntKeyDown(keyboard, Keys.Home) - IntKeyDown(keyboard, Keys.End)) * 0.00001f;
            demoController.EffectiveNear = System.Math.Min(System.Math.Max(demoController.EffectiveNear, 0), 1);

            var movement = new Vector3(
                IntKeyDown(keyboard, Keys.A) - IntKeyDown(keyboard, Keys.D),
                IntKeyDown(keyboard, Keys.R) - IntKeyDown(keyboard, Keys.F),
                IntKeyDown(keyboard, Keys.W) - IntKeyDown(keyboard, Keys.S)
            );

            if (keyboard.IsKeyDown(Keys.LeftControl)) {
                movement *= 10f;
            } else if (keyboard.IsKeyDown(Keys.LeftShift)) {
                movement *= 0.05f;
            }

            const float cameraSpeed = 1.5f;
            movement *= cameraSpeed * (float)e.Time;
            demoController.MoveCamera(movement.X, movement.Y, movement.Z);

            var mouse = MouseState;
            var delta = mouse.Delta;

            if (keyboard.IsKeyDown(Keys.LeftAlt) | keyboard.IsKeyDown(Keys.RightAlt)) {
                demoController.Perspective = demoController.Perspective - delta.Y * 0.001f;
            } else if (mouse.IsButtonDown(MouseButton.Right)) {
                const float sensitivity = 0.2f;
                demoController.CameraYaw -= delta.X * sensitivity;
                demoController.CameraPitch -= delta.Y * sensitivity;
            }
            
            int IntKeyDown(KeyboardState keyboardState, Keys key) => keyboardState.IsKeyDown(key) ? 1 : 0;
            int IntKeyPressed(KeyboardState keyboardState, Keys key) => keyboardState.IsKeyPressed(key) ? 1 : 0;
        }
        
        protected override void OnMouseWheel(MouseWheelEventArgs e) {
            base.OnMouseWheel(e);
            
            if (demoController == null) return;

            demoController.Zoom += (int)e.OffsetY;
        }

        protected override void OnResize(ResizeEventArgs e) {
            base.OnResize(e);
            
            if (demoController == null) return;

            GL.Viewport(0, 0, Size.X, Size.Y);
            
            demoController.Resize(Size.X, Size.Y);
        }
        
        private OctreeSplatting.OctreeNode[] LoadOctree(string path) {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return FromBytes<OctreeSplatting.OctreeNode>(bytes);
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
}
