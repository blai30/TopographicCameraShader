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

    [Export] public ContourLayer MinimapContours;
    [Export] public ContourLayer WorldMapContours;
    [Export] public ContourFieldResource BakedContours;

    // Contour level params, used only by the edit-time bake (the play path reads
    // them from the baked resource). They match the tint material's height range
    // and interval so the baked lines land on the tint band edges.
    private const float ContourHeightMin = -40.0f;
    private const float ContourHeightMax = 110.0f;
    private const float ContourInterval = 10.0f;
    private const int ContourMajorEvery = 5;
    private const int ContourResolution = 2048;
    private const string ContourPath = "res://TopoDemo/assets/contours.res";

    [Export] public Control MinimapMarker;
    [Export] public Control WorldMapMarker;
    [Export] public Node3D PlayerBody;
    [Export] public float MarkerScreenSize = 24.0f;

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

        SetupMarker(MinimapMarker);
        SetupMarker(WorldMapMarker);

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "bake-contours") >= 0)
        {
            _ = BakeContoursAsync();
            return;
        }

        LoadBakedContours();
    }

    // Edit-time bake: render the height buffer once with a real GPU, extract the
    // contour field from that buffer (the exact field the tint samples, so the
    // lines land on the band edges), and save it to the committed contours.res.
    // The heightmap-derived field is close but differs enough on gentle slopes to
    // shift the lines off the bands, so the contours must come from the buffer.
    // Run with: godot --path . res://TopoDemo/scenes/Demo.tscn -- bake-contours
    private async System.Threading.Tasks.Task BakeContoursAsync()
    {
        var field = await ContourSource.BuildFromViewportAsync(MapViewport, ContourHeightMin, ContourHeightMax,
            ContourInterval, ContourMajorEvery, ContourResolution);
        var resource = ContourFieldResource.FromField(field, ContourHeightMin, ContourHeightMax, ContourInterval,
            ContourMajorEvery);
        var error = ResourceSaver.Save(resource, ContourPath);
        GD.Print($"Baked contours: {error} -> {ContourPath} ({field.Polylines.Count} polylines)");
        GetTree().Quit();
    }

    private void SetupMarker(Control marker)
    {
        marker.Size = new(MarkerScreenSize, MarkerScreenSize);
        marker.PivotOffset = marker.Size * 0.5f;
    }

    // Player heading as a Control rotation. Body yaw is rotation about Y; on the
    // top-down map, screen rotation runs opposite world yaw. Flip the sign if the
    // arrow points the wrong way.
    private float MarkerRotation() => -PlayerBody.GlobalRotation.Y;

    // Load the baked contour field and hand it to both layers. Present on the
    // first frame with no buffer readback. Re-run TerrainBaker after changing the
    // terrain to regenerate contours.res.
    private void LoadBakedContours()
    {
        if (BakedContours == null)
        {
            GD.PrintErr("MapUi: BakedContours is not assigned; run TerrainBaker to bake contours.res.");
            return;
        }

        var field = BakedContours.ToField();
        MinimapContours.Field = field;
        WorldMapContours.Field = field;
        MinimapContours.QueueRedraw();
        WorldMapContours.QueueRedraw();
    }

    public override void _Process(double delta)
    {
        UpdateMinimap();
        if (!WorldMapRoot.Visible) return;
        LayoutWorldMap();
        UpdateWorldMap();
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
        MinimapContours.SetWindow(WorldToUv(Player.GlobalPosition), span);

        // The minimap is always centered on the player, so the marker sits at center.
        MinimapMarker.Position = Minimap.Size * 0.5f - MinimapMarker.Size * 0.5f;
        MinimapMarker.Rotation = MarkerRotation();
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
        WorldMapContours.SetWindow(_panUv, span);

        // Place the marker at the player's position within the current window; hide
        // it when the player is outside the visible area.
        var rel = (WorldToUv(Player.GlobalPosition) - _panUv) / span + new Vector2(0.5f, 0.5f);
        bool onMap = rel.X is >= 0.0f and <= 1.0f && rel.Y is >= 0.0f and <= 1.0f;
        WorldMapMarker.Visible = onMap;
        if (!onMap) return;
        WorldMapMarker.Position = rel * WorldMapImage.Size - WorldMapMarker.Size * 0.5f;
        WorldMapMarker.Rotation = MarkerRotation();
    }

    // Fraction of the world the world-map window spans at the current zoom.
    private float WindowSpan() => Mathf.Min(1.0f, 1.0f / _zoom);

    private Vector2 WorldToUv(Vector3 world) =>
        new(world.X / TerrainSize + 0.5f, world.Z / TerrainSize + 0.5f);

    // Zoom while keeping the world point under screenPos fixed on screen.
    private void ZoomAt(Vector2 screenPos, float factor)
    {
        var screenUv = (screenPos - WorldMapImage.Position) / WorldMapImage.Size - new Vector2(0.5f, 0.5f);
        var uvUnderCursor = _panUv + screenUv * WindowSpan();

        _zoom = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);

        _panUv = uvUnderCursor - screenUv * WindowSpan();
        ClampPan();
    }

    private void ClampPan()
    {
        float half = WindowSpan() * 0.5f;
        float hi = 1.0f - half;
        if (half > hi)
        {
            _panUv = new(0.5f, 0.5f);
            return;
        }

        _panUv = new(Mathf.Clamp(_panUv.X, half, hi), Mathf.Clamp(_panUv.Y, half, hi));
    }
}
