using Godot;
using OctreeSplatting;
using System;

public partial class FpsCounter : Label {
	public override void _Process(double deltaTime) {
		var fps = Engine.GetFramesPerSecond();
		var ms = (int)Math.Ceiling(1000f / fps);
		Text = Timing.Report+$"FPS:{fps} ({ms} ms)";
	}
}
