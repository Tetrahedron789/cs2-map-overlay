using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MapOverlay
{
    // Rivers and canals from OpenStreetMap (OsmWater). The data says where a river goes; these
    // rules decide how it looks and behaves in the game: a rounded path, a width that changes
    // gradually, a rounded bed, and banks that always stand above the water, raising the ground
    // where needed. Heights are worked out for the whole network at once, from where it flows
    // out, so pieces meet and nothing dams a river. The water itself is placed directly
    // (TerrainBuilder); sources only hold its level, evenly spaced and always inside the river.
    // Stretches at sea level need no sources: the sea fills them.
    internal static class Rivers
    {
        private const float kStep = 12f;          // metres between samples along a river
        private const float kDefaultRiverWidth = 25f;
        private const float kDefaultCanalWidth = 12f;
        private const float kMinWidth = 20f;      // game metres: thinner channels do not hold water well
        private const float kMaxWidth = 600f;
        private const float kChannelScale = 1.2f; // the rounded bed narrows the water inside the channel
        private const float kMinDepth = 2f;
        private const float kMaxDepth = 5f;
        private const float kBelowGround = 1.5f;  // water surface below the ground profile
        private const float kNodeSize = 24f;      // metres: river points this close share a height
        private const float kMinFall = 0.004f;    // per node, about 1:6000: water never stands still
        private const float kFreeboard = 0.8f;    // banks stand this far above the water
        private const float kCutSlope = 0.35f;    // metres up per metre where the ground is cut back
        private const float kMaxCut = 150f;       // metres out from the channel
        private const float kFillSlope = 0.12f;   // metres down per metre where a bank is raised
        private const float kFillReach = 80f;
        private const int kPathPasses = 2;
        private const int kPathWindow = 6;        // samples either side: rounds corners without leaving the river
        private const int kProfileWindow = 8;
        private const int kProfileSmooth = 20;
        private const int kWidthWindow = 12;
        private const float kSourceSpacing = 1200f; // the one value to tune if rivers do not stay full
        private const float kNoSourceBelow = 0.5f; // a surface this low is the sea's
        private const float kMinSourceRadius = 10f;
        private const int kProbeEvery = 20;       // samples: about every 240 m
        private const int kMaxSources = 150;
        private const float kMinLength = 400f;    // shorter runs are left out
        private const double kSnap = 6.0;         // tile pieces that end this close are one river

        private sealed class Way
        {
            public List<(double X, double Z)> Points;
            public float Width;
            public bool Used;
        }

        // Carves the channels into `heights` (real metres, res², row 0 south) and adds the sources
        // to `result`. `blocked` marks the cells the channels cover, for the tree planner.
        public static void Process(List<OsmWater.Line> lines, double scale, float[] heights, int res, double size, Lakes.Result result, bool[] blocked)
        {
            var ways = new List<Way>(lines.Count);
            foreach (var line in lines)
            {
                if (line.Points.Count < 2) continue;
                float width = line.Canal ? kDefaultCanalWidth : kDefaultRiverWidth;
                ways.Add(new Way { Points = line.Points, Width = Math.Max(kMinWidth, width * (float)scale) });
            }
            var chains = Stitch(ways);
            chains.Sort((a, b) => b.Points.Count.CompareTo(a.Points.Count));
            var runs = new List<(List<(double X, double Z)> Points, float Width)>();
            foreach (var chain in chains)
                foreach (var run in Runs(chain.Points, size))
                {
                    SmoothPath(run);
                    runs.Add((run, chain.Width));
                }
            var nodes = new Dictionary<long, int>();
            var network = BuildNetwork(runs, heights, res, size, result, nodes);

            int sources = 0;
            // Channels and banks are gathered first and applied together, so one river's bank
            // never closes another's channel, and every river reads the same ground.
            var carve = new float[heights.Length];
            var raise = new float[heights.Length];
            for (int i = 0; i < carve.Length; i++)
            {
                carve[i] = float.MaxValue;
                raise[i] = float.MinValue;
            }
            foreach (var (points, runWidth) in runs)
            {
                int first = nodes[NodeKey(points[0])];
                if (network[first].Dropped) continue;
                // Water runs from the higher end, as the network says.
                if (network[first].Level < network[nodes[NodeKey(points[points.Count - 1])]].Level) points.Reverse();
                var levels = new float[points.Count];
                for (int i = 0; i < points.Count; i++) levels[i] = network[nodes[NodeKey(points[i])]].Level;
                Plan(points, runWidth, levels, heights, res, size, result, blocked, ref sources, carve, raise);
                result.RiverCount++;
            }

            int n = result.Cells > 0 ? result.Cells : Lakes.kCells;
            Parallel.For(0, res, r =>
            {
                int cr = Math.Min(n - 1, r * n / res);
                for (int c = 0; c < res; c++)
                {
                    int i = r * res + c;
                    float h = heights[i];
                    if (raise[i] > h)
                    {
                        // Banks stop at open water: only channels and dry ground take them.
                        int cell = cr * n + Math.Min(n - 1, c * n / res);
                        bool open = (result.Sea != null && result.Sea[cell]) || (result.Label != null && result.Label[cell] >= 0);
                        if (!open) h = raise[i];
                    }
                    if (carve[i] < h) h = carve[i];
                    heights[i] = h;
                }
            });
            Mod.log.Info($"Rivers: {result.RiverCount} river(s), {sources} source(s).");
        }

        // Moving average with the ends fixed: rounds corners and zigzags.
        private static void SmoothPath(List<(double X, double Z)> run)
        {
            for (int pass = 0; pass < kPathPasses; pass++)
            {
                var source = run.ToArray();
                for (int i = 1; i + 1 < source.Length; i++)
                {
                    int w = Math.Min(kPathWindow, Math.Min(i, source.Length - 1 - i));
                    double x = 0, z = 0;
                    for (int k = i - w; k <= i + w; k++)
                    {
                        x += source[k].X;
                        z += source[k].Z;
                    }
                    run[i] = (x / (2 * w + 1), z / (2 * w + 1));
                }
            }
        }

        private static float[] MovingAverage(float[] values, int window)
        {
            var output = new float[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                float sum = 0;
                int count = 0;
                for (int k = Math.Max(0, i - window); k <= Math.Min(values.Length - 1, i + window); k++)
                {
                    sum += values[k];
                    count++;
                }
                output[i] = sum / count;
            }
            return output;
        }

        private sealed class Node
        {
            public float Level = float.MaxValue;   // ground under the river, then its worked-out profile
            public bool Outlet;
            public bool Dropped;
            public int Component = -1;
            public int Parent = -1;                // next node towards the outlet
            public bool Seen;
            public readonly List<int> Links = new List<int>();
        }

        private static long NodeKey((double X, double Z) p) =>
            ((long)Math.Floor(p.X / kNodeSize) << 32) ^ (uint)(long)Math.Floor(p.Z / kNodeSize);

        // Joins all the rivers into one graph and gives every node a height that only ever falls
        // towards the sea or lake it flows into, however the data came split up.
        private static List<Node> BuildNetwork(List<(List<(double X, double Z)> Points, float Width)> runs, float[] heights, int res, double size,
            Lakes.Result result, Dictionary<long, int> nodes)
        {
            var network = new List<Node>();
            int NodeAt((double X, double Z) p)
            {
                long key = NodeKey(p);
                if (!nodes.TryGetValue(key, out int index))
                {
                    index = network.Count;
                    nodes[key] = index;
                    network.Add(new Node());
                }
                return index;
            }
            void Link(int a, int b)
            {
                if (a == b || network[a].Links.Contains(b)) return;
                network[a].Links.Add(b);
                network[b].Links.Add(a);
            }

            int n = result.Cells > 0 ? result.Cells : Lakes.kCells;
            var scratch = new List<float>(2 * kProfileWindow + 1);
            foreach (var (points, _) in runs)
            {
                var raw = new float[points.Count];
                for (int i = 0; i < points.Count; i++) raw[i] = HeightAt(heights, res, size, points[i]);
                int previous = -1;
                for (int i = 0; i < points.Count; i++)
                {
                    // Median of the ground around the point: spikes do not count.
                    scratch.Clear();
                    for (int k = Math.Max(0, i - kProfileWindow); k <= Math.Min(points.Count - 1, i + kProfileWindow); k++) scratch.Add(raw[k]);
                    scratch.Sort();
                    int node = NodeAt(points[i]);
                    var item = network[node];
                    item.Level = Math.Min(item.Level, scratch[scratch.Count / 2]);
                    int cell = CellIndex(points[i], size, n);
                    if (result.Sea != null && cell >= 0 && result.Sea[cell])
                    {
                        item.Outlet = true;
                        item.Level = Math.Min(item.Level, kBelowGround);
                    }
                    int lake = LakeAt(result, size, points[i]);
                    if (lake >= 0)
                    {
                        item.Outlet = true;
                        item.Level = Math.Min(item.Level, result.Surfaces[lake] + kBelowGround);
                    }
                    if (previous >= 0) Link(previous, node);
                    previous = node;
                }
            }

            // Pieces that end next to each other are the same river.
            foreach (var (points, _) in runs)
                foreach (var end in new[] { points[0], points[points.Count - 1] })
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                            if (nodes.TryGetValue(NodeKey((end.X + dx * kNodeSize, end.Z + dz * kNodeSize)), out int other))
                                Link(nodes[NodeKey(end)], other);

            // Connected networks: too short overall is the only reason to leave one out.
            var stack = new Stack<int>();
            int components = 0;
            for (int i = 0; i < network.Count; i++)
            {
                if (network[i].Component >= 0) continue;
                network[i].Component = components;
                stack.Push(i);
                while (stack.Count > 0)
                    foreach (int next in network[stack.Pop()].Links)
                        if (network[next].Component < 0)
                        {
                            network[next].Component = components;
                            stack.Push(next);
                        }
                components++;
            }
            var lengths = new double[components];
            foreach (var (points, _) in runs) lengths[network[nodes[NodeKey(points[0])]].Component] += points.Count * kStep;

            // A network that reaches no sea or lake flows out at its lowest point.
            var lowest = new int[components];
            var hasOutlet = new bool[components];
            for (int i = 0; i < components; i++) lowest[i] = -1;
            for (int i = 0; i < network.Count; i++)
            {
                var item = network[i];
                item.Dropped = lengths[item.Component] < kMinLength;
                if (item.Outlet) hasOutlet[item.Component] = true;
                if (lowest[item.Component] < 0 || item.Level < network[lowest[item.Component]].Level) lowest[item.Component] = i;
            }
            var order = new List<int>(network.Count);
            for (int i = 0; i < network.Count; i++)
            {
                var item = network[i];
                if (item.Outlet || (!hasOutlet[item.Component] && lowest[item.Component] == i))
                {
                    item.Seen = true;
                    order.Add(i);
                }
            }
            for (int head = 0; head < order.Count; head++)
                foreach (int next in network[order[head]].Links)
                    if (!network[next].Seen)
                    {
                        network[next].Seen = true;
                        network[next].Parent = order[head];
                        order.Add(next);
                    }

            // From the farthest headwaters down: each node passes its height on, a little lower.
            for (int i = order.Count - 1; i >= 0; i--)
            {
                var item = network[order[i]];
                if (item.Parent >= 0)
                {
                    var parent = network[item.Parent];
                    parent.Level = Math.Min(parent.Level, item.Level - kMinFall);
                }
            }
            if (result.HasSea)
                foreach (var item in network)
                    if (item.Level < kBelowGround) item.Level = kBelowGround; // the surface meets the sea, not below it
            return network;
        }

        private static long Bucket(double x, double z) =>
            ((long)Math.Floor(x / kSnap) << 32) ^ (uint)(long)Math.Floor(z / kSnap);

        private static double Distance((double X, double Z) a, (double X, double Z) b)
        {
            double dx = a.X - b.X, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        // Rivers arrive as many pieces in no particular direction: joins those whose ends meet,
        // longest pieces first so the main stream stays whole. Branches become their own chains.
        private static List<Way> Stitch(List<Way> ways)
        {
            var ends = new Dictionary<long, List<Way>>();
            void Add((double X, double Z) p, Way way)
            {
                long key = Bucket(p.X, p.Z);
                if (!ends.TryGetValue(key, out var list)) ends[key] = list = new List<Way>();
                list.Add(way);
            }
            foreach (var way in ways)
            {
                Add(way.Points[0], way);
                Add(way.Points[way.Points.Count - 1], way);
            }
            Way Next((double X, double Z) p)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!ends.TryGetValue(Bucket(p.X + dx * kSnap, p.Z + dz * kSnap), out var list)) continue;
                        foreach (var way in list)
                            if (!way.Used && (Distance(way.Points[0], p) <= kSnap || Distance(way.Points[way.Points.Count - 1], p) <= kSnap))
                                return way;
                    }
                return null;
            }
            void Extend(Way chain)
            {
                while (true)
                {
                    var tail = chain.Points[chain.Points.Count - 1];
                    var next = Next(tail);
                    if (next == null) return;
                    next.Used = true;
                    bool forward = Distance(next.Points[0], tail) <= Distance(next.Points[next.Points.Count - 1], tail);
                    if (forward) chain.Points.AddRange(next.Points);
                    else for (int i = next.Points.Count - 1; i >= 0; i--) chain.Points.Add(next.Points[i]);
                    chain.Width = Math.Max(chain.Width, next.Width);
                }
            }

            ways.Sort((a, b) => b.Points.Count.CompareTo(a.Points.Count));
            var chains = new List<Way>();
            foreach (var way in ways)
            {
                if (way.Used) continue;
                way.Used = true;
                var chain = new Way { Points = new List<(double X, double Z)>(way.Points), Width = way.Width };
                Extend(chain);
                chain.Points.Reverse();
                Extend(chain);
                chains.Add(chain);
            }
            return chains;
        }

        // Evenly spaced samples, split wherever the line leaves the playable area.
        private static List<List<(double X, double Z)>> Runs(List<(double X, double Z)> points, double size)
        {
            var runs = new List<List<(double X, double Z)>>();
            List<(double X, double Z)> run = null;
            double half = size / 2 - 16;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                double dx = b.X - a.X, dz = b.Z - a.Z, length = Math.Sqrt(dx * dx + dz * dz);
                int steps = Math.Max(1, (int)(length / kStep));
                for (int k = 0; k < steps; k++)
                {
                    double t = k / (double)steps, x = a.X + dx * t, z = a.Z + dz * t;
                    if (Math.Abs(x) < half && Math.Abs(z) < half)
                    {
                        if (run == null) runs.Add(run = new List<(double X, double Z)>());
                        run.Add((x, z));
                    }
                    else run = null;
                }
            }
            return runs;
        }

        private static void Plan(List<(double X, double Z)> run, float width, float[] levels, float[] heights, int res, double size,
            Lakes.Result result, bool[] blocked, ref int sources, float[] carve, float[] raise)
        {
            var raw = new float[run.Count];
            for (int i = 0; i < run.Count; i++) raw[i] = HeightAt(heights, res, size, run[i]);
            var profile = MovingAverage(levels, kProfileSmooth);
            var ground = MovingAverage(raw, kProfileSmooth); // sets how far banks are cut back, without lumps

            int n = result.Cells > 0 ? result.Cells : Lakes.kCells;
            double cell = size / n;

            // As wide as the river's polygon around the line, changing gradually.
            var mapped = new float[run.Count];
            for (int i = 0; i < run.Count; i++)
            {
                int c = CellIndex(run[i], size, n);
                float inside = result.RiverInside != null && c >= 0 ? result.RiverInside[c] : 0f;
                mapped[i] = Math.Min(kMaxWidth, Math.Max(width, 2f * inside * (float)cell));
            }
            mapped = MovingAverage(mapped, kWidthWindow);

            double px = size / res;
            double sinceSource = kSourceSpacing;
            for (int i = 0; i < run.Count; i++)
            {
                var p = run[i];
                sinceSource += kStep;
                if (LakeAt(result, size, p) >= 0) continue;

                float here = mapped[i];
                float depth = Math.Max(kMinDepth, Math.Min(kMaxDepth, kMinDepth + here / 100f));
                float surface = profile[i] - kBelowGround;
                bool atSea = result.HasSea && surface <= kNoSourceBelow;
                if (i % kProbeEvery == 0 && !atSea)
                    result.Probes.Add(new Lakes.Probe { X = p.X, Z = p.Z, Width = here, Surface = surface });
                float bed = surface - depth, crest = surface + kFreeboard;
                float half = here * kChannelScale / 2f;
                // Far enough to reach the ground where the river runs below it, so no cliff is left.
                float cut = Math.Min(kMaxCut, Math.Max(0f, ground[i] - crest) / kCutSlope + 4f);
                double reach = half + Math.Max(cut, kFillReach);

                int c0 = (int)Math.Floor((p.X - reach + size / 2) / px), c1 = (int)Math.Ceiling((p.X + reach + size / 2) / px);
                int r0 = (int)Math.Floor((p.Z - reach + size / 2) / px), r1 = (int)Math.Ceiling((p.Z + reach + size / 2) / px);
                for (int r = Math.Max(0, r0); r <= Math.Min(res - 1, r1); r++)
                    for (int c = Math.Max(0, c0); c <= Math.Min(res - 1, c1); c++)
                    {
                        double x = -size / 2 + (c + 0.5) * px - p.X, z = -size / 2 + (r + 0.5) * px - p.Z;
                        float d = (float)Math.Sqrt(x * x + z * z);
                        if (d > reach) continue;
                        int pixel = r * res + c;
                        if (d <= half)
                        {
                            // Flat in the middle, curving up to the bank.
                            float t = (d / half - 0.35f) / 0.65f;
                            t = t < 0f ? 0f : t;
                            float target = bed + (crest - bed) * (t * t * (3f - 2f * t));
                            if (target < carve[pixel])
                            {
                                carve[pixel] = target;
                                if (result.Surface != null) result.Surface[pixel] = surface;
                            }
                            continue;
                        }
                        float out_ = d - half;
                        if (out_ <= cut)
                        {
                            float top = crest + out_ * kCutSlope;
                            if (top < carve[pixel]) carve[pixel] = top;
                        }
                        if (out_ <= kFillReach)
                        {
                            float bank = crest - out_ * kFillSlope;
                            if (bank > raise[pixel]) raise[pixel] = bank;
                        }
                    }

                int mark = (int)Math.Ceiling((half + 6) / cell);
                int mc = (int)((p.X + size / 2) / cell), mr = (int)((p.Z + size / 2) / cell);
                for (int r = Math.Max(0, mr - mark); r <= Math.Min(n - 1, mr + mark); r++)
                    for (int c = Math.Max(0, mc - mark); c <= Math.Min(n - 1, mc + mark); c++)
                        blocked[r * n + c] = true;

                if (sinceSource >= kSourceSpacing && sources < kMaxSources && !(result.HasSea && surface <= kNoSourceBelow))
                {
                    sinceSource = 0;
                    sources++;
                    result.Sources.Add(new Lakes.Source
                    {
                        X = p.X,
                        Z = p.Z,
                        Radius = Math.Max(kMinSourceRadius, here / 2f), // fits inside the river
                        SurfaceReal = surface,
                        BedReal = bed,
                    });
                }
            }
        }

        private static int CellIndex((double X, double Z) p, double size, int n)
        {
            double cell = size / n;
            int c = (int)((p.X + size / 2) / cell), r = (int)((p.Z + size / 2) / cell);
            return c < 0 || r < 0 || c >= n || r >= n ? -1 : r * n + c;
        }

        private static int LakeAt(Lakes.Result result, double size, (double X, double Z) p)
        {
            if (result.Label == null) return -1;
            int index = CellIndex(p, size, result.Cells);
            return index < 0 ? -1 : result.Label[index];
        }

        private static float HeightAt(float[] heights, int res, double size, (double X, double Z) p)
        {
            double px = size / res;
            int c = (int)((p.X + size / 2) / px), r = (int)((p.Z + size / 2) / px);
            c = c < 0 ? 0 : c >= res ? res - 1 : c;
            r = r < 0 ? 0 : r >= res ? res - 1 : r;
            return heights[r * res + c];
        }
    }
}
