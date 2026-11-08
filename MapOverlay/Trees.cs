using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MapOverlay
{
    // Scatters trees where the land cover says there is tree cover, on a jittered grid so they
    // do not line up. Positions are planned on a background thread and placed by TerrainBuilder.
    internal static class Trees
    {
        public struct Item
        {
            public float X, Z;
            public float HeightReal;
            public int Seed;
        }

        public const int kMaxTrees = 250000;
        private const float kKeep = 0.85f; // holes make the forest look natural

        // `spacing`: game metres between trees. `heights` is the finished heightmap, row 0 south.
        public static List<Item> Plan(MapFrame frame, float[] heights, int res, double size, CoverReader cover, Lakes.Result lakes, bool[] blocked, float spacing)
        {
            int side = (int)(size / spacing);
            int n = lakes != null && lakes.Cells > 0 ? lakes.Cells : Lakes.kCells;
            double cell = size / n, px = size / res;
            var rows = new List<Item>[side];
            Parallel.For(0, side, row =>
            {
                var list = new List<Item>();
                var random = new Random(row * 7919 + 17);
                for (int col = 0; col < side; col++)
                {
                    int seed = random.Next();
                    double x = -size / 2 + (col + random.NextDouble()) * spacing;
                    double z = -size / 2 + (row + random.NextDouble()) * spacing;
                    if (random.NextDouble() > kKeep) continue;
                    frame.ToGeo(x, z, out double lat, out double lon);
                    if (cover.Sample(lat, lon) != CoverReader.kTrees) continue;
                    int c = Clamp((int)((x + size / 2) / cell), n), r = Clamp((int)((z + size / 2) / cell), n);
                    int index = r * n + c;
                    if (blocked != null && blocked[index]) continue;
                    if (lakes?.Mask != null && lakes.Mask[index] != OsmWater.kNone) continue; // all mapped water, even if not simulated
                    int hc = Clamp((int)((x + size / 2) / px), res), hr = Clamp((int)((z + size / 2) / px), res);
                    list.Add(new Item { X = (float)x, Z = (float)z, HeightReal = heights[hr * res + hc], Seed = seed });
                }
                rows[row] = list;
            });

            int total = 0;
            foreach (var list in rows) total += list.Count;
            double keep = total > kMaxTrees ? (double)kMaxTrees / total : 1.0;
            var thin = new Random(4242);
            var items = new List<Item>(Math.Min(total, kMaxTrees));
            foreach (var list in rows)
                foreach (var item in list)
                    if (keep >= 1.0 || thin.NextDouble() < keep) items.Add(item);
            Mod.log.Info($"Trees: {total} candidates at {spacing} m spacing, placing {items.Count}.");
            return items;
        }

        private static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;
    }
}
