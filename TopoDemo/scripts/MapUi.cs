using Godot;

namespace TopographicMap.TopoDemo;

// Drives the HUD map: a player-centered minimap crop and a full-screen world
// map toggled with the map key. Both display the same map ViewportTexture,
// fetched from the SubViewport in code so no fragile viewport_path is authored.
public partial class MapUi : Control
{
    [Export] public SubViewport MapViewport;
    [Export] public TextureRect Minimap;
    [Export] public TextureRect WorldMapImage;
    [Export] public Control WorldMapRoot;
    [Export] public Node3D Player;
    [Export] public float TerrainSize = 768.0f;
    [Export] public float MinimapWorldSpan = 200.0f;

    private AtlasTexture _atlas;

    public override void _Ready()
    {
        var mapTexture = MapViewport.GetTexture();
        _atlas = new() { Atlas = mapTexture };
        Minimap.Texture = _atlas;
        WorldMapImage.Texture = mapTexture;
        WorldMapRoot.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_atlas.Atlas == null)
        {
            return;
        }

        var texSize = _atlas.Atlas.GetSize();
        var pos = Player.GlobalPosition;
        // World XZ maps to texture UV. Z maps to V; flip if the minimap scrolls
        // the wrong way during tuning.
        float u = pos.X / TerrainSize + 0.5f;
        float v = pos.Z / TerrainSize + 0.5f;
        float spanPx = MinimapWorldSpan / TerrainSize * texSize.X;
        _atlas.Region = new(
            u * texSize.X - spanPx * 0.5f,
            v * texSize.Y - spanPx * 0.5f,
            spanPx,
            spanPx);
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent.IsActionPressed("toggle_map"))
        {
            WorldMapRoot.Visible = !WorldMapRoot.Visible;
        }
    }
}
