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
            
            if (octree == null) {
                Title = NoDatasetMessage;
                System.Console.WriteLine(NoDatasetMessage);
                return;
            }
            
            demoController = new OctreeSplatting.Demo.DemoController(octree);
            
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

            var timeInfo = $"{demoController.FrameTime} ({demoController.AverageFrameTime:0.0}) ms/frame";
            Title = $"{initialTitle}: {timeInfo}; {demoController.ViewInfo}";

            SwapBuffers();
        }

        protected override void OnUpdateFrame(FrameEventArgs e) {
            base.OnUpdateFrame(e);
            
            if (demoController == null) return;

            if (!IsFocused) return;

            var keyboard = KeyboardState;

            if (keyboard.IsKeyDown(Keys.Escape)) {
                Close();
                return;
            }

            var movement = new Vector3(
                IntKeyDown(keyboard, Keys.A) - IntKeyDown(keyboard, Keys.D),
                IntKeyDown(keyboard, Keys.R) - IntKeyDown(keyboard, Keys.F),
                IntKeyDown(keyboard, Keys.W) - IntKeyDown(keyboard, Keys.S)
            );

            const float cameraSpeed = 1.5f;
            movement *= cameraSpeed * (float)e.Time;
            demoController.MoveCamera(movement.X, movement.Y, movement.Z);

            var mouse = MouseState;
            var delta = mouse.Delta;

            if (keyboard.IsKeyDown(Keys.LeftAlt) | keyboard.IsKeyDown(Keys.RightAlt)) {
                demoController.Perspective = demoController.Perspective - delta.Y * 0.01f;
            } else if (mouse.IsButtonDown(MouseButton.Right)) {
                const float sensitivity = 0.2f;
                demoController.CameraYaw -= delta.X * sensitivity;
                demoController.CameraPitch -= delta.Y * sensitivity;
            }
            
            int IntKeyDown(KeyboardState keyboardState, Keys key) => keyboardState.IsKeyDown(key) ? 1 : 0;
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
