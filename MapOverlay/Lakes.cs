using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MapOverlay
{
    // Shapes the terrain around the water OpenStreetMap maps. Elevation data is flat at 0 m over
    // the sea, which would leave only a film of water, so the sea gets a real floor and low land
    // stays above it. Lakes get a bed under their flat surface, a shore a little above the water
    // and sources that hold them at their real level.
    internal static class Lakes
    {
        public struct Source
        {
            public double X, Z;        // game metres
            public float Radius;       // game metres
            public float SurfaceReal;  // real metres
            public float BedReal;      // real metres
        }

        // A place to compare the water the simulation holds with the water that was planned.
        public struct Probe
        {
            public double X, Z;
            public float Width;
            public float Surface;      // real metres
            public bool Lake;
        }

        public sealed class Result
        {
            public readonly List<Source> Sources = new List<Source>();
            public readonly List<Probe> Probes = new List<Probe>();
            public int LakeCount;
            public int RiverCount;
            public int Cells;                // side of the grids below
            public int[] Label;              // lake id per cell, negative outside lakes (row 0 south)
            public bool[] Sea;               // cells at sea level: the editor's sea level fills them
            public byte[] Mask;              // OsmWater classes per cell
            public float[] RiverInside;      // cells from the bank, inside river polygons
            public float[] Surface;          // real metres of planned water per heightmap pixel, NaN where none
            public bool HasSea;
            public bool Processed;           // the terrain was fitted to the water
            public readonly List<float> Surfaces = new List<float>(); // real metres per lake id
        }

        public const int kCells = 1434;             // ~10 m cells over the playable area at 1:1
        private const int kMinLakeCells = 150;      // ~1.5 ha: smaller ponds are left out
        private const float kMinDepth = 1.5f;       // metres at the shore
        private const float kMaxDepth = 10f;
        private const float kSlope = 0.12f;         // depth gained per metre from the shore
        private const int kBankCells = 3;           // shore band raised above the water
        private const float kBankHeight = 0.6f;
        private const float kMaxSurfaceRange = 15f; // OSM says it is a lake, so only clear nonsense is dropped
        private const float kMinSeaSurface = 1f;    // lakes this low on a coast are at sea level
        private const float kSeaConnect = 14f;      // rivers lower than this join the sea: the data over city rivers reads metres too high
        private const float kSeaMinDepth = 2f;
        private const float kSeaMaxDepth = 25f;
        private const float kSeaSlope = 0.05f;
        private const float kLowLand = 2f;          // on a coast, land below this is squeezed into 1..2 m
        private const float kMinRadius = 12f;
        private const float kMaxRadius = 2400f;     // the editor allows 2500
        private const float kCoverage = 0.9f;       // of a source's radius counts as covered: water spreads over the gaps
        private const float kMinExtraRadius = 30f;  // narrow arms get no circles of their own: the water is placed directly
        private const int kMaxSourcesPerLake = 60;
        private const int kMaxSources = 400;

        // `heights`: res² real metres over a `size` square centred on the world origin, row 0 south.
        // `mask`: kCells² OsmWater classes. `lakes` off leaves lakes alone; the sea is always shaped.
        public static Result Process(float[] heights, int res, double size, byte[] mask, bool lakes)
        {
            var result = new Result { Cells = kCells, Mask = mask, Processed = true };
            result.Surface = new float[heights.Length];
            for (int i = 0; i < result.Surface.Length; i++) result.Surface[i] = float.NaN;
            var planned = result.Surface;
            int n = kCells;
            double cell = size / n;
            float CellHeight(int c) => HeightAt(heights, res, size, CellX(c % n, cell, size), CellX(c / n, cell, size));

            // 1. The sea, and the low rivers and docks that open into it.
            var sea = new bool[n * n];
            var queue = new int[n * n];
            int head = 0, tail = 0;
            for (int i = 0; i < mask.Length; i++)
                if (mask[i] == OsmWater.kOcean)
                {
                    sea[i] = true;
                    queue[tail++] = i;
                }
            result.HasSea = tail > 0;
            while (head < tail)
            {
                int c = queue[head++];
                int r = c / n, k = c % n;
                if (k > 0) VisitSea(c - 1);
                if (k < n - 1) VisitSea(c + 1);
                if (r > 0) VisitSea(c - n);
                if (r < n - 1) VisitSea(c + n);
            }
            void VisitSea(int v)
            {
                if (sea[v] || mask[v] != OsmWater.kRiver || CellHeight(v) > kSeaConnect) return;
                sea[v] = true;
                queue[tail++] = v;
            }
            result.Sea = sea;

            // 2. Connected lakes (4-neighbour), big enough and above the sea.
            var label = new int[n * n];
            for (int i = 0; i < label.Length; i++) label[i] = -1;
            result.Label = label;
            var surfaces = result.Surfaces;
            var lakeCells = new List<List<int>>();
            var scratch = new List<float>();
            for (int start = 0; lakes && start < mask.Length; start++)
            {
                if (mask[start] != OsmWater.kLake || label[start] != -1) continue;
                var cells = new List<int>();
                head = tail = 0;
                queue[tail++] = start;
                label[start] = -2;
                while (head < tail)
                {
                    int c = queue[head++];
                    cells.Add(c);
                    int r = c / n, k = c % n;
                    if (k > 0) Visit(c - 1);
                    if (k < n - 1) Visit(c + 1);
                    if (r > 0) Visit(c - n);
                    if (r < n - 1) Visit(c + n);
                }
                void Visit(int v)
                {
                    if (mask[v] == OsmWater.kLake && label[v] == -1) { label[v] = -2; queue[tail++] = v; }
                }

                // The elevation data is roughly flat over water; its median is the lake's surface.
                scratch.Clear();
                int step = Math.Max(1, cells.Count / 4000);
                for (int i = 0; i < cells.Count; i += step) scratch.Add(CellHeight(cells[i]));
                scratch.Sort();
                float surface = scratch[scratch.Count / 2];
                float spread = scratch[scratch.Count * 9 / 10] - scratch[scratch.Count / 10];

                if (result.HasSea && surface < kMinSeaSurface)
                {
                    foreach (int c in cells)
                    {
                        label[c] = -3;
                        sea[c] = true;
                    }
                    continue;
                }
                int id = cells.Count >= kMinLakeCells && spread <= kMaxSurfaceRange ? surfaces.Count : -3;
                foreach (int c in cells) label[c] = id;
                if (id >= 0)
                {
                    surfaces.Add(surface);
                    lakeCells.Add(cells);
                }
            }
            result.LakeCount = surfaces.Count;

            // 3. Distances in cells: to the shore inside water, to the nearest lake outside it.
            var inside = Chamfer(n, i => label[i] < 0, null, out _);
            var outside = Chamfer(n, i => label[i] >= 0, label, out var nearest);
            var seaInside = result.HasSea ? Chamfer(n, i => !sea[i], null, out _) : null;
            result.RiverInside = Chamfer(n, i => mask[i] != OsmWater.kRiver, null, out _);
            bool hasSea = result.HasSea;

            // 4. Shape the heightmap.
            double px = size / res;
            Parallel.For(0, res, row =>
            {
                double fr = ((row + 0.5) * px) / cell - 0.5;
                int cr = Clamp((int)Math.Round(fr), n);
                for (int col = 0; col < res; col++)
                {
                    double fc = ((col + 0.5) * px) / cell - 0.5;
                    int cc = Clamp((int)Math.Round(fc), n);
                    int c = cr * n + cc;
                    int i = row * res + col;
                    int lake = label[c];
                    if (sea[c])
                    {
                        float d = Bilinear(seaInside, n, fc, fr) * (float)cell;
                        float depth = Math.Min(kSeaMaxDepth, kSeaMinDepth + d * kSeaSlope);
                        heights[i] = Math.Min(heights[i], -depth);
                    }
                    else if (lake >= 0)
                    {
                        float d = Bilinear(inside, n, fc, fr) * (float)cell;
                        float depth = Math.Min(kMaxDepth, kMinDepth + d * kSlope);
                        heights[i] = Math.Min(heights[i], surfaces[lake] - depth);
                        planned[i] = surfaces[lake];
                    }
                    else if (mask[c] == OsmWater.kNone)
                    {
                        if (outside[c] <= kBankCells && nearest[c] >= 0)
                            heights[i] = Math.Max(heights[i], surfaces[nearest[c]] + kBankHeight);
                        // The data dips below zero over cities and fills: that is still dry land.
                        if (hasSea && heights[i] < kLowLand)
                            heights[i] = 1f + Math.Max(0f, heights[i]) / 2f;
                    }
                }
            });

            // 5. Sources: circles inside each lake, deepest point first, until it is covered.
            for (int lake = 0; lake < lakeCells.Count && result.Sources.Count < kMaxSources; lake++)
            {
                var cells = lakeCells[lake];
                cells.Sort((a, b) => inside[b].CompareTo(inside[a]));
                var covered = new HashSet<int>();
                int placed = 0;
                foreach (int c in cells)
                {
                    if (placed >= kMaxSourcesPerLake || result.Sources.Count >= kMaxSources) break;
                    if (covered.Contains(c)) continue;
                    float radius = Math.Min(kMaxRadius, inside[c] * (float)cell);
                    if (radius < kMinRadius || (placed > 0 && radius < kMinExtraRadius))
                    {
                        if (placed > 0) break;
                        radius = kMinRadius; // tiny lake: still give it one source
                    }
                    double x = CellX(c % n, cell, size), z = CellX(c / n, cell, size);
                    result.Sources.Add(new Source
                    {
                        X = x,
                        Z = z,
                        Radius = radius,
                        SurfaceReal = surfaces[lake],
                        BedReal = HeightAt(heights, res, size, x, z),
                    });
                    result.Probes.Add(new Probe { X = x, Z = z, Width = radius * 2f, Surface = surfaces[lake], Lake = true });
                    placed++;
                    int reach = (int)Math.Ceiling(radius * kCoverage / cell);
                    int r0 = c / n, c0 = c % n;
                    for (int dr = -reach; dr <= reach; dr++)
                        for (int dc = -reach; dc <= reach; dc++)
                        {
                            int r = r0 + dr, k = c0 + dc;
                            if (r < 0 || k < 0 || r >= n || k >= n || dr * dr + dc * dc > reach * reach) continue;
                            covered.Add(r * n + k);
                        }
                }
            }
            return result;
        }

        private static double CellX(int index, double cell, double size) => -size / 2 + (index + 0.5) * cell;

        private static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;

        private static float HeightAt(float[] heights, int res, double size, double x, double z)
        {
            double px = size / res;
            return Bilinear(heights, res, (x + size / 2) / px - 0.5, (z + size / 2) / px - 0.5);
        }

        private static float Bilinear(float[] m, int n, double fx, double fy)
        {
            if (fx < 0) fx = 0; else if (fx > n - 1) fx = n - 1;
            if (fy < 0) fy = 0; else if (fy > n - 1) fy = n - 1;
            int ix = Math.Min((int)fx, n - 2), iy = Math.Min((int)fy, n - 2);
            float ax = (float)(fx - ix), ay = (float)(fy - iy);
            int i = iy * n + ix;
            float top = m[i] + (m[i + 1] - m[i]) * ax;
            float bottom = m[i + n] + (m[i + n + 1] - m[i + n]) * ax;
            return top + (bottom - top) * ay;
        }

        // Two-pass chamfer distance (in cells) to the nearest seed cell; also carries the seed's
        // label when `labels` is given.
        private static float[] Chamfer(int n, Func<int, bool> seed, int[] labels, out int[] nearest)
        {
            const float diag = 1.41421356f;
            var d = new float[n * n];
            nearest = new int[n * n];
            for (int i = 0; i < d.Length; i++)
            {
                bool s = seed(i);
                d[i] = s ? 0f : float.MaxValue;
                nearest[i] = s && labels != null ? labels[i] : -1;
            }
            void Relax(int i, int j, float w, int[] near)
            {
                if (d[j] == float.MaxValue) return;
                float v = d[j] + w;
                if (v < d[i]) { d[i] = v; near[i] = near[j]; }
            }
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    int i = r * n + c;
                    if (c > 0) Relax(i, i - 1, 1f, nearest);
                    if (r > 0)
                    {
                        Relax(i, i - n, 1f, nearest);
                        if (c > 0) Relax(i, i - n - 1, diag, nearest);
                        if (c < n - 1) Relax(i, i - n + 1, diag, nearest);
                    }
                }
            for (int r = n - 1; r >= 0; r--)
                for (int c = n - 1; c >= 0; c--)
                {
                    int i = r * n + c;
                    if (c < n - 1) Relax(i, i + 1, 1f, nearest);
                    if (r < n - 1)
                    {
                        Relax(i, i + n, 1f, nearest);
                        if (c < n - 1) Relax(i, i + n + 1, diag, nearest);
                        if (c > 0) Relax(i, i + n - 1, diag, nearest);
                    }
                }
            return d;
        }
    }
}
