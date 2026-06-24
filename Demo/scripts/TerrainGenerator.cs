using Godot;

namespace TopographicCameraShader.Demo;

public readonly record struct TerrainBake(
    ArrayMesh Mesh,
    float[] HeightField,
    int GridWidth,
    int GridDepth,
    float MinHeight,
    float MaxHeight);

// Tunables that shape the generated landmass. Defaults (TerrainSettings.Island)
// reproduce the demo island exactly; a larger, more detailed map raises the
// octave counts and mountain drama and drops the spawn-center flattening.
public readonly record struct TerrainSettings
{
    public int ContinentOctaves { get; init; }
    public int DetailOctaves { get; init; }
    public int MountainOctaves { get; init; }
    public float BaseLow { get; init; }
    public float BaseHigh { get; init; }
    public float MountainHeight { get; init; }
    public bool RiverEnabled { get; init; }
    public bool CenterFlatten { get; init; }

    // When true, a radial/elliptical falloff sinks the edges into the sea (an island).
    // When false, the falloff is dropped so terrain runs edge to edge, like a cropped
    // tile of a larger continent.
    public bool IslandFalloff { get; init; }

    // The demo island look. Matches the constants the generator used before
    // these knobs existed, so the baked demo terrain is unchanged.
    public static TerrainSettings Island => new()
    {
        ContinentOctaves = 3,
        DetailOctaves = 3,
        MountainOctaves = 5,
        BaseLow = -10f,
        BaseHigh = 26f,
        MountainHeight = 62f,
        RiverEnabled = true,
        CenterFlatten = true,
        IslandFalloff = true
    };
}

public static class TerrainGenerator
{
    private record struct NoiseSet(
        FastNoiseLite Continent,
        FastNoiseLite Detail,
        FastNoiseLite Mountain,
        FastNoiseLite MountainMask);

    // Generates a landmass spanning worldWidth (X) by worldDepth (Z). resolutionZ is
    // the cell count along Z; the X cell count is derived to keep cells roughly square,
    // so a wide world (worldWidth > worldDepth) yields a wide rectangular map rather
    // than a stretched one. A square world (worldWidth == worldDepth) reproduces the
    // original radial island exactly.
    public static TerrainBake CreateTerrain(
        float worldWidth, float worldDepth, int resolutionZ, int seed, in TerrainSettings settings)
    {
        int resolutionX = Mathf.RoundToInt(resolutionZ * (worldWidth / worldDepth));

        // Feature size is keyed to the depth (the shorter, framed dimension), so widening
        // the world adds more terrain across X at the same scale instead of enlarging it.
        float freqScale = 1200f / worldDepth;
        var noises = BuildNoises(seed, freqScale, in settings);

        float halfX = worldWidth * 0.5f;
        float halfZ = worldDepth * 0.5f;
        float stepX = worldWidth / resolutionX;
        float stepZ = worldDepth / resolutionZ;
        float[,] heights = new float[resolutionX + 1, resolutionZ + 1];

        for (int z = 0; z <= resolutionZ; z++)
        for (int x = 0; x <= resolutionX; x++)
        {
            heights[x, z] = SampleHeight(-halfX + x * stepX, -halfZ + z * stepZ, halfX, halfZ, in noises, in settings);
        }

        Smooth(heights, resolutionX, resolutionZ);

        (float[] heightField, int gridWidth, int gridDepth, float minHeight, float maxHeight) =
            BakeHeightField(heights, resolutionX, resolutionZ);
        var mesh = BuildMesh(heights, resolutionX, resolutionZ, halfX, halfZ, stepX, stepZ);
        return new(mesh, heightField, gridWidth, gridDepth, minHeight, maxHeight);
    }

    // DomainWarpAmplitude is a world-space distance, so it scales DOWN as the world
    // shrinks -- opposite of Frequency, which scales up.
    private static NoiseSet BuildNoises(int seed, float freqScale, in TerrainSettings settings) => new(
        new()
        {
            Seed = seed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = settings.ContinentOctaves,
            FractalLacunarity = 2.0f,
            FractalGain = 0.5f,
            Frequency = 0.0011f * freqScale,
            DomainWarpEnabled = true,
            DomainWarpType = FastNoiseLite.DomainWarpTypeEnum.SimplexReduced,
            DomainWarpAmplitude = 60f / freqScale,
            DomainWarpFrequency = 0.004f * freqScale,
            DomainWarpFractalType = FastNoiseLite.DomainWarpFractalTypeEnum.Progressive,
            DomainWarpFractalOctaves = 3
        },
        new()
        {
            Seed = seed + 101,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = settings.DetailOctaves,
            FractalLacunarity = 2.1f,
            FractalGain = 0.5f,
            Frequency = 0.0030f * freqScale
        },
        new()
        {
            Seed = seed + 202,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Ridged,
            FractalOctaves = settings.MountainOctaves,
            FractalLacunarity = 2.0f,
            FractalGain = 0.5f,
            Frequency = 0.0019f * freqScale
        },
        new()
        {
            Seed = seed + 303,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 2,
            Frequency = 0.0012f * freqScale
        }
    );

    private static float SampleHeight(
        float wx, float wz, float halfX, float halfZ, in NoiseSet n, in TerrainSettings settings)
    {
        // Elliptical falloff: normalize each axis by its own half-extent so the land fills
        // a rectangle of the world's aspect (a circle when worldWidth == worldDepth). With
        // IslandFalloff off the terrain runs edge to edge (no coastline shaping).
        float nx = wx / halfX;
        float nz = wz / halfZ;
        float d = Mathf.Sqrt(nx * nx + nz * nz);
        float falloff = settings.IslandFalloff ? 1f - Mathf.SmoothStep(0.6f, 1.05f, d) : 1f;

        float cont = n.Continent.GetNoise2D(wx, wz) * 0.5f + 0.5f;
        float det = n.Detail.GetNoise2D(wx, wz);
        float t = Mathf.Clamp((cont + det * 0.008f) * falloff, 0f, 1f);

        float baseHeight = Mathf.Lerp(settings.BaseLow, settings.BaseHigh, Mathf.Pow(t, 1.4f));

        // Keep spawn center as dry land without disturbing beaches elsewhere.
        // Only relevant where a player spawns; a top-down map skips it so the
        // center is not an artificial flat disc.
        float center = settings.CenterFlatten ? Mathf.Clamp(1f - d / 0.14f, 0f, 1f) : 0f;
        baseHeight = Mathf.Lerp(baseHeight, Mathf.Max(baseHeight, 7f), center);

        // River only cuts through lowland; excluded from spawn center and mountains.
        // X position/width scale with the world width, the meander wavelength with depth.
        float river = 0f;
        if (settings.RiverEnabled)
        {
            float worldWidth = halfX * 2f;
            float worldDepth = halfZ * 2f;
            float riverX = worldWidth * 0.18f + Mathf.Sin(wz * (Mathf.Tau / (worldDepth * 0.7f))) * worldWidth * 0.10f +
                           Mathf.Sin(wz * (Mathf.Tau / (worldDepth * 0.27f))) * worldWidth * 0.04f;
            river = (1f - Mathf.SmoothStep(worldWidth * 0.018f, worldWidth * 0.05f, Mathf.Abs(wx - riverX))) *
                    (1f - Mathf.SmoothStep(6f, 20f, baseHeight)) * (1f - center);
            baseHeight = Mathf.Lerp(baseHeight, -2.5f, river);
        }

        float ridge = n.Mountain.GetNoise2D(wx, wz) * 0.5f + 0.5f;
        float mask = Mathf.SmoothStep(0.5f, 0.72f, n.MountainMask.GetNoise2D(wx, wz) * 0.5f + 0.5f);
        float mountainHeight =
            Mathf.Pow(ridge, 2.2f) * mask * falloff * (1f - river) * (1f - center) * settings.MountainHeight;

        return baseHeight + mountainHeight;
    }

    private static void Smooth(float[,] heights, int resolutionX, int resolutionZ, int passes = 3)
    {
        for (int pass = 0; pass < passes; pass++)
        {
            float[,] src = (float[,])heights.Clone();
            for (int z = 1; z < resolutionZ; z++)
            {
                for (int x = 1; x < resolutionX; x++)
                {
                    heights[x, z] = BoxAverage(src, x, z);
                }
            }
        }
    }

    // Mean of the 3x3 neighborhood centered on (x, z).
    private static float BoxAverage(float[,] src, int x, int z)
    {
        float sum = 0f;
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                sum += src[x + dx, z + dz];
            }
        }

        return sum / 9f;
    }

    private static (float[] field, int gridWidth, int gridDepth, float minHeight, float maxHeight) BakeHeightField(
        float[,] heights, int resolutionX, int resolutionZ)
    {
        int gridWidth = resolutionX + 1;
        int gridDepth = resolutionZ + 1;
        float[] field = new float[gridWidth * gridDepth];
        float minH = float.MaxValue, maxH = float.MinValue;
        for (int z = 0; z <= resolutionZ; z++)
        {
            for (int x = 0; x <= resolutionX; x++)
            {
                float h = heights[x, z];
                field[z * gridWidth + x] = h;
                minH = Mathf.Min(minH, h);
                maxH = Mathf.Max(maxH, h);
            }
        }

        return (field, gridWidth, gridDepth, minH, maxH);
    }

    private static ArrayMesh BuildMesh(
        float[,] heights, int resolutionX, int resolutionZ, float halfX, float halfZ, float stepX, float stepZ)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        for (int z = 0; z < resolutionZ; z++)
        {
            for (int x = 0; x < resolutionX; x++)
            {
                var v00 = Vert(x, z, halfX, halfZ, stepX, stepZ, heights);
                var v10 = Vert(x + 1, z, halfX, halfZ, stepX, stepZ, heights);
                var v01 = Vert(x, z + 1, halfX, halfZ, stepX, stepZ, heights);
                var v11 = Vert(x + 1, z + 1, halfX, halfZ, stepX, stepZ, heights);

                st.AddVertex(v00);
                st.AddVertex(v01);
                st.AddVertex(v11);
                st.AddVertex(v00);
                st.AddVertex(v11);
                st.AddVertex(v10);
            }
        }

        st.Index();
        st.GenerateNormals();
        return st.Commit();
    }

    private static Vector3 Vert(int x, int z, float halfX, float halfZ, float stepX, float stepZ, float[,] h) =>
        new(-halfX + x * stepX, h[x, z], -halfZ + z * stepZ);
}
