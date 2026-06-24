using Godot;

namespace TopographicCameraShader.Demo;

// Root of the standalone marketing scene: a wide topographic landmass viewed
// straight down through the Classic Ink compositor. Sizes and centers the window
// to the terrain's 2.5:1 banner aspect on launch so the top-down orthographic
// camera frames the whole map with no surrounding background, ready to screenshot
// for the README.
public partial class BigTerrainMap : Node3D
{
    private static readonly Vector2I WindowSize = new(2000, 800);

    public override void _Ready()
    {
        var screen = DisplayServer.ScreenGetSize();
        DisplayServer.WindowSetSize(WindowSize);
        DisplayServer.WindowSetPosition((screen - WindowSize) / 2);
    }
}
