using System.Threading.Tasks;
using Godot;

namespace TopographicMap;

// Runtime contour extraction from a live height-buffer viewport, for dynamic or
// non-heightmap geometry. Renders the viewport once, reads the buffer back, and
// runs the pure ContourExtractor off the main thread so the one-time build does
// not stutter. The demo bakes its contours instead (see TerrainBaker), but this
// keeps the general extraction path a first-class, maintained addon capability.
public static class ContourSource
{
    public static async Task<ContourField> BuildFromViewportAsync(SubViewport viewport,
        float heightMin, float heightMax, float interval, int majorEvery, int maxResolution)
    {
        viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        await viewport.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await viewport.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        var image = viewport.GetTexture().GetImage();
        image.Convert(Image.Format.Rgbaf);
        byte[] data = image.GetData();
        int width = image.GetWidth();
        int height = image.GetHeight();

        return await Task.Run(() => ContourExtractor.Build(
            data, width, height, heightMin, heightMax, interval, majorEvery, maxResolution));
    }
}
