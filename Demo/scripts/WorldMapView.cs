using Godot;

namespace TopographicCameraShader.Demo;

// Owns the toggleable fullscreen world map: open/close, drag-pan, wheel-zoom, and
// keeping the marker camera locked onto the map camera. Constructed by Demo with
// the scene nodes it drives, so all world-map interaction state lives in one place.
public class WorldMapView
{
    private const float MinSize = 30f;
    private const float MaxSize = 470f;

    // Ortho size the map opens at: zoomed in on the player, not the whole island.
    private const float DefaultSize = 130f;

    private readonly PlayerController _player;
    private readonly float _cameraHeight;
    private readonly SubViewport _viewport;
    private readonly Camera3D _camera;
    private readonly SubViewport _markerViewport;
    private readonly Camera3D _markerCamera;
    private readonly Control _overlay;
    private readonly Control _texture;

    private bool _dragging;
    private bool _initialized;

    public WorldMapView(
        PlayerController player,
        float cameraHeight,
        SubViewport viewport,
        Camera3D camera,
        SubViewport markerViewport,
        Camera3D markerCamera,
        Control overlay,
        Control texture)
    {
        _player = player;
        _cameraHeight = cameraHeight;
        _viewport = viewport;
        _camera = camera;
        _markerViewport = markerViewport;
        _markerCamera = markerCamera;
        _overlay = overlay;
        _texture = texture;

        _overlay.Visible = false;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _markerViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    public bool IsOpen { get; private set; }

    // Keeps the marker camera aligned with the map camera while the map is open.
    public void SyncMarker()
    {
        if (!IsOpen) return;

        _markerCamera.GlobalTransform = _camera.GlobalTransform;
        _markerCamera.Size = _camera.Size;
    }

    public void Toggle()
    {
        IsOpen = !IsOpen;
        _overlay.Visible = IsOpen;
        var mode = IsOpen ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        _viewport.RenderTargetUpdateMode = mode;
        _markerViewport.RenderTargetUpdateMode = mode;

        // First open: frame on the player. Subsequent opens keep prior pan/zoom.
        if (IsOpen && !_initialized)
        {
            var playerPos = _player.GlobalPosition;
            _camera.Position = new(playerPos.X, _cameraHeight, playerPos.Z);
            _camera.Size = DefaultSize;
            _initialized = true;
        }

        _player.InputEnabled = !IsOpen;
        Input.MouseMode = IsOpen ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
        _dragging = false;
    }

    public void HandleMouseButton(InputEventMouseButton mb)
    {
        if (mb.ButtonIndex == MouseButton.Left)
        {
            _dragging = mb.Pressed;
        }
        else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)
        {
            Zoom(0.9f);
        }
        else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown) Zoom(1.1f);
    }

    public void HandleMouseMotion(InputEventMouseMotion motion)
    {
        if (_dragging) Pan(motion.Relative);
    }

    private void Zoom(float factor)
    {
        _camera.Size = Mathf.Clamp(_camera.Size * factor, MinSize, MaxSize);
    }

    private void Pan(Vector2 pixelDelta)
    {
        // Camera moves opposite to drag (map follows cursor).
        var rect = _texture.Size;
        if (rect.X <= 0f || rect.Y <= 0f) return;

        float worldPerPixelX = _camera.Size * (rect.X / rect.Y) / rect.X;
        float worldPerPixelZ = _camera.Size / rect.Y;
        _camera.Position += new Vector3(-pixelDelta.X * worldPerPixelX, 0f, -pixelDelta.Y * worldPerPixelZ);
    }
}
