using Godot;

namespace TopographicMap.TopoDemo;

public partial class PlayerController : CharacterBody3D
{
    [Export] public float MoveSpeed = 16.0f;
    [Export] public float SprintSpeed = 30.0f;
    [Export] public float JumpVelocity = 8.0f;
    [Export] public float MouseSensitivity = 0.002f;
    [Export] public float MinPitch = -1.2f;
    [Export] public float MaxPitch = 0.4f;

    [Export] public Node3D CameraPivot;

    private float _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity");
    private float _pitch;

    public override void _Ready()
    {
        // Start with the cursor free. Mouselook is enabled when the player clicks into
        // the window (see _UnhandledInput), so launching the game never confines the
        // cursor and an abrupt window close cannot leave the OS cursor clip stuck.
        Input.MouseMode = Input.MouseModeEnum.Visible;

        // Keep the third-person spring arm from colliding with the player's own body.
        if (CameraPivot is SpringArm3D springArm)
        {
            springArm.AddExcludedObject(GetRid());
        }
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        // Clicking into the window grabs the mouse and starts mouselook. While the world
        // map is open MapUi consumes its own left clicks, so this only fires during play.
        if (inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }
            && Input.MouseMode == Input.MouseModeEnum.Visible)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (inputEvent is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            // Yaw turns the whole body (kept upright, rotation stays on Y); the camera pivot
            // only pitches. MapUi reads the body's yaw to rotate the player marker.
            RotateY(-motion.Relative.X * MouseSensitivity);
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, MinPitch, MaxPitch);
            CameraPivot.Rotation = new(_pitch, 0.0f, 0.0f);
        }

        // Escape releases the mouse; click back into the window to grab it again.
        if (inputEvent.IsActionPressed("ui_cancel"))
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        var velocity = Velocity;

        if (!IsOnFloor())
        {
            velocity.Y -= _gravity * (float)delta;
        }
        else if (Input.IsActionJustPressed("jump"))
        {
            velocity.Y = JumpVelocity;
        }

        var input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        float yaw = Rotation.Y;
        var forward = new Vector3(-Mathf.Sin(yaw), 0.0f, -Mathf.Cos(yaw));
        var right = new Vector3(Mathf.Cos(yaw), 0.0f, -Mathf.Sin(yaw));
        // input.Y is positive for "back", so subtracting forward maps W to the facing direction.
        var direction = (right * input.X - forward * input.Y).Normalized();

        float speed = Input.IsActionPressed("sprint") ? SprintSpeed : MoveSpeed;
        if (direction.LengthSquared() > 0.001f)
        {
            velocity.X = direction.X * speed;
            velocity.Z = direction.Z * speed;
        }
        else
        {
            velocity.X = Mathf.MoveToward(velocity.X, 0.0f, speed);
            velocity.Z = Mathf.MoveToward(velocity.Z, 0.0f, speed);
        }

        Velocity = velocity;
        MoveAndSlide();
    }
}
