using System.Linq;
using Godot;

namespace TopographicCameraShader.Demo;

public partial class Demo : Node3D
{
    private const float MapCameraHeight = 150f;
    private const float WorldMapMinSize = 30f;

    private const float WorldMapMaxSize = 470f;

    // Ortho size the world map opens at: zoomed in on the player, not the whole island.
    private const float WorldMapDefaultSize = 130f;

    [Export] public PlayerController Player { get; set; }
    [Export] public Camera3D MinimapCamera { get; set; }
    [Export] public Camera3D MinimapMarkerCamera { get; set; }

    [Export] public SubViewport WorldMapViewport { get; set; }
    [Export] public Camera3D WorldMapCamera { get; set; }
    [Export] public SubViewport WorldMapMarkerViewport { get; set; }
    [Export] public Camera3D WorldMapMarkerCamera { get; set; }
    [Export] public Control WorldMapOverlay { get; set; }
    [Export] public Control WorldMapTexture { get; set; }

    public bool MapOpen { get; private set; }

    private TopographicEffect _effect;
    private bool _dragging;
    private bool _worldMapInitialized;

    public override void _Ready()
    {
        _effect = ResolveEffect(MinimapCamera);

        WorldMapOverlay.Visible = false;
        WorldMapViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        WorldMapMarkerViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    private static TopographicEffect ResolveEffect(Camera3D camera) =>
        camera?.Compositor?.CompositorEffects.OfType<TopographicEffect>().FirstOrDefault();

    public override void _Process(double delta)
    {
        var playerPos = Player.GlobalPosition;
        MinimapCamera.Position = new(playerPos.X, MapCameraHeight, playerPos.Z);
        MinimapMarkerCamera.GlobalTransform = MinimapCamera.GlobalTransform;

        if (!MapOpen)
        {
            return;
        }

        WorldMapMarkerCamera.GlobalTransform = WorldMapCamera.GlobalTransform;
        WorldMapMarkerCamera.Size = WorldMapCamera.Size;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            HandleKey(key);
            return;
        }

        if (!MapOpen)
        {
            return;
        }

        if (@event is InputEventMouseButton mb)
        {
            HandleMouseButton(mb);
        }
        else if (@event is InputEventMouseMotion motion && _dragging)
        {
            PanWorldMap(motion.Relative);
        }
    }

    private void HandleKey(InputEvent key)
    {
        if (key.IsActionPressed("toggle_map"))
        {
            ToggleMap();
        }
        else if (MapOpen)
        {
            HandleShaderToggle(key);
        }
    }

    private void HandleMouseButton(InputEventMouseButton mb)
    {
        if (mb.ButtonIndex == MouseButton.Left)
        {
            _dragging = mb.Pressed;
        }
        else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)
        {
            ZoomWorldMap(0.9f);
        }
        else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown)
        {
            ZoomWorldMap(1.1f);
        }
    }

    private void ToggleMap()
    {
        MapOpen = !MapOpen;
        WorldMapOverlay.Visible = MapOpen;
        var mode = MapOpen ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        WorldMapViewport.RenderTargetUpdateMode = mode;
        WorldMapMarkerViewport.RenderTargetUpdateMode = mode;

        // First open: frame on the player. Subsequent opens keep prior pan/zoom.
        if (MapOpen && !_worldMapInitialized)
        {
            var playerPos = Player.GlobalPosition;
            WorldMapCamera.Position = new(playerPos.X, MapCameraHeight, playerPos.Z);
            WorldMapCamera.Size = WorldMapDefaultSize;
            _worldMapInitialized = true;
        }

        Player.InputEnabled = !MapOpen;
        Input.MouseMode = MapOpen ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
        _dragging = false;
    }

    private void ZoomWorldMap(float factor)
    {
        WorldMapCamera.Size = Mathf.Clamp(WorldMapCamera.Size * factor, WorldMapMinSize, WorldMapMaxSize);
    }

    private void PanWorldMap(Vector2 pixelDelta)
    {
        // Camera moves opposite to drag (map follows cursor).
        var rect = WorldMapTexture.Size;
        if (rect.X <= 0f || rect.Y <= 0f)
        {
            return;
        }

        float worldPerPixelX = WorldMapCamera.Size * (rect.X / rect.Y) / rect.X;
        float worldPerPixelZ = WorldMapCamera.Size / rect.Y;
        WorldMapCamera.Position += new Vector3(
            -pixelDelta.X * worldPerPixelX,
            0f,
            -pixelDelta.Y * worldPerPixelZ);
    }

    private void HandleShaderToggle(InputEvent @event)
    {
        if (_effect == null)
        {
            return;
        }

        if (@event.IsActionPressed("topo_shader"))
        {
            _effect.Enabled = !_effect.Enabled;
        }
        else if (@event.IsActionPressed("topo_contours"))
        {
            _effect.ContoursEnabled = !_effect.ContoursEnabled;
        }
        else if (@event.IsActionPressed("topo_major"))
        {
            _effect.MajorContoursEnabled = !_effect.MajorContoursEnabled;
        }
        else if (@event.IsActionPressed("topo_ramp"))
        {
            _effect.SmoothRamp = !_effect.SmoothRamp;
        }
        else if (@event.IsActionPressed("topo_invert"))
        {
            _effect.InvertRamp = !_effect.InvertRamp;
        }
    }
}
