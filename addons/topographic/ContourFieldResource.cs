using Godot;

namespace TopographicMap;

// Serializable container for a baked ContourField. Stores the field as packed
// primitive arrays (Godot serializes these efficiently in a .res); bounding boxes
// and major flags are not stored, ContourFieldSerializer recomputes them on load.
// A thin bridge: the pure ContourFieldSerializer does the real work.
[GlobalClass]
public partial class ContourFieldResource : Resource
{
    [Export] public float[] PointsXy { get; set; } = [];
    [Export] public int[] PointCounts { get; set; } = [];
    [Export] public float[] Levels { get; set; } = [];
    [Export] public float HeightMin { get; set; } = -40f;
    [Export] public float HeightMax { get; set; } = 110f;
    [Export] public float Interval { get; set; } = 10f;
    [Export] public int MajorEvery { get; set; } = 5;

    public static ContourFieldResource FromField(ContourField field, float heightMin, float heightMax,
        float interval, int majorEvery)
    {
        ContourFieldSerializer.Flatten(field, out float[] pointsXy, out int[] pointCounts, out float[] levels);
        return new()
        {
            PointsXy = pointsXy,
            PointCounts = pointCounts,
            Levels = levels,
            HeightMin = heightMin,
            HeightMax = heightMax,
            Interval = interval,
            MajorEvery = majorEvery
        };
    }

    public ContourField ToField() =>
        ContourFieldSerializer.Inflate(PointsXy, PointCounts, Levels, HeightMin, HeightMax, Interval, MajorEvery);
}
