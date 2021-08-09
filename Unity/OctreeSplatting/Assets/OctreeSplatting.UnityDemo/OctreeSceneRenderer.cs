// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.Runtime.InteropServices;
using UnityEngine;

namespace OctreeSplatting.UnityDemo {
    public class OctreeSceneRenderer : MonoBehaviour {
        private OctreeSplatting.Demo.DemoController demoController;
        
        private Texture2D colorbuffer;
        
        private Camera cam;
        private Material material;
        
        private Vector3 lastMousePosition;
        
        private const string NoDatasetMessage = "ERROR: DemoOctree dataset not found!";

        private void Start() {
            if (!Application.isEditor) Screen.SetResolution(640, 480, false);
            
            var asset = Resources.Load<TextAsset>("DemoOctree");
            
            if (!asset) {
                Debug.LogError(NoDatasetMessage);
                return;
            }
            
            var octree = FromBytes<OctreeSplatting.OctreeNode>(asset.bytes);
            demoController = new OctreeSplatting.Demo.DemoController(octree);
            
            cam = GetComponent<Camera>();
            
            material = new Material(Shader.Find("UI/Default"));
            
            lastMousePosition = Input.mousePosition;
        }

        private void Update() {
            if (demoController == null) return;
            
            demoController.Resize(cam.pixelWidth, cam.pixelHeight);
            ResizeTexture(ref colorbuffer, cam.pixelWidth, cam.pixelHeight);
            
            ProcessInput();
            
            demoController.RenderFrame();
            
            var (sizeX, sizeY, pixels) = demoController.GetImageData();
            
            colorbuffer.SetPixelData(pixels, 0);
            colorbuffer.Apply(false);
        }
        
        private void OnPostRender() {
            if (demoController == null) return;
            
            material.mainTexture = colorbuffer;
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
            
            if (demoController == null) {
                GUI.Label(new Rect(x, y, Screen.width, lineHeight), NoDatasetMessage);
                return;
            }
            
            int camW = cam.pixelWidth, camH = cam.pixelHeight;
            DrawBox(new Rect(0, 0, panelWidth, lineHeight*2));
            GUI.Label(new Rect(x, y, panelWidth, lineHeight), $"{camW}x{camH}: {demoController.TimeInfo}");
            y += lineHeight;
            GUI.Label(new Rect(x, y, panelWidth, lineHeight), demoController.ViewInfo);
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
        
        private void ProcessInput() {
            var mousePosition = Input.mousePosition;
            var mouseDelta = mousePosition - lastMousePosition;
            lastMousePosition = mousePosition;
            
            var movement = new Vector3(
                IntKeyDown(KeyCode.A) - IntKeyDown(KeyCode.D),
                IntKeyDown(KeyCode.R) - IntKeyDown(KeyCode.F),
                IntKeyDown(KeyCode.W) - IntKeyDown(KeyCode.S)
            );
            
            demoController.ThreadCount += IntKeyPressed(KeyCode.Period) - IntKeyPressed(KeyCode.Comma);
            
            demoController.MaxLevel += IntKeyPressed(KeyCode.RightBracket) - IntKeyPressed(KeyCode.LeftBracket);
            demoController.MaxLevel = Mathf.Max(demoController.MaxLevel, -1);
            
            demoController.RelativeDilation += (IntKeyPressed(KeyCode.Equals) - IntKeyPressed(KeyCode.Minus)) / (float)(1 << 14);
            demoController.RelativeDilation = Mathf.Max(demoController.RelativeDilation, 0);
            
            const float cameraSpeed = 1.5f;
            movement *= cameraSpeed * Time.deltaTime;
            demoController.MoveCamera(movement.x, movement.y, movement.z);
            
            if (Input.GetKey(KeyCode.LeftAlt) | Input.GetKey(KeyCode.RightAlt)) {
                demoController.Perspective = demoController.Perspective - mouseDelta.y * 0.01f;
            } else if (Input.GetMouseButton(1)) {
                const float sensitivity = 0.2f;
                demoController.CameraYaw -= mouseDelta.x * sensitivity;
                demoController.CameraPitch -= -mouseDelta.y * sensitivity;
            }
            
            demoController.Zoom += (int)Input.mouseScrollDelta.y;
            
            int IntKeyDown(KeyCode keyCode) => Input.GetKey(keyCode) ? 1 : 0;
            int IntKeyPressed(KeyCode keyCode) => Input.GetKeyDown(keyCode) ? 1 : 0;
        }
        
        private static void ResizeTexture(ref Texture2D texture, int w, int h) {
            if (texture && (w == texture.width) && (h == texture.height)) return;

            if (texture) Destroy(texture);

            texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
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