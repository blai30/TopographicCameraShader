using System;
using Godot;

namespace TopographicCameraShader.Demo;

public partial class Demo : Node3D
{
    private const float MapCameraHeight = 150f;

    [Export] public PlayerController Player { get; set; }
    [Export] public Camera3D MinimapCamera { get; set; }
    [Export] public Camera3D MinimapMarkerCamera { get; set; }

    [Export] public SubViewport WorldMapViewport { get; set; }
    [Export] public Camera3D WorldMapCamera { get; set; }
    [Export] public SubViewport WorldMapMarkerViewport { get; set; }
    [Export] public Camera3D WorldMapMarkerCamera { get; set; }
    [Export] public Control WorldMapOverlay { get; set; }
    [Export] public Control WorldMapTexture { get; set; }

    // Each runtime shader toggle as data: the input action plus how to read and
    // write the matching effect flag. Adding a toggle is one row.
    private static readonly (string Action, Func<TopographicEffect, bool> Get, Action<TopographicEffect, bool> Set)[]
        ShaderToggles =
        [
            ("topo_shader", e => e.Enabled, (e, v) => e.Enabled = v),
            ("topo_contours", e => e.ContoursEnabled, (e, v) => e.ContoursEnabled = v),
            ("topo_major", e => e.MajorContoursEnabled, (e, v) => e.MajorContoursEnabled = v),
            ("topo_ramp", e => e.SmoothRamp, (e, v) => e.SmoothRamp = v),
            ("topo_invert", e => e.InvertRamp, (e, v) => e.InvertRamp = v)
        ];

    private TopographicEffect _effect;
    private WorldMapView _worldMap;

    public override void _Ready()
    {
        _effect = TopographicEffect.FindIn(MinimapCamera);
        _worldMap = new(
            Player, MapCameraHeight,
            WorldMapViewport, WorldMapCamera,
            WorldMapMarkerViewport, WorldMapMarkerCamera,
            WorldMapOverlay, WorldMapTexture);
    }

    public override void _Process(double delta)
    {
        // Minimap follows the player from directly above.
        var playerPos = Player.GlobalPosition;
        MinimapCamera.Position = new(playerPos.X, MapCameraHeight, playerPos.Z);
        MinimapMarkerCamera.GlobalTransform = MinimapCamera.GlobalTransform;

        _worldMap.SyncMarker();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            HandleKey(key);
            return;
        }

        if (!_worldMap.IsOpen) return;

        if (@event is InputEventMouseButton mb)
        {
            _worldMap.HandleMouseButton(mb);
        }
        else if (@event is InputEventMouseMotion motion)
        {
            _worldMap.HandleMouseMotion(motion);
        }
    }

    private void HandleKey(InputEvent key)
    {
        if (key.IsActionPressed("toggle_map"))
        {
            _worldMap.Toggle();
        }
        else if (_worldMap.IsOpen)
        {
            HandleShaderToggle(key);
        }
    }

    private void HandleShaderToggle(InputEvent @event)
    {
        if (_effect == null) return;

        foreach (var toggle in ShaderToggles)
        {
            if (!@event.IsActionPressed(toggle.Action)) continue;
            toggle.Set(_effect, !toggle.Get(_effect));
            return;
        }
    }
}
