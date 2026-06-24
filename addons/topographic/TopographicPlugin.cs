#if TOOLS
using Godot;

namespace TopographicCameraShader;

// Minimal editor plugin so the addon appears under Project Settings > Plugins and
// can be enabled/disabled like any installed addon. The effect itself is a
// [GlobalClass] CompositorEffect and needs no runtime registration, so this plugin
// intentionally has no body. This exists to make the package installable.
[Tool]
public partial class TopographicPlugin : EditorPlugin
{
}
#endif
