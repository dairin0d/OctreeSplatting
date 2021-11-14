using Godot;
using System;

public class FpsCounter : Label
{
	public override void _Process(float deltaTime)
	{
		Text = Engine.GetFramesPerSecond().ToString();
	}
}
