using Godot;

namespace TopographicCameraShader.Demo;

// Bakes the large, high-fidelity showcase island used for the README marketing
// map. Unlike TerrainBaker (the walkable demo island), this saves only the mesh:
// nothing walks this terrain, so there is no collider, and it must not touch the
// shipped addon compositor. Run headless:
//   godot --headless res://Demo/scenes/BigTerrainBake.tscn
public partial class BigTerrainBaker : Node
{
    // Wide, banner-shaped landmass (2.5:1) for the README hero image.
    private const float WorldWidth = 3600f;
    private const float WorldDepth = 1440f;
    private const int ResolutionZ = 600;
    private const int Seed = 20260623;

    private const string MeshPath = "res://Demo/assets/big_terrain.res";

    public override void _Ready()
    {
        GD.Print("BigTerrainBaker: starting bake...");

        var settings = TerrainSettings.Island with
        {
            ContinentOctaves = 5,
            DetailOctaves = 5,
            MountainOctaves = 7,
            BaseHigh = 38f,
            MountainHeight = 120f,
            CenterFlatten = false,
            IslandFalloff = false
        };

        var bake = TerrainGenerator.CreateTerrain(WorldWidth, WorldDepth, ResolutionZ, Seed, settings);

        var err = ResourceSaver.Save(bake.Mesh, MeshPath);
        if (err != Error.Ok)
        {
            GD.PrintErr($"BigTerrainBaker: failed to save mesh: {err}");
        }
        else
        {
            GD.Print($"BigTerrainBaker: saved {MeshPath}. Height range: " +
                     $"{bake.MinHeight:F1} .. {bake.MaxHeight:F1} (world Y)");
        }

        GD.Print("BigTerrainBaker: done.");
        GetTree().Quit();
    }
}
