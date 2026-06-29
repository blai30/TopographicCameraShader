using Godot;

namespace TopographicMap.TopoDemo;

// Edit-time command-line tool. From the repo root:
//   godot --headless --path . --script res://TopoDemo/scripts/TerrainBaker.cs
// Bakes every committed terrain asset, then quits:
//   heightmap.exr + terrain_collision.res - the demo continent (used by the game)
//   banner_heightmap.exr                  - rich terrain for DemoTerrain.tscn / the README shots
//   torture_heightmap.exr                 - schematic stress terrain for DemoTerrain.tscn view mode
// Never referenced by the game scene or any autoload, so the shipped game contains no
// generator; only the baked outputs load at runtime.
public partial class TerrainBaker : MainLoop
{
    private const float WorldSize = 1536f;
    private const float Half = WorldSize * 0.5f; // 768
    private const float MinHeight = -40f;
    private const float MaxHeight = 110f;

    private const int DemoRes = 512; // demo heightmap texels
    private const int BannerRes = 1024; // banner heightmap texels (finer relief)
    private const int TortureRes = 1024; // torture heightmap texels (matches the mesh for sharp cliffs)
    private const int CollisionGrid = 513; // verts; cell = WorldSize / (CollisionGrid - 1) = 3 units

    private const string HeightmapPath = "res://TopoDemo/assets/heightmap.exr";
    private const string CollisionPath = "res://TopoDemo/assets/terrain_collision.res";
    private const string BannerPath = "res://TopoDemo/assets/banner_heightmap.exr";
    private const string TorturePath = "res://TopoDemo/assets/torture_heightmap.exr";

    public override void _Initialize()
    {
        BuildDemoNoise();
        BakeHeightmap(HeightmapPath, DemoRes, DemoHeight);
        BakeCollision();

        BuildBannerNoise();
        BakeHeightmap(BannerPath, BannerRes, BannerHeight);

        BuildTortureNoise();
        BakeHeightmap(TorturePath, TortureRes, TortureHeight);
    }

    // Quit after the first frame; all work is done in _Initialize.
    public override bool _Process(double delta) => true;

    // Shared heightmap bake: a single-channel EXR normalized 0..1 over [MinHeight, MaxHeight].
    private static void BakeHeightmap(string path, int res, System.Func<float, float, float> sample)
    {
        var image = Image.CreateEmpty(res, res, false, Image.Format.Rf);
        float minSeen = float.MaxValue, maxSeen = float.MinValue;
        for (int ty = 0; ty < res; ty++)
        for (int tx = 0; tx < res; tx++)
        {
            float wx = (tx + 0.5f) / res * WorldSize - Half;
            float wz = (ty + 0.5f) / res * WorldSize - Half;
            float height = sample(wx, wz);
            minSeen = Mathf.Min(minSeen, height);
            maxSeen = Mathf.Max(maxSeen, height);
            float normalized = Mathf.Clamp((height - MinHeight) / (MaxHeight - MinHeight), 0f, 1f);
            image.SetPixel(tx, ty, new(normalized, 0f, 0f));
        }

        var error = image.SaveExr(ProjectSettings.GlobalizePath(path), true);
        GD.Print($"Baked {path} ({res}x{res}): {error}  height {minSeen:0.0}..{maxSeen:0.0}");
    }

    private void BakeCollision()
    {
        // Collision HeightMapShape3D, heights in world units, same field as the demo heightmap.
        float[] data = new float[CollisionGrid * CollisionGrid];
        const float cell = WorldSize / (CollisionGrid - 1); // world units between grid points after the node scale
        int below = 0;
        for (int iz = 0; iz < CollisionGrid; iz++)
        for (int ix = 0; ix < CollisionGrid; ix++)
        {
            // Sample at the world position each grid point lands on after the uniform
            // (cell,cell,cell) node scale. Heights are stored divided by the cell size so the
            // uniform scale (needed to keep the CollisionShape3D happy) does not stretch them:
            // storedHeight * cell == true world height.
            float wx = (ix - (CollisionGrid - 1) * 0.5f) * cell;
            float wz = (iz - (CollisionGrid - 1) * 0.5f) * cell;
            float height = DemoHeight(wx, wz);
            data[iz * CollisionGrid + ix] = height / cell;
            if (height < 0f) below++;
        }

        var shape = new HeightMapShape3D { MapWidth = CollisionGrid, MapDepth = CollisionGrid, MapData = data };
        var error = ResourceSaver.Save(shape, CollisionPath);
        float waterPct = 100f * below / (CollisionGrid * CollisionGrid);
        GD.Print($"Collision: {error} -> {CollisionPath} ({CollisionGrid}x{CollisionGrid})  water {waterPct:0.0}%");
    }

    // ---- Demo continent: the playable terrain. Smooth, broadly rolling land (single-octave
    // simplex, no ridged noise or domain warp, so the map contours read as smooth ripples),
    // with a small ocean in the far SW corner, a smooth inland lake, and a flattened spawn.
    private FastNoiseLite _continent, _relief;

    private void BuildDemoNoise()
    {
        _continent = new()
        {
            Seed = 1337, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.None, Frequency = 0.0016f
        };
        _relief = new()
        {
            Seed = 1539, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.None, Frequency = 0.0032f
        };
    }

    private float DemoHeight(float wx, float wz)
    {
        float nx = wx / Half; // -1..1
        float nz = wz / Half;

        float coast = (nx - nz) * 0.5f; // -1 ocean corner .. +1 inland
        float cont = _continent.GetNoise2D(wx, wz);

        float land = coast * 0.85f + cont * 0.4f + 0.6f;
        float shore = Mathf.SmoothStep(-0.2f, 0.25f, land); // 0 open ocean, 1 inland

        float height = Mathf.Lerp(MinHeight, 16f, shore);

        // Smooth rolling relief on the land: two single-octave simplex scales blended, biased
        // upward so most of the land sits above sea level (a coastline, not a flooded map).
        float relief = cont * 0.6f + _relief.GetNoise2D(wx, wz) * 0.4f;
        height += (relief + 0.28f) * 52f * shore;

        // A smooth inland lake basin.
        float lake = 1f - Mathf.SmoothStep(45f, 95f, Distance(wx, wz, 170f, 30f));
        height = Mathf.Lerp(height, -10f, lake * shore * 0.85f);

        // Flatten the spawn area so the player starts on gentle ground.
        float spawn = 1f - Mathf.SmoothStep(0f, 22f, Distance(wx, wz, 120f, 130f));
        height = Mathf.Lerp(height, Mathf.Max(height, 12f), spawn);

        return Mathf.Clamp(height, MinHeight, MaxHeight);
    }

    // ---- Banner terrain (used only by DemoTerrain.tscn and the README screenshots) ----
    // Multi-scale smooth fbm (SimplexSmooth, no ridged noise, no domain warp): a few octaves
    // give large massifs with smaller hills nested inside, so the terrain naturally carries
    // lots of smooth concentric contours. Ridged noise and domain warp are what folded the
    // contours into jagged zigzags before, so neither is used.
    private FastNoiseLite _bSwell;

    private void BuildBannerNoise()
    {
        _bSwell = new()
        {
            Seed = 7001, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm, FractalOctaves = 3, FractalGain = 0.46f,
            FractalLacunarity = 2.0f, Frequency = 0.0036f
        };
    }

    // Smooth rolling relief over a full-ish height span, so the frame carries many smooth
    // concentric contour rings. The gentle banner colors come from the soft elevation_gradient,
    // not from squashing the height range, so the contour count stays high.
    private float BannerHeight(float wx, float wz)
    {
        float h = _bSwell.GetNoise2D(wx, wz);
        float n = Mathf.Clamp(h * 0.92f + 0.5f, 0f, 1f);
        return Mathf.Lerp(MinHeight, MaxHeight, n);
    }

    private static float Distance(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ---- Torture terrain (used only by DemoTerrain.tscn view mode): a schematic test pattern of
    // disjoint analytic zones (mesa, staircase, cone+ridge, basin, col/saddle, smooth patch) on a
    // flat base, with hard vertical relief the smooth terrains never produce. The validation rig for
    // the Phase 1 robustness items. See docs/topographic-map-architecture.md for the zone map.
    private FastNoiseLite _tortureRings, _tortureApron;

    private void BuildTortureNoise()
    {
        _tortureRings = new()
        {
            Seed = 2026, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.None, Frequency = 0.012f
        };
        _tortureApron = new()
        {
            Seed = 4242, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.None, Frequency = 0.02f
        };
    }

    // Schematic torture field. A flat base at +5 (between the 0 and 10 contours so the apron stays
    // clean), plus six spatially disjoint zones laid out on a 3-by-2 grid. Only one zone is nonzero
    // at any point, so the per-zone deltas simply sum onto the base.
    private float TortureHeight(float wx, float wz)
    {
        const float baseLevel = 5f;
        float height = baseLevel;

        // Zone centers (col 0..2 by x, row 0..1 by z). Cells are 512 wide by 768 tall.
        // (0,0) Mesa: flat top with vertical cliff walls. Top +85 (between contours), foot = base.
        {
            float lx = wx - -512f, lz = wz - -384f;
            float fill = BoxFill(lx, lz, 150f, 150f, 50f, 4f);
            height += (85f - baseLevel) * fill;
        }

        // (1,0) Staircase: five flat treads with vertical risers climbing along +z. Treads sit between
        // contours; the hard index jumps between treads are the risers.
        {
            float lx = wx - 0f, lz = wz - -384f;
            float mask = BoxFill(lx, lz, 170f, 300f, 30f, 4f);
            float t = Mathf.Clamp((lz + 300f) / 600f, 0f, 1f);
            int step = Mathf.Clamp((int)(t * 5f), 0, 4);
            float[] treads = { 5f, 25f, 45f, 65f, 85f };
            height += (treads[step] - baseLevel) * mask;
        }

        // (2,0) Cone + ridge: a steep peak (+105) with the +x lobe stretched into a ridge spur, so the
        // concentric contours elongate into a crest on one side.
        {
            float lx = wx - 512f, lz = wz - -384f;
            float ax = lx > 0f ? lx * 0.45f : lx; // stretch the +x lobe into a ridge
            float radial = Mathf.Sqrt(ax * ax + lz * lz);
            float cone = Mathf.Max(0f, 1f - radial / 180f);
            height += (105f - baseLevel) * cone;
        }

        // (0,1) Basin: flat floor at -25 (between contours) with steep walls rising to the base.
        {
            float radial = Distance(wx, wz, -512f, 384f);
            float wall = Mathf.SmoothStep(142f, 130f, radial); // 1 on the floor, 0 past the wall
            height += (-25f - baseLevel) * wall;
        }

        // (1,1) Col / saddle: two Gaussian peaks (+80) combined with max, leaving a saddle pass tuned to
        // sit exactly on the +40 contour, which then forms the saddle X-crossing.
        {
            float lx = wx - 0f, lz = wz - 384f;
            const float amp = 75f, sigma = 73f, denom = 2f * sigma * sigma;
            float dxA = lx - 90f, dxB = lx + 90f;
            float peakA = amp * Mathf.Exp(-(dxA * dxA + lz * lz) / denom);
            float peakB = amp * Mathf.Exp(-(dxB * dxB + lz * lz) / denom);
            height += Mathf.Max(peakA, peakB);
        }

        // (2,1) Smooth patch: gentle single-octave simplex mapped into +10..+50, with a soft (wide) edge
        // so it blends into the base. Many closely spaced smooth rings for the moire case.
        {
            float lx = wx - 512f, lz = wz - 384f;
            float sdf = RoundedBoxSdf(lx, lz, 200f, 320f, 40f);
            float window = Mathf.SmoothStep(30f, -30f, sdf);
            float rings = _tortureRings.GetNoise2D(wx, wz) * 0.5f + 0.5f; // 0..1
            height += window * (5f + 40f * rings);
        }

        // Low-amplitude apron relief everywhere so flat regions are not a single dead band. Amplitude is
        // small enough to keep flat tops and floors from crossing their neighboring contours.
        height += 1.0f * _tortureApron.GetNoise2D(wx, wz);

        return Mathf.Clamp(height, MinHeight, MaxHeight);
    }

    // Signed distance to an axis-aligned rounded box centered at the origin. Negative inside.
    private static float RoundedBoxSdf(float px, float pz, float halfX, float halfZ, float radius)
    {
        float qx = Mathf.Abs(px) - halfX + radius;
        float qz = Mathf.Abs(pz) - halfZ + radius;
        float outside = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qz, 0f) * Mathf.Max(qz, 0f));
        float inside = Mathf.Min(Mathf.Max(qx, qz), 0f);
        return outside + inside - radius;
    }

    // 1 well inside a rounded box, 0 well outside, with a near-vertical transition band of half-width
    // `cliff` world units across the edge (a few buffer texels at 1024 res).
    private static float BoxFill(float px, float pz, float halfX, float halfZ, float radius, float cliff)
    {
        float sdf = RoundedBoxSdf(px, pz, halfX, halfZ, radius);
        return Mathf.SmoothStep(cliff, -cliff, sdf);
    }
}
