using Godot;
using System.Runtime.InteropServices;

namespace OctreeSplatting.GodotDemo {
	public class OctreeDemo : TextureRect {
		private OctreeSplatting.Demo.DemoController demoController;
		
		private int width = 640;
		private int height = 480;
		private int bytesPerPixel = 4;
		private Image.Format imageFormat = Image.Format.Rgba8;
		
		private Image image;
		private ImageTexture imageTexture;
		private byte[] colorbuffer;
		
		private Vector2 mouseSpeed;
		
		public override void _Ready() {
			OS.SetWindowSize(new Vector2(width, height));
			
			colorbuffer = new byte[width*height*bytesPerPixel];
			
			image = new Image();
			image.CreateFromData(width, height, false, imageFormat, colorbuffer);
			
			imageTexture = new ImageTexture();
			imageTexture.CreateFromImage(image);
			
			this.Texture = imageTexture;
			
			InitializeScene();
		}

		public override void _Input(InputEvent inputEvent) {
			if (inputEvent is InputEventMouseMotion mouseEvent) {
				mouseSpeed = mouseEvent.Speed;
			}
		}

		public override void _Process(float deltaTime) {
			ProcessInput(deltaTime);
			
			UpdateView();
			
			image.CreateFromData(width, height, false, imageFormat, colorbuffer);
			imageTexture.SetData(image);
			
			mouseSpeed = new Vector2(0, 0);
		}

		private void InitializeScene() {
			var octree = LoadOctree($"../../Unity/OctreeSplatting/Assets/Resources/DemoOctree.bytes");
			var characterOctree = LoadOctree($"../../Unity/OctreeSplatting/Assets/Resources/CharacterOctree.bytes");

			if (octree == null) {
				GD.Print("ERROR: DemoOctree dataset not found!");
				return;
			}

			demoController = new OctreeSplatting.Demo.DemoController(octree, characterOctree);
			
			demoController.Resize(width, height);
		}

		private void UpdateView() {
			if (demoController == null) return;

			demoController.RenderFrame();

			var (sizeX, sizeY, pixels) = demoController.GetImageData();

			CopyToBytes(pixels, colorbuffer);
		}

		private void ProcessInput(float deltaTime) {
			if (demoController == null) return;


			if (Input.IsActionJustPressed("ui_cancel")) {
				GetTree().Notification(MainLoop.NotificationWmQuitRequest);
			}

			if (Input.IsActionJustPressed("demo_shape_point")) {
				demoController.Shape = SplatShape.Point;
			} else if (Input.IsActionJustPressed("demo_shape_rect")) {
				demoController.Shape = SplatShape.Rectangle;
			} else if (Input.IsActionJustPressed("demo_shape_square")) {
				demoController.Shape = SplatShape.Square;
			} else if (Input.IsActionJustPressed("demo_shape_circle")) {
				demoController.Shape = SplatShape.Circle;
			} else if (Input.IsActionJustPressed("demo_shape_cube")) {
				demoController.Shape = SplatShape.Cube;
			}

			if (Input.IsActionJustPressed("demo_switch_ortho")) {
				demoController.SwitchToOrthographic();
			}

			if (Input.IsActionJustPressed("demo_switch_persp")) {
				demoController.SwitchToPerspective();
			}

			if (Input.IsActionJustPressed("demo_use_map_at_3")) {
				demoController.UseMapAt3 = !demoController.UseMapAt3;
			}

			if (Input.IsActionJustPressed("demo_show_bounds")) {
				demoController.ShowBounds = !demoController.ShowBounds;
			}

			if (Input.IsActionJustPressed("demo_use_upscaling")) {
				demoController.UseUpscaling = !demoController.UseUpscaling;
			}

			demoController.ThreadCount += IntKeyPressed("demo_threads_incr") - IntKeyPressed("demo_threads_decr");

			demoController.MaxLevel += IntKeyPressed("demo_level_incr") - IntKeyPressed("demo_level_decr");
			demoController.MaxLevel = System.Math.Max(demoController.MaxLevel, -1);

			demoController.RelativeDilation += (IntKeyDown("demo_dilation_incr") - IntKeyDown("demo_dilation_decr")) / (float)(1 << 14);
			demoController.RelativeDilation = System.Math.Max(demoController.RelativeDilation, 0);

			demoController.EffectiveNear += (IntKeyDown("demo_near_incr") - IntKeyDown("demo_near_decr")) * 0.00001f;
			demoController.EffectiveNear = System.Math.Min(System.Math.Max(demoController.EffectiveNear, 0), 1);

			var movement = new Vector3(
				IntKeyDown("demo_move_x_pos") - IntKeyDown("demo_move_x_neg"),
				IntKeyDown("demo_move_y_pos") - IntKeyDown("demo_move_y_neg"),
				IntKeyDown("demo_move_z_pos") - IntKeyDown("demo_move_z_neg")
			);

			if (Input.IsActionPressed("demo_fast_speed")) {
				movement *= 10f;
			} else if (Input.IsActionPressed("demo_slow_speed")) {
				movement *= 0.05f;
			}

			const float cameraSpeed = 1.5f;
			movement *= cameraSpeed * deltaTime;
			demoController.MoveCamera(movement.x, movement.y, movement.z);

			var delta = mouseSpeed;

			if (Input.IsActionPressed("demo_perspective")) {
				demoController.Perspective = demoController.Perspective - delta.y * 0.001f;
			} else if (Input.IsActionPressed("demo_rotate")) {
				const float sensitivity = 0.4f;
				demoController.CameraYaw -= delta.x * sensitivity * deltaTime;
				demoController.CameraPitch -= delta.y * sensitivity * deltaTime;
			}

			// Mouse wheel only sends released events
			demoController.Zoom += IntKeyReleased("demo_zoom_incr") - IntKeyReleased("demo_zoom_decr");

			int IntKeyDown(string key) => Input.IsActionPressed(key) ? 1 : 0;
			int IntKeyPressed(string key) => Input.IsActionJustPressed(key) ? 1 : 0;
			int IntKeyReleased(string key) => Input.IsActionJustReleased(key) ? 1 : 0;
		}

		private OctreeSplatting.OctreeNode[] LoadOctree(string path) {
			if (!System.IO.File.Exists(path)) return null;
			var bytes = System.IO.File.ReadAllBytes(path);
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

		private static void CopyToBytes<T>(T[] source, byte[] bytes) where T : struct {
			var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
			try {
				Marshal.Copy(handle.AddrOfPinnedObject(), bytes, 0, bytes.Length);
			} finally {
				if (handle.IsAllocated) handle.Free();
			}
		}
	}
}
