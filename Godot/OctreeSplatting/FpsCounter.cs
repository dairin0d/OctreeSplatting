using Godot;
using System;

public partial class FpsCounter : Label {
	public override void _Process(double deltaTime) {
		Text = Engine.GetFramesPerSecond().ToString();
	}
}
