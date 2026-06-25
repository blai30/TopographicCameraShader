using Godot;

namespace TopographicMap.TopoDemo;

// Drives the HUD map. The corner minimap and the full-screen world map are both
// ColorRects running the topographic styling shader over the shared height
// buffer (the MapView SubViewport texture). Each sets a sampling window
// (center + span in buffer-UV space) so it rasterizes the topographic effect at
// its own resolution: the minimap is a fixed player-centered window, the world
// map is a zoom/pan window. Nothing magnifies a pre-rendered image. The world
// map is kept a centered square so the square world is not stretched on a
// non-square screen.
public partial class MapUi : Control
{
    [Export] public SubViewport MapViewport;
    [Export] public ColorRect Minimap;
    [Export] public ColorRect WorldMapImage;
    [Export] public Control WorldMapRoot;
    [Export] public Node3D Player;
    [Export] public float TerrainSize = 1536.0f;
    [Export] public float MinimapWorldSpan = 220.0f;

    // World map zoom. Zoom is how much of the world the window spans: span = 1/zoom.
    [Export] public float InitialZoom = 1.8f;
    [Export] public float MinZoom = 1.0f;
    [Export] public float MaxZoom = 6.0f;
    [Export] public float ZoomStep = 1.15f;

    private ShaderMaterial _minimapMat;
    private ShaderMaterial _worldMat;

    private float _zoom = 1.8f;
    private Vector2 _panUv = new(0.5f, 0.5f); // world UV at the world-map window center
    private bool _dragging;

    public override void _Ready()
    {
        _minimapMat = (ShaderMaterial)Minimap.Material;
        _worldMat = (ShaderMaterial)WorldMapImage.Material;

        var heightBuffer = MapViewport.GetTexture();
        _minimapMat.SetShaderParameter("height_buffer", heightBuffer);
        _worldMat.SetShaderParameter("height_buffer", heightBuffer);

        WorldMapRoot.Visible = false;
    }

    public override void _Process(double delta)
    {
        UpdateMinimap();
        if (WorldMapRoot.Visible)
        {
            LayoutWorldMap();
            UpdateWorldMap();
        }
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent.IsActionPressed("toggle_map"))
        {
            ToggleWorldMap();
            return;
        }

        if (!WorldMapRoot.Visible)
        {
            return;
        }

        switch (inputEvent)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } wheelUp:
                ZoomAt(wheelUp.Position, ZoomStep);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } wheelDown:
                ZoomAt(wheelDown.Position, 1.0f / ZoomStep);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } leftButton:
                _dragging = leftButton.Pressed;
                break;
            case InputEventMouseMotion motion when _dragging:
                // Drag moves the world under the cursor: shift the window center
                // opposite the drag, scaled by the current span over the map size.
                _panUv -= motion.Relative / WorldMapImage.Size * WindowSpan();
                ClampPan();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Equal or Key.KpAdd }:
                ZoomAt(WorldMapImage.Position + WorldMapImage.Size * 0.5f, ZoomStep);
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Minus or Key.KpSubtract }:
                ZoomAt(WorldMapImage.Position + WorldMapImage.Size * 0.5f, 1.0f / ZoomStep);
                break;
        }
    }

    private void ToggleWorldMap()
    {
        bool show = !WorldMapRoot.Visible;
        WorldMapRoot.Visible = show;
        Minimap.Visible = !show;
        if (show)
        {
            _zoom = InitialZoom;
            _panUv = WorldToUv(Player.GlobalPosition);
            ClampPan();
            LayoutWorldMap();
            UpdateWorldMap();
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else
        {
            _dragging = false;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    private void UpdateMinimap()
    {
        float span = MinimapWorldSpan / TerrainSize;
        _minimapMat.SetShaderParameter("window_center", WorldToUv(Player.GlobalPosition));
        _minimapMat.SetShaderParameter("window_span", new Vector2(span, span));
    }

    // Keep the world map a centered square sized to the shorter screen dimension.
    private void LayoutWorldMap()
    {
        float side = Mathf.Min(WorldMapRoot.Size.X, WorldMapRoot.Size.Y);
        WorldMapImage.Size = new(side, side);
        WorldMapImage.Position = (WorldMapRoot.Size - WorldMapImage.Size) * 0.5f;
    }

    private void UpdateWorldMap()
    {
        float span = WindowSpan();
        _worldMat.SetShaderParameter("window_center", _panUv);
        _worldMat.SetShaderParameter("window_span", new Vector2(span, span));
    }

    // Fraction of the world the world-map window spans at the current zoom.
    private float WindowSpan() => Mathf.Min(1.0f, 1.0f / _zoom);

    private Vector2 WorldToUv(Vector3 world) =>
        new(world.X / TerrainSize + 0.5f, world.Z / TerrainSize + 0.5f);

    // Zoom while keeping the world point under screenPos fixed on screen.
    private void ZoomAt(Vector2 screenPos, float factor)
    {
        Vector2 screenUv = (screenPos - WorldMapImage.Position) / WorldMapImage.Size - new Vector2(0.5f, 0.5f);
        Vector2 uvUnderCursor = _panUv + screenUv * WindowSpan();

        _zoom = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);

        _panUv = uvUnderCursor - screenUv * WindowSpan();
        ClampPan();
    }

    private void ClampPan()
    {
        float half = WindowSpan() * 0.5f;
        float lo = half;
        float hi = 1.0f - half;
        if (lo > hi)
        {
            _panUv = new(0.5f, 0.5f);
            return;
        }
        _panUv = new(Mathf.Clamp(_panUv.X, lo, hi), Mathf.Clamp(_panUv.Y, lo, hi));
    }
}
