using System;
using System.Collections.Generic;

namespace MapOverlay
{
    internal enum RoadKind { Motorway, Trunk, Primary, Secondary, Tertiary, Minor, Service, Track, Ramp }

    // A road network ready to place: junctions (nodes) and the roads between them (chains), each
    // chain cut into the pieces that become one game road segment each. Plain data, no game types:
    // it is planned on a background thread and only the heights are filled in on the main one.
    internal sealed class RoadPlan
    {
        // The game picks a road's form from its height over the ground, against a limit of 4 m for
        // roads: from 4 m it is an embankment with retaining walls, and only from 8 m a bridge on
        // pillars; from -4 m an open cutting, and a tunnel only where it is 8 m down at both ends
        // and 12 m somewhere. So a bridge has to clear 8 m and an underpass go 12 m down.
        public const double kClearance = 8.5;   // metres a bridge rises over what it crosses, per level
        public const double kDip = 12.5;        // metres a tunnel sinks under what it crosses
        public const double kRampGrade = 0.08;  // slope of the approaches to a raised bridge or sunk tunnel
        public const double kMaxOffset = 26.0;
        public const float kGroundTolerance = 1f; // closer to the ground than this is on the ground
        public const double kSinkGrade = 0.05;  // slope of a road going down into a cutting
        public const double kNoRampRoom = 120.0; // a junction this close to a bridge leaves it no room to climb

        public sealed class Node
        {
            public double X, Z;
            public float Ground, Y, Elevation;
            internal double Raise, Dip;
        }

        // A point where one piece ends and the next begins; the first and last are the chain's nodes.
        public struct Stop
        {
            public double X, Z, S;      // S: distance along the chain
            public double TX, TZ;       // unit direction of travel
            public float Y, Elevation;  // Elevation: height above the ground, 0 on it, negative in a tunnel
        }

        internal struct Crossing
        {
            public double X, Z, S;
            public int Levels;
            public Chain Under;     // the road on the ground this bridge crosses, if that is what it crosses
            public double UnderS;
        }

        public sealed class Chain
        {
            public int A, B;
            public RoadKind Kind;
            public bool Oneway;
            public int Level;           // above 0 a bridge, below 0 a tunnel
            public double Length;
            public Stop[] Stops;
            public string Name;         // the real street's name, null when it has none
            public bool Spans;          // a bridge at ground level over a road sunk beneath it
            internal List<Crossing> Crossings;
            internal List<(double S, double Depth)> Sunk;
        }

        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Chain> Chains = new List<Chain>();
        public int Pieces, Bridges, Tunnels;

        // Main thread: reads the terrain under every node and stop, lifts bridges over what they
        // cross and sinks tunnels under it, and slopes the roads that lead up to them.
        public void ResolveHeights(Func<double, double, float> ground)
        {
            foreach (var n in Nodes)
            {
                n.Ground = ground(n.X, n.Z);
                n.Raise = n.Dip = 0;
            }

            var degree = new int[Nodes.Count];
            var meets = new List<Chain>[Nodes.Count];
            foreach (var c in Chains)
            {
                c.Sunk = null;
                c.Spans = false;
                degree[c.A]++;
                degree[c.B]++;
                (meets[c.A] ?? (meets[c.A] = new List<Chain>())).Add(c);
                if (c.B != c.A) (meets[c.B] ?? (meets[c.B] = new List<Chain>())).Add(c);
            }

            // A bridge is a straight span between its ends. Where that already clears what it crosses
            // (a viaduct over a valley, a tunnel under a hill) nothing moves. On flat ground something
            // has to give by what is missing: the bridge rises on ramps, or, where junctions right at
            // its ends leave it no room to climb and the road below has a clear run, that road goes
            // down into a cutting and the bridge stays level, as a street does over a sunken motorway.
            foreach (var c in Chains)
            {
                if (c.Crossings == null || c.Level == 0) continue;
                double gA = Nodes[c.A].Ground, gB = Nodes[c.B].Ground, need = 0;
                double room = c.Level > 0 ? Math.Min(Run(c, c.A, degree, meets), Run(c, c.B, degree, meets)) : 0;
                foreach (var x in c.Crossings)
                {
                    double span = gA + (gB - gA) * (c.Length > 0 ? x.S / c.Length : 0);
                    double g = ground(x.X, x.Z);
                    double gap = c.Level > 0 ? span - g : g - span;
                    double missing = (c.Level > 0 ? kClearance * x.Levels : kDip) - gap;
                    if (missing < 0.5) continue;
                    var under = x.Under;
                    if (c.Level > 0 && under != null && x.Levels == 1 && room < kNoRampRoom)
                    {
                        double clear = Math.Min(x.UnderS + Run(under, under.A, degree, meets), under.Length - x.UnderS + Run(under, under.B, degree, meets));
                        if (clear > room)
                        {
                            (under.Sunk ?? (under.Sunk = new List<(double, double)>())).Add((x.UnderS, Math.Min(missing, kMaxOffset)));
                            c.Spans = true;
                            continue;
                        }
                    }
                    need = Math.Max(need, missing);
                }
                need = Math.Min(need, kMaxOffset);
                if (need < 0.5) continue;
                if (c.Level > 0)
                {
                    Nodes[c.A].Raise = Math.Max(Nodes[c.A].Raise, need);
                    Nodes[c.B].Raise = Math.Max(Nodes[c.B].Raise, need);
                }
                else
                {
                    Nodes[c.A].Dip = Math.Max(Nodes[c.A].Dip, need);
                    Nodes[c.B].Dip = Math.Max(Nodes[c.B].Dip, need);
                }
            }
            // A cutting that has not climbed back out by the end of its road goes on past the junction.
            foreach (var c in Chains)
            {
                if (c.Sunk == null) continue;
                Nodes[c.A].Dip = Math.Max(Nodes[c.A].Dip, SunkAt(c, 0));
                Nodes[c.B].Dip = Math.Max(Nodes[c.B].Dip, SunkAt(c, c.Length));
            }
            Spread(true);
            Spread(false);

            foreach (var n in Nodes)
            {
                float offset = (float)(n.Raise - n.Dip);
                n.Y = n.Ground + offset;
                n.Elevation = Math.Abs(offset) < kGroundTolerance ? 0f : offset;
            }

            Bridges = Tunnels = 0;
            foreach (var c in Chains)
            {
                var a = Nodes[c.A];
                var b = Nodes[c.B];
                var stops = c.Stops;
                int last = stops.Length - 1;
                var g = new float[stops.Length];
                var y = new float[stops.Length];
                for (int i = 0; i <= last; i++)
                {
                    g[i] = i == 0 ? a.Ground : i == last ? b.Ground : ground(stops[i].X, stops[i].Z);
                    double f = c.Length > 0 ? stops[i].S / c.Length : 0;
                    if (c.Level != 0) y[i] = (float)(a.Y + (b.Y - a.Y) * f);
                    else
                    {
                        double dip = a.Dip + (b.Dip - a.Dip) * f;
                        if (c.Sunk != null) dip = Math.Max(dip, SunkAt(c, stops[i].S));
                        y[i] = g[i] + (float)(a.Raise + (b.Raise - a.Raise) * f - dip);
                    }
                }
                bool offGround = false;
                for (int i = 0; i <= last; i++)
                {
                    float height = y[i];
                    // Roads on the ground iron out the small bumps of the elevation data.
                    if (c.Level == 0 && i > 0 && i < last) height = 0.25f * y[i - 1] + 0.5f * y[i] + 0.25f * y[i + 1];
                    float e = height - g[i];
                    if (c.Level > 0) e = Math.Max(e, 0f);
                    else if (c.Level < 0) e = Math.Min(e, 0f);
                    if (Math.Abs(e) < kGroundTolerance) e = 0f;
                    if (i == 0) { height = a.Y; e = a.Elevation; }
                    else if (i == last) { height = b.Y; e = b.Elevation; }
                    stops[i].Y = height;
                    stops[i].Elevation = e;
                    if (e != 0f) offGround = true;
                }
                if ((offGround || c.Spans) && c.Level > 0) Bridges++;
                else if (offGround && c.Level < 0) Tunnels++;
            }
        }

        // How deep a road's cuttings put it at a point along it.
        private static double SunkAt(Chain c, double s)
        {
            double depth = 0;
            foreach (var (at, deep) in c.Sunk) depth = Math.Max(depth, deep - kSinkGrade * Math.Abs(s - at));
            return depth;
        }

        // How far a road runs on from one of its ends before the next junction: 0 if that end is
        // one, and through the plain joints where only the kind of road changes.
        private double Run(Chain chain, int node, int[] degree, List<Chain>[] meets)
        {
            double run = 0;
            for (int hop = 0; hop < 8; hop++)
            {
                if (degree[node] >= 3) return run;
                if (degree[node] != 2) return double.MaxValue / 4; // a dead end: all the room there is
                Chain next = meets[node][0] == chain ? (meets[node].Count > 1 ? meets[node][1] : null) : meets[node][0];
                if (next == null || next == chain) return run;
                run += next.Length;
                node = next.A == node ? next.B : next.A;
                chain = next;
            }
            return run;
        }

        // Carries each lifted (or sunk) node's offset out along the roads that meet it, falling by
        // kRampGrade, so the approaches slope instead of stepping.
        private void Spread(bool raise)
        {
            var links = new List<(int Other, double Length)>[Nodes.Count];
            var queue = new Queue<int>();
            for (int i = 0; i < Nodes.Count; i++)
                if ((raise ? Nodes[i].Raise : Nodes[i].Dip) > 0) queue.Enqueue(i);
            if (queue.Count == 0) return;
            foreach (var c in Chains)
            {
                if (c.A == c.B) continue;
                (links[c.A] ?? (links[c.A] = new List<(int, double)>())).Add((c.B, c.Length));
                (links[c.B] ?? (links[c.B] = new List<(int, double)>())).Add((c.A, c.Length));
            }
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                if (links[n] == null) continue;
                double value = raise ? Nodes[n].Raise : Nodes[n].Dip;
                foreach (var (other, length) in links[n])
                {
                    double carried = value - kRampGrade * length;
                    var node = Nodes[other];
                    if (carried <= (raise ? node.Raise : node.Dip) + 0.05) continue;
                    if (raise) node.Raise = carried;
                    else node.Dip = carried;
                    queue.Enqueue(other);
                }
            }
        }
    }

    // Turns the loose road pieces of the map tiles into a connected network.
    //
    // The tiles cut every road at their edges and may drop the shared point where two roads meet,
    // so junctions are found again from the geometry: roads that touch or cross on the same level
    // are joined, and roads that cross on different levels (a bridge over a street) are not. The
    // result is then cleaned for the game: junctions too close together are merged, and each road
    // is cut into curved pieces of a length the game handles well.
    internal static class RoadPlanner
    {
        private const double kSnap = 0.6;        // points closer than this are the same point
        private const double kCutSnap = 3.0;     // the two halves of a road cut at a tile edge
        private const double kMinChain = 9.0;    // shorter roads between two junctions merge them
        private const double kMergeReach = 12.0;
        private const double kMinPiece = 12.0;
        private const double kMaxPiece = 96.0;
        private const double kMaxTurn = 0.7;     // radians of bend in one piece
        private const double kTinyLoop = 40.0;
        private const double kEdgeMargin = 16.0; // roads stop this far inside the playable area

        private struct P
        {
            public double X, Z;
            public P(double x, double z) { X = x; Z = z; }
        }

        private sealed class Line
        {
            public List<P> Points = new List<P>();
            public RoadKind Kind;
            public bool Oneway;
            public int Level;
            public bool CutStart, CutEnd;
            public List<(P Point, int Levels, int Id)> Crossings;
            public List<(P Point, int Id)> Under;
            public List<(int Segment, double T, P Point)> Inserts;
        }

        // detail: 0 main roads, 1 adds streets, 2 adds service roads and tracks.
        public static RoadPlan Build(List<OsmWater.Road> roads, List<OsmWater.RoadName> names, int detail, double half)
        {
            var lines = Collect(roads, detail, half - kEdgeMargin);
            Unify(lines);
            Join(lines);
            var plan = Assemble(lines);
            if (names != null && names.Count > 0) Name(plan, names);
            return plan;
        }

        // ---- Names -------------------------------------------------------------------------

        private const double kNameCell = 32.0;
        private const double kNameReach = 8.0;

        // The tiles carry street names as separate lines drawn along the roads. Each road takes
        // the name most of its pieces run alongside: close by and in the same direction, so a
        // side street does not pick up the name of the avenue it ends on.
        private static void Name(RoadPlan plan, List<OsmWater.RoadName> names)
        {
            var cells = new Dictionary<long, List<(P A, P B, string Name)>>();
            foreach (var line in names)
                for (int i = 0; i + 1 < line.Points.Count; i++)
                {
                    var a = new P(line.Points[i].X, line.Points[i].Z);
                    var b = new P(line.Points[i + 1].X, line.Points[i + 1].Z);
                    long x0 = (long)Math.Floor((Math.Min(a.X, b.X) - kNameReach) / kNameCell), x1 = (long)Math.Floor((Math.Max(a.X, b.X) + kNameReach) / kNameCell);
                    long z0 = (long)Math.Floor((Math.Min(a.Z, b.Z) - kNameReach) / kNameCell), z1 = (long)Math.Floor((Math.Max(a.Z, b.Z) + kNameReach) / kNameCell);
                    // A line that runs off across the map is not a street's name.
                    if ((x1 - x0 + 1) * (z1 - z0 + 1) > 4096) continue;
                    for (long x = x0; x <= x1; x++)
                        for (long z = z0; z <= z1; z++)
                        {
                            long key = Key(x, z);
                            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<(P, P, string)>();
                            list.Add((a, b, line.Name));
                        }
                }

            var votes = new Dictionary<string, int>();
            foreach (var chain in plan.Chains)
            {
                votes.Clear();
                int samples = 0;
                for (int i = 0; i + 1 < chain.Stops.Length; i++)
                {
                    var s = chain.Stops[i];
                    var e = chain.Stops[i + 1];
                    var middle = new P((s.X + e.X) / 2, (s.Z + e.Z) / 2);
                    double dx = e.X - s.X, dz = e.Z - s.Z, size = Math.Sqrt(dx * dx + dz * dz);
                    if (size < 1e-6) continue;
                    samples++;
                    if (!cells.TryGetValue(Key((long)Math.Floor(middle.X / kNameCell), (long)Math.Floor(middle.Z / kNameCell)), out var near)) continue;
                    string best = null;
                    double bestDistance = kNameReach;
                    foreach (var (a, b, name) in near)
                    {
                        double distance = ToSegment(middle, a, b, out _);
                        if (distance >= bestDistance) continue;
                        double nx = b.X - a.X, nz = b.Z - a.Z, length = Math.Sqrt(nx * nx + nz * nz);
                        if (length < 1e-6 || Math.Abs(dx * nx + dz * nz) < 0.85 * size * length) continue;
                        bestDistance = distance;
                        best = name;
                    }
                    if (best == null) continue;
                    votes.TryGetValue(best, out int n);
                    votes[best] = n + 1;
                }
                string winner = null;
                int most = 0;
                foreach (var vote in votes)
                    if (vote.Value > most) { most = vote.Value; winner = vote.Key; }
                if (winner != null && most * 2 >= samples) chain.Name = winner;
            }
        }

        // ---- Input -------------------------------------------------------------------------

        private static bool Classify(OsmWater.Road road, int detail, out RoadKind kind)
        {
            int rank;
            switch (road.Class)
            {
                case "motorway": kind = RoadKind.Motorway; rank = 0; break;
                case "trunk": kind = RoadKind.Trunk; rank = 0; break;
                case "primary": kind = RoadKind.Primary; rank = 0; break;
                case "secondary": kind = RoadKind.Secondary; rank = 0; break;
                case "tertiary": kind = RoadKind.Tertiary; rank = 1; break;
                case "minor": kind = RoadKind.Minor; rank = 1; break;
                case "service": kind = RoadKind.Service; rank = 2; break;
                case "track": kind = RoadKind.Track; rank = 2; break;
                default: kind = RoadKind.Minor; return false;
            }
            if (rank > detail) return false;
            if (road.Ramp && rank < 2) kind = RoadKind.Ramp;
            return true;
        }

        private static List<Line> Collect(List<OsmWater.Road> roads, int detail, double half)
        {
            var lines = new List<Line>();
            foreach (var road in roads)
            {
                if (!Classify(road, detail, out var kind)) continue;
                int level = road.Brunnel > 0 ? Math.Max(road.Layer, 1) : road.Brunnel < 0 ? Math.Min(road.Layer, -1) : 0;
                // Underground service roads are car park ramps and delivery tunnels.
                if (level < 0 && (kind == RoadKind.Service || kind == RoadKind.Track)) continue;
                Line current = null;
                var pts = road.Points;
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    double x0 = pts[i].X, z0 = pts[i].Z, dx = pts[i + 1].X - x0, dz = pts[i + 1].Z - z0;
                    double t0 = 0, t1 = 1;
                    bool inside = Clip(-dx, x0 + half, ref t0, ref t1) && Clip(dx, half - x0, ref t0, ref t1)
                        && Clip(-dz, z0 + half, ref t0, ref t1) && Clip(dz, half - z0, ref t0, ref t1);
                    if (!inside)
                    {
                        current = null;
                        continue;
                    }
                    if (current == null)
                    {
                        current = new Line { Kind = kind, Oneway = road.Oneway, Level = level, CutStart = i == 0 && t0 == 0 && road.CutStart };
                        lines.Add(current);
                        current.Points.Add(new P(x0 + dx * t0, z0 + dz * t0));
                    }
                    current.Points.Add(new P(x0 + dx * t1, z0 + dz * t1));
                    if (t1 < 1) current = null;
                    else if (i + 2 == pts.Count) current.CutEnd = road.CutEnd;
                }
            }
            return lines;
        }

        private static bool Clip(double p, double q, ref double t0, ref double t1)
        {
            if (p == 0) return q >= 0;
            double r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }

        // ---- Shared points -----------------------------------------------------------------

        private sealed class Snapper
        {
            private const double kCell = 4.0;
            private readonly Dictionary<long, List<P>> m_Cells = new Dictionary<long, List<P>>();

            public P Snap(P p, double reach)
            {
                long cx = (long)Math.Floor(p.X / kCell), cz = (long)Math.Floor(p.Z / kCell);
                double best = reach * reach;
                bool found = false;
                P result = p;
                for (long x = cx - 1; x <= cx + 1; x++)
                    for (long z = cz - 1; z <= cz + 1; z++)
                    {
                        if (!m_Cells.TryGetValue(Key(x, z), out var list)) continue;
                        foreach (var q in list)
                        {
                            double d = (q.X - p.X) * (q.X - p.X) + (q.Z - p.Z) * (q.Z - p.Z);
                            if (d > best) continue;
                            best = d;
                            result = q;
                            found = true;
                        }
                    }
                if (found) return result;
                long key = Key(cx, cz);
                if (!m_Cells.TryGetValue(key, out var own)) m_Cells[key] = own = new List<P>();
                own.Add(p);
                return p;
            }
        }

        private static long Key(long x, long z) => (x << 32) ^ (z & 0xffffffffL);

        // Gives points that are the same place the same coordinates, so they can be matched exactly.
        private static void Unify(List<Line> lines)
        {
            var cuts = new Snapper();
            foreach (var line in lines)
            {
                int last = line.Points.Count - 1;
                if (line.CutStart) line.Points[0] = cuts.Snap(line.Points[0], kCutSnap);
                if (line.CutEnd) line.Points[last] = cuts.Snap(line.Points[last], kCutSnap);
            }
            var all = new Snapper();
            foreach (var line in lines)
            {
                int last = line.Points.Count - 1;
                if (line.CutStart) line.Points[0] = all.Snap(line.Points[0], kSnap);
                if (line.CutEnd) line.Points[last] = all.Snap(line.Points[last], kSnap);
            }
            foreach (var line in lines)
            {
                for (int i = 0; i < line.Points.Count; i++) line.Points[i] = all.Snap(line.Points[i], kSnap);
                Dedupe(line.Points);
            }
            lines.RemoveAll(l => l.Points.Count < 2);
        }

        private static void Dedupe(List<P> points)
        {
            int write = 1;
            for (int i = 1; i < points.Count; i++)
            {
                if (Same(points[i], points[write - 1])) continue;
                points[write++] = points[i];
            }
            if (points.Count > 0) points.RemoveRange(write, points.Count - write);
        }

        private static bool Same(P a, P b) => a.X == b.X && a.Z == b.Z;

        // ---- Junctions from geometry -------------------------------------------------------

        private const double kGrid = 64.0;

        private static void Join(List<Line> lines)
        {
            var cells = new Dictionary<long, List<(int Line, int Segment)>>();
            for (int l = 0; l < lines.Count; l++)
            {
                var pts = lines[l].Points;
                for (int s = 0; s + 1 < pts.Count; s++)
                {
                    Bounds(pts[s], pts[s + 1], out double x0, out double z0, out double x1, out double z1);
                    long cx0 = Cell(x0), cx1 = Cell(x1), cz0 = Cell(z0), cz1 = Cell(z1);
                    for (long cx = cx0; cx <= cx1; cx++)
                        for (long cz = cz0; cz <= cz1; cz++)
                        {
                            long key = Key(cx, cz);
                            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<(int, int)>();
                            list.Add((l, s));
                        }
                }
            }

            foreach (var pair in cells)
            {
                var list = pair.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    var la = lines[a.Line];
                    Bounds(la.Points[a.Segment], la.Points[a.Segment + 1], out double ax0, out double az0, out double ax1, out double az1);
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        var b = list[j];
                        if (b.Line == a.Line) continue;
                        var lb = lines[b.Line];
                        Bounds(lb.Points[b.Segment], lb.Points[b.Segment + 1], out double bx0, out double bz0, out double bx1, out double bz1);
                        double ix = Math.Max(ax0, bx0), iz = Math.Max(az0, bz0);
                        if (ix > Math.Min(ax1, bx1) || iz > Math.Min(az1, bz1)) continue;
                        // Two segments share several cells: handle the pair in one of them only.
                        if (Key(Cell(ix), Cell(iz)) != pair.Key) continue;
                        Meet(la, a.Segment, lb, b.Segment);
                    }
                }
            }

            foreach (var line in lines)
            {
                if (line.Inserts == null) continue;
                line.Inserts.Sort((u, v) => u.Segment != v.Segment ? v.Segment.CompareTo(u.Segment) : v.T.CompareTo(u.T));
                foreach (var insert in line.Inserts) line.Points.Insert(insert.Segment + 1, insert.Point);
                line.Inserts = null;
                Dedupe(line.Points);
            }
        }

        private static long Cell(double v) => (long)Math.Floor(v / kGrid);

        private static void Bounds(P a, P b, out double x0, out double z0, out double x1, out double z1)
        {
            x0 = Math.Min(a.X, b.X) - kSnap;
            x1 = Math.Max(a.X, b.X) + kSnap;
            z0 = Math.Min(a.Z, b.Z) - kSnap;
            z1 = Math.Max(a.Z, b.Z) + kSnap;
        }

        private static void Meet(Line la, int sa, Line lb, int sb)
        {
            P a0 = la.Points[sa], a1 = la.Points[sa + 1], b0 = lb.Points[sb], b1 = lb.Points[sb + 1];
            if (Same(a0, b0) || Same(a0, b1) || Same(a1, b0) || Same(a1, b1)) return;
            bool level = la.Level == lb.Level;

            // A road's own end always joins what it touches, whatever the level: a bridge ends on
            // the street it leads to. A point along a road joins only roads on its level.
            bool touched = false;
            if (level || sa == 0) touched |= Touch(a0, lb, sb, b0, b1);
            if (level || sa == la.Points.Count - 2) touched |= Touch(a1, lb, sb, b0, b1);
            if (level || sb == 0) touched |= Touch(b0, la, sa, a0, a1);
            if (level || sb == lb.Points.Count - 2) touched |= Touch(b1, la, sa, a0, a1);
            if (touched) return;

            double rx = a1.X - a0.X, rz = a1.Z - a0.Z, sx = b1.X - b0.X, sz = b1.Z - b0.Z;
            double d = rx * sz - rz * sx;
            if (Math.Abs(d) < 1e-9) return;
            double qx = b0.X - a0.X, qz = b0.Z - a0.Z;
            double t = (qx * sz - qz * sx) / d, u = (qx * rz - qz * rx) / d;
            if (t <= 0 || t >= 1 || u <= 0 || u >= 1) return;
            var point = new P(a0.X + rx * t, a0.Z + rz * t);

            if (level)
            {
                Insert(la, sa, t, point);
                Insert(lb, sb, u, point);
                return;
            }
            // Different levels: no junction. Remember it so the heights can keep them apart.
            Line upper = la.Level > lb.Level ? la : lb, lower = upper == la ? lb : la;
            int id = ++s_Crossings;
            if (lower.Level < 0) Cross(lower, point, Math.Min(2, -lower.Level), id);
            else if (upper.Level > 0)
            {
                Cross(upper, point, Math.Min(3, upper.Level), id);
                if (lower.Level == 0) (lower.Under ?? (lower.Under = new List<(P, int)>())).Add((point, id));
            }
        }

        private static bool Touch(P p, Line other, int segment, P b0, P b1)
        {
            double sx = b1.X - b0.X, sz = b1.Z - b0.Z;
            double length2 = sx * sx + sz * sz;
            if (length2 < 1e-12) return false;
            double t = ((p.X - b0.X) * sx + (p.Z - b0.Z) * sz) / length2;
            if (t <= 0 || t >= 1) return false;
            double dx = b0.X + sx * t - p.X, dz = b0.Z + sz * t - p.Z;
            if (dx * dx + dz * dz > kSnap * kSnap) return false;
            Insert(other, segment, t, p);
            return true;
        }

        private static void Insert(Line line, int segment, double t, P point)
        {
            (line.Inserts ?? (line.Inserts = new List<(int, double, P)>())).Add((segment, t, point));
        }

        private static int s_Crossings; // one plan is built at a time

        private static void Cross(Line line, P point, int levels, int id)
        {
            (line.Crossings ?? (line.Crossings = new List<(P, int, int)>())).Add((point, levels, id));
        }

        // ---- Network -----------------------------------------------------------------------

        private sealed class Draft
        {
            public List<P> Points;
            public Line Line;
            public int A, B;        // vertex ids, later node ids
            public double Length;
            public bool Dropped;
        }

        private static RoadPlan Assemble(List<Line> lines)
        {
            // Every stretch between two neighbouring points becomes one link of the graph.
            var ids = new Dictionary<(double, double), int>();
            var vertices = new List<P>();
            var from = new List<int>();
            var to = new List<int>();
            var owner = new List<int>();
            var seen = new HashSet<long>();
            for (int l = 0; l < lines.Count; l++)
            {
                var pts = lines[l].Points;
                int previous = -1;
                foreach (var p in pts)
                {
                    if (!ids.TryGetValue((p.X, p.Z), out int id))
                    {
                        id = vertices.Count;
                        ids[(p.X, p.Z)] = id;
                        vertices.Add(p);
                    }
                    // The same stretch can arrive twice, from two tiles or two features.
                    if (previous >= 0 && previous != id && seen.Add(((long)Math.Min(previous, id) << 32) | (uint)Math.Max(previous, id)))
                    {
                        from.Add(previous);
                        to.Add(id);
                        owner.Add(l);
                    }
                    previous = id;
                }
            }

            int links = from.Count, count = vertices.Count;
            var start = new int[count + 1];
            for (int s = 0; s < links; s++) { start[from[s] + 1]++; start[to[s] + 1]++; }
            for (int v = 0; v < count; v++) start[v + 1] += start[v];
            var fill = new int[count];
            var around = new int[links * 2];
            for (int s = 0; s < links; s++)
            {
                around[start[from[s]] + fill[from[s]]++] = s;
                around[start[to[s]] + fill[to[s]]++] = s;
            }

            // A point is a junction unless exactly two links of the same kind of road pass through.
            var junction = new bool[count];
            for (int v = 0; v < count; v++)
            {
                int degree = start[v + 1] - start[v];
                junction[v] = degree != 2 || !Continues(lines, from, to, owner, around[start[v]], around[start[v] + 1], v);
            }

            var drafts = new List<Draft>();
            var chainOf = new int[links];
            for (int s = 0; s < links; s++) chainOf[s] = -1;
            for (int v = 0; v < count; v++)
            {
                if (!junction[v]) continue;
                for (int k = start[v]; k < start[v + 1]; k++)
                    Walk(v, around[k], lines, vertices, from, to, owner, start, around, junction, chainOf, drafts);
            }
            // Rings with no junction at all: start anywhere.
            for (int s = 0; s < links; s++)
            {
                if (chainOf[s] >= 0) continue;
                junction[from[s]] = true;
                Walk(from[s], s, lines, vertices, from, to, owner, start, around, junction, chainOf, drafts);
            }

            // Junctions a few metres apart (the two sides of a divided road meeting a street, a
            // short connector) become one: the game cannot build segments that short.
            var parent = new int[count];
            var sumX = new double[count];
            var sumZ = new double[count];
            var members = new int[count];
            for (int v = 0; v < count; v++) { parent[v] = v; sumX[v] = vertices[v].X; sumZ[v] = vertices[v].Z; members[v] = 1; }
            var order = new List<Draft>(drafts);
            order.Sort((u, v) => u.Length.CompareTo(v.Length));
            foreach (var draft in order)
            {
                if (draft.Length >= kMinChain) break;
                int a = Find(parent, draft.A), b = Find(parent, draft.B);
                if (a != b)
                {
                    double dx = sumX[a] / members[a] - sumX[b] / members[b], dz = sumZ[a] / members[a] - sumZ[b] / members[b];
                    if (dx * dx + dz * dz > kMergeReach * kMergeReach) continue;
                    parent[b] = a;
                    sumX[a] += sumX[b];
                    sumZ[a] += sumZ[b];
                    members[a] += members[b];
                }
                draft.Dropped = true;
            }

            var plan = new RoadPlan();
            var nodeOf = new Dictionary<int, int>();
            int NodeFor(int vertex)
            {
                int root = Find(parent, vertex);
                if (!nodeOf.TryGetValue(root, out int node))
                {
                    node = plan.Nodes.Count;
                    nodeOf[root] = node;
                    plan.Nodes.Add(new RoadPlan.Node { X = sumX[root] / members[root], Z = sumZ[root] / members[root] });
                }
                return node;
            }

            var pairs = new Dictionary<long, int>();
            foreach (var draft in drafts)
            {
                if (draft.Dropped) continue;
                int ra = Find(parent, draft.A), rb = Find(parent, draft.B);
                var pts = draft.Points;
                pts[0] = new P(sumX[ra] / members[ra], sumZ[ra] / members[ra]);
                pts[pts.Count - 1] = new P(sumX[rb] / members[rb], sumZ[rb] / members[rb]);
                // Points right next to a junction only put a kink in the road.
                for (int i = pts.Count - 2; i >= 1; i--)
                    if (Distance(pts[i], pts[0]) < 3.0 || Distance(pts[i], pts[pts.Count - 1]) < 3.0) pts.RemoveAt(i);
                Simplify(pts, 0.4);
                draft.Length = LengthOf(pts);
                if (draft.Length < 1.0 || (ra == rb && draft.Length < kTinyLoop))
                {
                    draft.Dropped = true;
                    continue;
                }
                draft.A = NodeFor(draft.A);
                draft.B = NodeFor(draft.B);
                long key = ((long)Math.Min(draft.A, draft.B) << 32) | (uint)Math.Max(draft.A, draft.B);
                pairs.TryGetValue(key, out int n);
                pairs[key] = n + 1;
            }

            var chains = new RoadPlan.Chain[drafts.Count];
            for (int d = 0; d < drafts.Count; d++)
            {
                var draft = drafts[d];
                if (draft.Dropped) continue;
                // Two roads between the same two junctions, or a loop back to its own junction,
                // need a point of their own in between for the game to tell them apart.
                long key = ((long)Math.Min(draft.A, draft.B) << 32) | (uint)Math.Max(draft.A, draft.B);
                int least = draft.A == draft.B ? 3 : pairs[key] > 1 ? 2 : 1;
                var stops = Cut(draft.Points, draft.Length, least);
                if (stops == null) continue;
                var chain = new RoadPlan.Chain
                {
                    A = draft.A, B = draft.B, Kind = draft.Line.Kind, Oneway = draft.Line.Oneway, Level = draft.Line.Level,
                    Length = draft.Length, Stops = stops,
                };
                chains[d] = chain;
                plan.Chains.Add(chain);
                plan.Pieces += stops.Length - 1;
            }

            // Hand each crossing to the chain that holds that stretch of its road.
            var linksOf = new Dictionary<int, List<int>>();
            for (int s = 0; s < links; s++)
            {
                if (lines[owner[s]].Crossings == null && lines[owner[s]].Under == null) continue;
                if (!linksOf.TryGetValue(owner[s], out var list)) linksOf[owner[s]] = list = new List<int>();
                list.Add(s);
            }
            RoadPlan.Chain Holder(List<int> own, P point, out double along)
            {
                int best = -1;
                double bestDistance = double.MaxValue;
                along = 0;
                foreach (int s in own)
                {
                    double distance = ToSegment(point, vertices[from[s]], vertices[to[s]], out _);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = s;
                }
                var chain = best >= 0 && chainOf[best] >= 0 ? chains[chainOf[best]] : null;
                if (chain != null) along = Along(drafts[chainOf[best]].Points, point);
                return chain;
            }
            // The roads on the ground under bridges first, so each bridge can find its own.
            var under = new Dictionary<int, (RoadPlan.Chain Chain, double S)>();
            foreach (var pair in linksOf)
            {
                if (lines[pair.Key].Under == null) continue;
                foreach (var crossing in lines[pair.Key].Under)
                {
                    var chain = Holder(pair.Value, crossing.Point, out double along);
                    if (chain != null) under[crossing.Id] = (chain, along);
                }
            }
            foreach (var pair in linksOf)
            {
                if (lines[pair.Key].Crossings == null) continue;
                foreach (var crossing in lines[pair.Key].Crossings)
                {
                    var chain = Holder(pair.Value, crossing.Point, out double along);
                    if (chain == null) continue;
                    under.TryGetValue(crossing.Id, out var below);
                    (chain.Crossings ?? (chain.Crossings = new List<RoadPlan.Crossing>())).Add(new RoadPlan.Crossing
                    {
                        X = crossing.Point.X, Z = crossing.Point.Z, S = along, Levels = crossing.Levels,
                        Under = below.Chain, UnderS = below.S,
                    });
                }
            }
            return plan;
        }

        private static int Find(int[] parent, int v)
        {
            while (parent[v] != v)
            {
                parent[v] = parent[parent[v]];
                v = parent[v];
            }
            return v;
        }

        private static bool Continues(List<Line> lines, List<int> from, List<int> to, List<int> owner, int s1, int s2, int v)
        {
            Line a = lines[owner[s1]], b = lines[owner[s2]];
            if (a.Kind != b.Kind || a.Oneway != b.Oneway || a.Level != b.Level) return false;
            // One-way traffic has to flow through: in on one link, out on the other.
            return !a.Oneway || (to[s1] == v) != (to[s2] == v);
        }

        private static void Walk(int vertex, int link, List<Line> lines, List<P> vertices, List<int> from, List<int> to, List<int> owner,
            int[] start, int[] around, bool[] junction, int[] chainOf, List<Draft> drafts)
        {
            if (chainOf[link] >= 0) return;
            var line = lines[owner[link]];
            // One-way roads are followed in their direction only, from their upstream junction.
            if (line.Oneway && from[link] != vertex) return;
            var draft = new Draft { Points = new List<P> { vertices[vertex] }, Line = line, A = vertex };
            int index = drafts.Count, current = vertex;
            while (true)
            {
                chainOf[link] = index;
                current = from[link] == current ? to[link] : from[link];
                draft.Points.Add(vertices[current]);
                if (junction[current]) break;
                int next = around[start[current]] == link ? around[start[current] + 1] : around[start[current]];
                if (chainOf[next] >= 0) break;
                link = next;
            }
            draft.B = current;
            draft.Length = LengthOf(draft.Points);
            drafts.Add(draft);
        }

        // ---- Geometry ----------------------------------------------------------------------

        private static double Distance(P a, P b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        private static double LengthOf(List<P> points)
        {
            double length = 0;
            for (int i = 0; i + 1 < points.Count; i++) length += Distance(points[i], points[i + 1]);
            return length;
        }

        private static double ToSegment(P p, P a, P b, out double t)
        {
            double sx = b.X - a.X, sz = b.Z - a.Z, length2 = sx * sx + sz * sz;
            t = length2 > 0 ? Math.Max(0, Math.Min(1, ((p.X - a.X) * sx + (p.Z - a.Z) * sz) / length2)) : 0;
            double dx = a.X + sx * t - p.X, dz = a.Z + sz * t - p.Z;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        // Distance along a line of the point on it nearest to p.
        private static double Along(List<P> points, P p)
        {
            double best = double.MaxValue, result = 0, walked = 0;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                double length = Distance(points[i], points[i + 1]);
                double distance = ToSegment(p, points[i], points[i + 1], out double t);
                if (distance < best)
                {
                    best = distance;
                    result = walked + t * length;
                }
                walked += length;
            }
            return result;
        }

        // Douglas-Peucker: drops points that move the line by less than the tolerance.
        private static void Simplify(List<P> points, double tolerance)
        {
            if (points.Count < 3) return;
            var keep = new bool[points.Count];
            keep[0] = keep[points.Count - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, points.Count - 1));
            while (stack.Count > 0)
            {
                var (first, last) = stack.Pop();
                double worst = tolerance;
                int index = -1;
                for (int i = first + 1; i < last; i++)
                {
                    double distance = ToSegment(points[i], points[first], points[last], out _);
                    if (distance <= worst) continue;
                    worst = distance;
                    index = i;
                }
                if (index < 0) continue;
                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }
            int write = 0;
            for (int i = 0; i < points.Count; i++)
                if (keep[i]) points[write++] = points[i];
            points.RemoveRange(write, points.Count - write);
        }

        // Cuts a road into pieces: at its sharper bends, and wherever a piece would grow too long.
        // Each cut gets the direction of the road there, so the pieces join as one smooth curve.
        private static RoadPlan.Stop[] Cut(List<P> points, double length, int least)
        {
            int n = points.Count;
            var at = new double[n];
            for (int i = 1; i < n; i++) at[i] = at[i - 1] + Distance(points[i - 1], points[i]);

            var cuts = new List<double> { 0 };
            double turned = 0;
            for (int i = 1; i < n - 1; i++)
            {
                double turn = Turn(points[i - 1], points[i], points[i + 1]);
                if (turned + turn > kMaxTurn && at[i] - cuts[cuts.Count - 1] >= kMinPiece && length - at[i] >= kMinPiece)
                {
                    cuts.Add(at[i]);
                    turned = 0;
                }
                else turned += turn;
            }
            cuts.Add(length);

            var result = new List<double> { 0 };
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                double span = cuts[i + 1] - cuts[i];
                int parts = Math.Max(1, (int)Math.Ceiling(span / kMaxPiece));
                for (int k = 1; k <= parts; k++) result.Add(cuts[i] + span * k / parts);
            }
            if (result.Count - 1 < least)
            {
                if (length < least * 5.0) return null;
                result.Clear();
                for (int k = 0; k <= least; k++) result.Add(length * k / least);
            }
            result[result.Count - 1] = length;

            var stops = new RoadPlan.Stop[result.Count];
            for (int k = 0; k < result.Count; k++)
            {
                double s = result[k];
                double reach = 6.0;
                if (k > 0) reach = Math.Min(reach, (s - result[k - 1]) / 2);
                if (k + 1 < result.Count) reach = Math.Min(reach, (result[k + 1] - s) / 2);
                reach = Math.Max(reach, 0.5);
                P here = At(points, at, s), back = At(points, at, Math.Max(0, s - reach)), ahead = At(points, at, Math.Min(length, s + reach));
                double tx = ahead.X - back.X, tz = ahead.Z - back.Z, size = Math.Sqrt(tx * tx + tz * tz);
                if (size < 1e-6)
                {
                    tx = points[n - 1].X - points[0].X;
                    tz = points[n - 1].Z - points[0].Z;
                    size = Math.Max(1e-6, Math.Sqrt(tx * tx + tz * tz));
                }
                stops[k] = new RoadPlan.Stop { X = here.X, Z = here.Z, S = s, TX = tx / size, TZ = tz / size };
            }
            // The ends are the junctions themselves, to the last bit.
            stops[0].X = points[0].X;
            stops[0].Z = points[0].Z;
            stops[stops.Length - 1].X = points[n - 1].X;
            stops[stops.Length - 1].Z = points[n - 1].Z;
            return stops;
        }

        private static double Turn(P a, P b, P c)
        {
            double ux = b.X - a.X, uz = b.Z - a.Z, vx = c.X - b.X, vz = c.Z - b.Z;
            return Math.Abs(Math.Atan2(ux * vz - uz * vx, ux * vx + uz * vz));
        }

        private static P At(List<P> points, double[] at, double s)
        {
            int low = 0, high = points.Count - 1;
            while (high - low > 1)
            {
                int middle = (low + high) / 2;
                if (at[middle] <= s) low = middle;
                else high = middle;
            }
            double span = at[high] - at[low];
            double t = span > 0 ? Math.Max(0, Math.Min(1, (s - at[low]) / span)) : 0;
            return new P(points[low].X + (points[high].X - points[low].X) * t, points[low].Z + (points[high].Z - points[low].Z) * t);
        }
    }
}
