using Godot;
using System;

public partial class FpsCounter : Label {
	public override void _Process(double deltaTime) {
		var fps = Engine.GetFramesPerSecond();
		var ms = (int)Math.Ceiling(1000f / fps);
		Text = $"FPS: {fps} ({ms} ms)";
	}
}
