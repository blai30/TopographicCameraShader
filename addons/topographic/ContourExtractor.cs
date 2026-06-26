using System;

namespace TopographicMap;

// Turns the raw bytes of a height-buffer image (Rgbaf: R = normalized height,
// G = coverage mask, 16 bytes per pixel) into a ContourField. Pure C# with no
// Godot types, so the heavy Marching Squares pass can run on a background thread.
// Optionally box-downsamples to maxResolution; at full resolution the line
// crossings line up with the per-pixel tint so band edges do not bleed.
public static class ContourExtractor
{
    public static ContourField Build(byte[] data, int srcW, int srcH, float heightMin,
        float heightMax, float interval, int majorEvery, int maxResolution)
    {
        const int stride = 16; // bytes per pixel in Rgbaf (4 channels x 4 bytes)
        int step = Math.Max(1, (Math.Max(srcW, srcH) + maxResolution - 1) / maxResolution);
        int cols = (srcW + step - 1) / step;
        int rows = (srcH + step - 1) / step;

        float[] field = new float[cols * rows];
        float[] mask = new float[cols * rows];
        for (int ry = 0; ry < rows; ry++)
        {
            for (int rx = 0; rx < cols; rx++)
            {
                int index = ry * cols + rx;
                (field[index], mask[index]) = SampleBlock(data, srcW, srcH, stride, step, rx, ry);
            }
        }

        return ContourField.Build(field, mask, cols, rows, heightMin, heightMax, interval, majorEvery);
    }

    // Box-averages a step x step block of the source buffer (a single texel at full
    // resolution), clamped at the right/bottom edges. Returns the average normalized
    // height and a coverage mask thresholded at 0.5. The clamped block is never empty,
    // since cols/rows are sized so every block start lands inside the source.
    private static (float Field, float Mask) SampleBlock(byte[] data, int srcW, int srcH, int stride, int step,
        int rx, int ry)
    {
        int x0 = rx * step, x1 = Math.Min(x0 + step, srcW);
        int y0 = ry * step, y1 = Math.Min(y0 + step, srcH);

        float sum = 0f;
        float maskSum = 0f;
        for (int sy = y0; sy < y1; sy++)
        {
            for (int sx = x0; sx < x1; sx++)
            {
                int offset = (sy * srcW + sx) * stride;
                sum += BitConverter.ToSingle(data, offset);
                maskSum += BitConverter.ToSingle(data, offset + 4);
            }
        }

        int count = (x1 - x0) * (y1 - y0);
        return (sum / count, maskSum / count >= 0.5f ? 1f : 0f);
    }
}
