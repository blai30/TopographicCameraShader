# Topographic Map: Architecture and Engineering Notes

This document captures how the topographic map works, the decisions behind it, and the gotchas worth knowing before changing anything. It reflects the state after the move to vector contour lines.

## What it is

A topographic map rendered from any 3D terrain the game shows: a hypsometric (elevation-colored) tint with contour lines, shown as a corner minimap and a full-screen pan/zoom world map. The reusable system lives in `addons/topographic/`; the demo wiring lives in `TopoDemo/`.

## High-level architecture

The map is produced in three stages, kept deliberately separate:

```
PRODUCER (on the orthographic map camera)
  TopDownCamera (ortho, top-down)  -->  depth buffer
  TopographicCompositorEffect (compute)  reconstructs world height
      -->  HEIGHT BUFFER: MapView SubViewport texture, 2048x2048, RGBA16F
           R = normalized height in [-40, 110], G = terrain/background mask

TINT (per consumer, per pixel)
  topographic_style.gdshader (canvas_item) samples the height buffer over a UV
  window and draws the stepped hypsometric tint. No contour code.

CONTOURS (vector)
  The contour field is baked at edit time FROM THE CAMERA HEIGHT BUFFER (the same
  buffer the tint samples, so the lines land exactly on the band edges) into a
  committed ContourFieldResource (.res). At load, MapUi reads that resource and
  inflates a ContourField directly, so the lines are present on the first frame
  with no readback. The bake reuses the runtime extraction path (read the buffer
  back, run Marching Squares per level on a background thread, chain and simplify;
  ContourSource + ContourExtractor + ContourField), which also stays available at
  runtime for dynamic or non-heightmap geometry. ContourLayer (a Control) strokes
  the polylines for the current window with constant-pixel-width anti-aliased
  lines, redrawing only when it moves.

MARKER (overlay)
  A constant-size SDF arrow (marker_overlay.gdshader) drawn into a small UI
  Control on top of each map, rotated to the player's heading.
```

The height buffer is the single shared data source. The tint samples it on the GPU; the contours are extracted from a one-time CPU readback of it. Because both come from the same buffer, lines and tint band edges align.

### Why this shape

- The tint is cheap and smooth as a per-pixel shader, and it has no flat-ground problem (a band fill does not need a gradient).
- Contour lines as vector geometry are smooth and constant-width on every slope with no tuning. The per-pixel approach we used first is fundamentally unstable on flat ground (see Design history).
- Reading the contours from the camera's height buffer (not a heightmap) keeps the addon general: it works for any geometry the camera renders, with no heightmap required.

## Component reference

Reusable addon (`addons/topographic/`):

- `TopographicCompositorEffect.cs` + `topographic.glsl`: the producer. A `CompositorEffect` running a compute shader at the `PreTransparent` stage. Reads the camera depth buffer, reconstructs world height (linear for an orthographic projection), and writes `R = normalized height`, `G = coverage mask` into the camera color buffer. Exported params: `HeightMin`, `HeightMax`, `CameraY`, `NearPlane`, `FarPlane`, `DepthReversed`.
- `topographic_style.gdshader`: tint only. Samples `height_buffer` over `window_center`/`window_span`, reconstructs height, and outputs the stepped hypsometric band color sampled from `color_ramp`. No water special case: low ground simply takes the gradient's low colors.
- `MarchingSquares.cs` (pure C#, no Godot types): `ContourPoint` struct; `ExtractSegments(field, mask, cols, rows, level)` returns flat segment endpoint pairs in normalized `[0,1]` space; `ChainSegments(segments)` links them into polylines; `Simplify(points, epsilon)` runs Ramer-Douglas-Peucker to cut the dense per-cell point count.
- `ContourField.cs` (pure C#): `ContourPolyline` (points, level, major flag, bounding box) and `ContourField.Build(field, mask, cols, rows, heightMin, heightMax, interval, majorEvery, simplifyEpsilon)`. Levels are independent and extracted in parallel (`Parallel.For`); each polyline is simplified before storage.
- `ContourExtractor.cs` (pure C#): `Build(byte[] data, srcW, srcH, ...)` parses raw `Rgbaf` bytes (16 bytes/pixel, R height, G mask), optionally box-downsamples to `maxResolution`, and calls `ContourField.Build`. Pure so it can run on a background thread; the caller does the Godot-side image readback.
- `ContourFieldSerializer.cs` (pure C#): `Flatten(field, out pointsXy, out pointCounts, out levels)` packs a `ContourField` into interleaved-xy and per-polyline arrays; `Inflate(pointsXy, pointCounts, levels, heightMin, heightMax, interval, majorEvery)` rebuilds it, recomputing each polyline's bounding box and major flag (so neither is stored). No Godot types, so the round-trip is unit-tested.
- `ContourFieldResource.cs` (Godot `Resource`, `[GlobalClass]`): a thin serialization wrapper holding the flattened arrays as packed primitives (`PointsXy`, `PointCounts`, `Levels`) plus the level params. `FromField`/`ToField` delegate to `ContourFieldSerializer`. This is what the bake saves and `MapUi` loads.
- `ContourSource.cs` (Godot): `BuildFromViewportAsync(viewport, ...)` renders the viewport `Once`, reads the buffer back, and `Task.Run`s the pure `ContourExtractor.Build` off the main thread. This is both the edit-time bake source (the demo bakes its `contours.res` from this) and the runtime/general path for dynamic or non-heightmap geometry.
- `ContourLayer.cs` (Godot `Control`): holds a `ContourField`, a window (`SetWindow(center, span)`, which redraws only when the window actually changes), and draws on `_Draw` with `DrawPolyline(..., width_px, antialiased: true)`. Culls polylines by bounding box. Line color is driven by `LineColor`'s alpha: when fully transparent (the default) lines are colored dynamically from the `ColorRamp` (a `GradientTexture1D`) sampled at the line's elevation and scaled by `ContourLightness`, falling back to black when no ramp is set; any alpha above 0 makes `LineColor` a solid override. Major lines use `MajorWidthPx`, minor lines `MinorWidthPx`.
- `marker_overlay.gdshader` (canvas_item): SDF arrow for the player marker, `fwidth`-antialiased, drawn into a small UI `Control` rotated to the player's heading.

Demo (`TopoDemo/`):

- `scripts/MapUi.cs`: orchestration. Loads the baked `ContourFieldResource` (the `BakedContours` export) at `_Ready` and inflates it into a `ContourField` for both layers (`LoadBakedContours`), so the lines are present on the first frame with no readback; owns the pan/zoom window state and drives the two tint materials, two `ContourLayer`s, and two marker overlays each frame. It also owns the edit-time contour bake (`BakeContoursAsync`, gated behind the `bake-contours` command-line arg): it renders the `MapView` buffer via `ContourSource`, saves `contours.res`, and quits. The marker heading comes from the player `Body` node's yaw (`MarkerRotation()`, currently `-PlayerBody.GlobalRotation.Y`; flip the sign if the arrow points backward).
- `scenes/Demo.tscn`: the `MapView` SubViewport + `TopDownCamera` + compositor, the two map `ColorRect`s (tint), each with a `Contours` child (`ContourLayer`) and a `Marker` child (overlay), the HUD, and the `MapUi` node's `BakedContours` assigned to `assets/contours.res`.
- `scripts/TerrainBaker.cs`: edit-time, headless, CPU-only tool that bakes `heightmap.exr` (512x512) and `terrain_collision.res`. Not shipped in the running game. Run with `godot --headless --path . --script res://TopoDemo/scripts/TerrainBaker.cs`. It does NOT bake the contours: those must come from the rendered camera buffer (see the contour bake below), which needs a real GPU and so cannot run under `--headless`.

Tests (`tests/MarchingSquaresTests/`): a standalone console project that links the pure-C# files and asserts on small known grids. Run with `dotnet run --project tests/MarchingSquaresTests/MarchingSquaresTests.csproj` and expect `ALL PASS` (exit 0).

## Hard rules and constraints

- The topographic effect lives only on the orthographic map camera (via the `Compositor` on `TopDownCamera`). It must never affect the main gameplay view or the editor.
- The contour system reads the camera's height buffer, not a heightmap, so it works for any rendered geometry. Do not couple it to `heightmap.exr`.
- Terrain, heightmap, and collision are static committed files baked at edit time (`TerrainBaker`). No runtime generation of those assets. Contour polylines are a derived visualization built at load and are exempt.
- No emdash characters (or other AI-tell characters) in committed files, including comments. American English. `//` line comments.
- Coordinate conventions: world to buffer UV is `buffer_uv = world.xz / 1536 + 0.5`. Normalized height for world height `H` is `(H + 40) / 150` (range `[-40, 110]`).

## Design history (why it ended up here)

The contour line approach changed twice. The reasoning matters so the dead ends are not retried.

1. First the whole styled map (tint + contours + marker) was baked into one fixed 1024x1024 SubViewport texture and the UI magnified it. Magnifying a raster is why everything looked blocky. A shader is mathematical, but its output is sampled onto a finite pixel grid; once styled into a fixed-resolution image and upscaled, the crispness is gone.

2. Then the map was split into a height buffer (data) plus a per-pixel styling shader run at display resolution. Tint became crisp. Contour lines, drawn implicitly per pixel, did not. A constant-pixel-width implicit line needs to divide distance-to-level by the screen-space height gradient. On near-flat ground that gradient approaches zero, so the line is ill-conditioned: it dots, stipples, and flickers when panning. We fought this with height smoothing, an analytic gradient (sampling neighbor texels instead of screen derivatives), and a flat-area guard. Each helped but none fully fixed it, because the instability is inherent to implicit isoline rendering on flat terrain.

3. Finally the contour lines became vector geometry: extract the isolines with Marching Squares and stroke them with a constant pixel width. The width comes from the stroking, not from a gradient, so there is no flat-ground degeneracy. Lines are smooth and consistent on every slope with no parameters. This is how cartographic software draws contours. The tint stayed a per-pixel shader (it never had the line problem).

## Gotchas and obscure details

Godot rendering:

- `use_hdr_2d = true` on the `MapView` SubViewport is required. Without it the viewport texture is 8-bit, which crushes the normalized height to 256 levels (about 0.6 m per step). That quantization is invisible in the tint gradient but shows as terraced, jagged contours in flat areas where the surface barely changes across a band.
- The map camera needs a linear-tonemap environment override (`Environment_map`, `background_mode = 1`, `tonemap_mode = 0`, which is the default so Godot does not serialize it). Otherwise the scene's ACES tonemap distorts the stored height values nonlinearly.
- `render_target_update_mode` integer values: `Disabled = 0`, `Once = 1`, `WhenVisible = 2`, `WhenParentVisible = 3`, `Always = 4`. The producer uses `Once` (1): the terrain is static, so it renders one frame and stops. Re-rendering a static 2048x2048 buffer plus the compute every frame is wasted GPU work and was the likely cause of the window lingering on close.
- In C#, the enum is `SubViewport.UpdateMode.Once` (not `UpdateModeEnum`).
- A `canvas_item` fragment shader cannot use an early `return`. Godot reports "Using 'return' in the 'fragment' processor function is incorrect." Compute the result in branches and write `COLOR` once.
- Reading the height buffer back: set the SubViewport to `Once`, then `await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw)` a couple of times before `GetTexture().GetImage()`, so the render has completed.
- Reading pixels: convert the image to `Image.Format.Rgbaf` and parse the raw `GetData()` byte array (`BitConverter.ToSingle`, 16 bytes per pixel, R at offset 0, G at offset 4). Per-pixel `GetPixel` over millions of texels is slow enough to stall the load.
- `GradientTexture1D` is not a `Gradient`. To sample colors in C#, use its `.Gradient` property: `gradientTexture.Gradient.Sample(t)`. Multiplying the resulting `Color` by a float darkens all channels including alpha, so reset alpha to 1.

Marching Squares:

- Edge crossings are computed in a canonical corner order (the lower row-major corner first) so a shared edge yields identical points from both adjacent cells. This lets `ChainSegments` match endpoints exactly. Computing the same crossing from each cell in a different corner order can differ in the last float bit and break chaining; chaining also quantizes endpoints to a grid as a safety net.
- Saddle cases (5 and 10) emit two segments; the chosen resolution keeps the two same-side corners together. The choice is rarely visible.
- `ContourField.Build` derives levels from world heights at each interior multiple of the interval; a level is major when its index `round(H / interval)` is a multiple of `majorEvery`.

Alignment and resolution:

- Contours are extracted at the full buffer resolution (2048) so the line crossing matches the per-pixel tint's crossing; extracting at a lower resolution shifts the crossings slightly and the band color bleeds past the line.
- The contour line covers the hard tint band edge, so the band stepping needs no anti-aliasing of its own.
- Final detail is bounded by the terrain itself (the demo mesh is a 511-subdivision plane over a 512x512 heightmap). Contours are crisp at any zoom, but they trace that resolution; we accepted "crisp lines, current detail" rather than re-baking a higher-resolution heightmap.

Project and build:

- `Godot.NET.Sdk` compiles every `.cs` under the project directory. The standalone test project under `tests/` has its own entry point and links the pure sources directly, so it must be excluded from the game build with `<Compile Remove="tests/**/*.cs" />` in `TopographicMap.csproj`.
- The game targets `net8.0`; `dotnet build` does not need the net8 runtime installed. The console test project must target a runtime that is installed (net9 here) so `dotnet run` can launch.
- The world map is kept a centered square sized to the shorter screen dimension so the square world is not horizontally stretched on a non-square screen. Zoom and pan are expressed as the sampling window (`window_center`, `window_span`) shared by the tint shader and the contour layer; nothing magnifies a pre-rendered image.

## Performance and load behavior

- The demo has no runtime contour cost: the field is baked into `contours.res` at edit time and `MapUi.LoadBakedContours` inflates it synchronously at `_Ready`, so the lines are present on the very first frame with no readback and no startup flash.
- `ContourSource.BuildFromViewportAsync` (used by the edit-time bake, and at runtime for dynamic or non-heightmap geometry, but NOT on the demo's play path) is the only heavy one-time cost when it runs. It does the readback parse + Marching Squares + chaining + simplification on a background thread via `Task.Run`; the continuation resumes on the main thread (Godot installs a synchronization context), so assigning `Field` and calling `QueueRedraw` after the `await` is main-thread-safe. Within the build, the contour levels are extracted in parallel (`Parallel.For`), since each level is independent.
- Polylines are simplified with Ramer-Douglas-Peucker (`simplifyEpsilon`, default `0.00015` normalized, about 1px at max zoom, so visually lossless) so far fewer points are transformed and drawn each frame.
- `ContourLayer.SetWindow` redraws only when the window changes, so an idle open map costs nothing even though `_Process` calls it every frame.

## Tuning parameters

- Contour appearance is on the two `Contours` (`ContourLayer`) nodes: `MinorWidthPx`, `MajorWidthPx`, `ColorRamp`, `ContourLightness`, and `LineColor` (transparent alpha = dynamic ramp color, alpha above 0 = solid override).
- Contour levels are baked, so they are set on `MapUi` (the `ContourHeightMin`, `ContourHeightMax`, `ContourInterval`, `ContourMajorEvery`, `ContourResolution` constants used by `BakeContoursAsync`) and stored in `contours.res`; change them there and re-run the contour bake. They must match the tint material's `height_min`/`height_max`/`contour_interval` so lines land on band edges.
- The palette is the `color_ramp` gradient (a `GradientTexture1D`) assigned to both tint materials and both contour layers. It is the single source of all map color; water is just the gradient's low end. The demo uses `addons/topographic/gradients/hypsometric_deep.tres`; the addon ships several preset gradients in that folder.
- The marker overlays expose `marker_color` and `outline_color` (on the material) and `MarkerScreenSize` (on `MapUi`).

## Known limitations and follow-ups

- DONE: contours are baked at edit time, FROM THE CAMERA HEIGHT BUFFER. `MapUi.BakeContoursAsync` (gated behind the `bake-contours` command-line arg) renders the `MapView` buffer via `ContourSource.BuildFromViewportAsync`, flattens the field via `ContourFieldSerializer`, and `ResourceSaver.Save`s a `ContourFieldResource` to `assets/contours.res`. `MapUi` loads that resource at `_Ready` and inflates it, so the lines are present on the first frame with zero runtime extraction. Re-bake after a terrain change (needs a real GPU, so NOT `--headless`): `godot --path . res://TopoDemo/scenes/Demo.tscn -- bake-contours`. Why the buffer and not `heightmap.exr`: an early version baked from the heightmap upsampled to 2048. It looked aligned on average (the buffer matches a texel-center bilinear of the heightmap to ~0.05 m mean) but the camera buffer is the rendered MESH (vertices resampled at uv `i/511`, triangle-interpolated, reconstructed from a quantized depth buffer), which differs from the raw heightmap by up to ~1.4 m in spots. On gentle slopes contour position is `height_error / gradient`, so that small difference shifted the lines visibly off the bands. Baking from the buffer makes the field identical to what the tint samples, so the lines sit exactly on the band edges regardless of slope.
- The player marker is done: a constant-size, `fwidth`-antialiased SDF arrow UI overlay on each map (`marker_overlay.gdshader`), replacing the removed in-world marker quad. The old `marker.gdshader` was deleted. Verify the heading sign once in-editor.
- Contour extraction is one-time (static terrain). `ContourField`/`MapUi` could expose a rebuild path for terrain that changes; real-time per-frame extraction of fast-changing terrain is not built.
- The addon's `TopographicCompositorEffect` still produces the height buffer and is in use; the old per-pixel contour code in the styling shader was removed.
