using System;
using System.Threading.Tasks;

namespace MapOverlay
{
    // Elevation data is blocky and noisy where the land is flat, and the game shows every step.
    // Flat ground is smoothed strongly; hills and mountains, which differ a lot from their
    // smoothed version, keep their shape. A credible map matters more than exact heights.
    internal static class TerrainSmoothing
    {
        // Low, medium, high. Game metres of blur on flat ground, and how far (real metres) ground
        // must stand from its smoothed version to start and finish keeping its detail.
        private static readonly float[] kFlatMetres = { 60f, 120f, 220f };
        private static readonly float[] kDetailFrom = { 2f, 3f, 5f };
        private static readonly float[] kDetailTo = { 8f, 15f, 30f };
        private static readonly double[] kSourcePixels = { 0.5, 1.0, 1.5 }; // blur everywhere, in source pixels

        // `heights`: res² metres. `px`: game metres per pixel. `sourcePx`: game metres per source pixel.
        public static void Apply(float[] heights, int res, double px, double sourcePx, int level)
        {
            level = Math.Max(0, Math.Min(2, level));
            int light = (int)Math.Round(sourcePx / px * kSourcePixels[level]);
            if (light > 0) BoxBlur(heights, res, light, 2);

            int wide = Math.Max(1, (int)Math.Round(kFlatMetres[level] / px));
            var flat = (float[])heights.Clone();
            BoxBlur(flat, res, wide, 2);
            var detail = new float[heights.Length];
            Parallel.For(0, heights.Length, i => detail[i] = Math.Abs(heights[i] - flat[i]));
            BoxBlur(detail, res, Math.Max(1, wide / 2), 1);

            float from = kDetailFrom[level], span = kDetailTo[level] - from;
            Parallel.For(0, heights.Length, i =>
            {
                float t = (detail[i] - from) / span;
                t = t < 0f ? 0f : t > 1f ? 1f : t;
                heights[i] = flat[i] + (heights[i] - flat[i]) * (t * t * (3f - 2f * t));
            });
        }

        // Separable running-sum box blur; two passes look close to a Gaussian.
        public static void BoxBlur(float[] data, int size, int radius, int passes)
        {
            for (int pass = 0; pass < passes; pass++)
            {
                Parallel.For(0, size, () => new float[size], (row, _, line) =>
                {
                    BlurLine(data, row * size, 1, size, radius, line);
                    return line;
                }, _ => { });
                Parallel.For(0, size, () => new float[size], (col, _, line) =>
                {
                    BlurLine(data, col, size, size, radius, line);
                    return line;
                }, _ => { });
            }
        }

        private static void BlurLine(float[] data, int start, int step, int count, int radius, float[] line)
        {
            for (int i = 0; i < count; i++) line[i] = data[start + i * step];
            double sum = 0;
            for (int i = -radius; i <= radius; i++) sum += line[Math.Max(0, Math.Min(count - 1, i))];
            float inv = 1f / (2 * radius + 1);
            for (int i = 0; i < count; i++)
            {
                data[start + i * step] = (float)(sum * inv);
                sum += line[Math.Min(count - 1, i + radius + 1)] - line[Math.Max(0, i - radius)];
            }
        }
    }
}
