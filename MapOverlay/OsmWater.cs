using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace MapOverlay
{
    // Reads the sea, lakes, rivers and canals from OpenStreetMap through the OpenFreeMap vector
    // tiles (OpenMapTiles schema): one source that tells them apart. Open sea tiles hold an ocean
    // polygon and land with nothing mapped comes back empty, so an empty tile simply means land.
    internal sealed class OsmWater
    {
        public const byte kNone = 0, kOcean = 1, kRiver = 2, kLake = 3; // lower wins where they overlap

        public sealed class Line
        {
            public readonly List<(double X, double Z)> Points = new List<(double, double)>();
            public bool Canal;
        }

        // One piece of road from the "transportation" layer, already in world metres. One-way
        // roads always run in the direction of their points.
        public sealed class Road
        {
            public readonly List<(double X, double Z)> Points = new List<(double, double)>();
            public string Class;
            public int Brunnel;     // 1 bridge, -1 tunnel, 0 on the ground
            public int Layer;       // OSM layer, 0 when not mapped
            public bool Oneway;
            public bool Ramp;
            public bool CutStart, CutEnd; // ends at a tile edge: the road goes on in the next tile
        }

        // A named stretch of road from the "transportation_name" layer, in world metres.
        public sealed class RoadName
        {
            public readonly List<(double X, double Z)> Points = new List<(double, double)>();
            public string Name;
        }

        public sealed class Data
        {
            public byte[] Mask;     // n² cells of kNone..kLake over the map, row 0 south
            public List<Line> Lines;
        }

        private const string kTileJson = "https://tiles.openfreemap.org/planet";
        private const string kCacheKey = "openfreemap";
        private const int kMaxZoom = 14;
        private const int kMinZoom = 8;
        private const int kMaxTiles = 160;
        private const int kMaxRequests = 6;

        private sealed class Job
        {
            public int X, Y;
            public UnityWebRequest Request;
            public bool Retried;
        }

        private readonly Queue<Job> m_Queue = new Queue<Job>();
        private readonly List<Job> m_Active = new List<Job>();
        private readonly List<(int X, int Y, byte[] Bytes)> m_Tiles = new List<(int, int, byte[])>();
        private UnityWebRequest m_TileJson;
        private bool m_TileJsonRetried;
        private string m_Template;
        private MapFrame m_Frame;
        private double m_Size;
        private int m_Zoom;
        private int m_Total;
        private List<Road> m_Roads; // set while decoding roads instead of water
        private List<RoadName> m_RoadNames;

        public int Zoom => m_Zoom;

        public bool Done { get; private set; }
        public bool Failed { get; private set; }
        public float Progress => m_Total > 0 ? (float)m_Tiles.Count / m_Total : 0f;

        public void Start(MapFrame frame, double size)
        {
            Cancel();
            m_Tiles.Clear();
            m_Frame = frame;
            m_Size = size;
            m_Total = 0;
            m_TileJsonRetried = false;
            Done = Failed = false;
            if (!PlanTiles()) return;
            SendTileJson();
        }

        public void Cancel()
        {
            foreach (var job in m_Active) job.Request.Dispose();
            m_Active.Clear();
            m_Queue.Clear();
            m_TileJson?.Dispose();
            m_TileJson = null;
        }

        private void SendTileJson()
        {
            m_TileJson = UnityWebRequest.Get(kTileJson);
            m_TileJson.SetRequestHeader("User-Agent", TileSources.UserAgent);
            m_TileJson.timeout = 30;
            m_TileJson.SendWebRequest();
        }

        // Highest zoom that keeps the download reasonable; small map scales cover more ground.
        private bool PlanTiles()
        {
            for (int z = kMaxZoom; z >= kMinZoom; z--)
            {
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var (cx, cz) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
                {
                    m_Frame.ToGeo(cx * m_Size / 2, cz * m_Size / 2, out double lat, out double lon);
                    double tx = GeoMath.TileX(lon, z), ty = GeoMath.TileY(lat, z);
                    minX = Math.Min(minX, tx); maxX = Math.Max(maxX, tx);
                    minY = Math.Min(minY, ty); maxY = Math.Max(maxY, ty);
                }
                int x0 = (int)Math.Floor(minX), x1 = (int)Math.Floor(maxX), y0 = (int)Math.Floor(minY), y1 = (int)Math.Floor(maxY);
                if ((x1 - x0 + 1) * (y1 - y0 + 1) > kMaxTiles) continue;
                m_Zoom = z;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        m_Queue.Enqueue(new Job { X = x, Y = y });
                m_Total = m_Queue.Count;
                return true;
            }
            Mod.log.Warn("OSM water: the area needs too many tiles (is it across the antimeridian?).");
            Failed = true;
            return false;
        }

        // Main thread, every frame.
        public void Tick()
        {
            if (Done || Failed) return;
            if (m_Template == null)
            {
                if (m_TileJson == null || !m_TileJson.isDone) return;
                bool ok = m_TileJson.result == UnityWebRequest.Result.Success;
                string text = ok ? m_TileJson.downloadHandler.text : null;
                string error = m_TileJson.error;
                m_TileJson.Dispose();
                m_TileJson = null;
                try
                {
                    if (ok) m_Template = (string)JObject.Parse(text)["tiles"]?[0];
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                if (m_Template == null)
                {
                    Mod.log.Warn($"OSM water: no tile address ({error}).");
                    if (!m_TileJsonRetried)
                    {
                        m_TileJsonRetried = true;
                        SendTileJson();
                    }
                    else Fail();
                }
                return;
            }

            int reads = 0;
            while (m_Queue.Count > 0 && m_Active.Count < kMaxRequests)
            {
                var job = m_Queue.Peek();
                string path = TileCache.PathFor(kCacheKey, m_Zoom, Wrap(job.X), job.Y);
                if (File.Exists(path))
                {
                    if (reads >= 8) break;
                    reads++;
                    m_Queue.Dequeue();
                    m_Tiles.Add((job.X, job.Y, File.ReadAllBytes(path)));
                    continue;
                }
                m_Queue.Dequeue();
                Send(job);
            }

            for (int i = m_Active.Count - 1; i >= 0; i--)
            {
                var job = m_Active[i];
                if (!job.Request.isDone) continue;
                m_Active.RemoveAt(i);
                var request = job.Request;
                bool ok = request.result == UnityWebRequest.Result.Success;
                byte[] data = ok ? request.downloadHandler.data ?? new byte[0] : null;
                long code = request.responseCode;
                string error = request.error;
                request.Dispose();
                if (data == null && code == 404) data = new byte[0];
                if (data == null)
                {
                    if (!job.Retried)
                    {
                        job.Retried = true;
                        Send(job);
                        continue;
                    }
                    Mod.log.Warn($"OSM water: tile {m_Zoom}/{job.X}/{job.Y} failed ({code} {error}).");
                    Fail();
                    return;
                }
                Store(job, data);
                m_Tiles.Add((job.X, job.Y, data));
            }

            if (m_Queue.Count == 0 && m_Active.Count == 0) Done = true;
        }

        private void Fail()
        {
            Cancel();
            Failed = true;
        }

        private int Wrap(int x)
        {
            int n = 1 << m_Zoom;
            return ((x % n) + n) % n;
        }

        private void Send(Job job)
        {
            string url = m_Template.Replace("{z}", m_Zoom.ToString()).Replace("{x}", Wrap(job.X).ToString()).Replace("{y}", job.Y.ToString());
            var request = UnityWebRequest.Get(url);
            request.SetRequestHeader("User-Agent", TileSources.UserAgent);
            request.timeout = 30;
            request.SendWebRequest();
            job.Request = request;
            m_Active.Add(job);
        }

        private void Store(Job job, byte[] data)
        {
            try
            {
                string path = TileCache.PathFor(kCacheKey, m_Zoom, Wrap(job.X), job.Y);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, data);
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"OSM water: could not cache a tile: {ex.Message}");
            }
        }

        // ---- Decoding (background thread, once Done) ---------------------------------------

        public Data Decode(int cells)
        {
            var data = new Data { Mask = new byte[cells * cells], Lines = new List<Line>() };
            var raster = new Raster(data.Mask, cells, m_Size);
            foreach (var tile in m_Tiles)
                if (tile.Bytes.Length > 0) ReadTile(Unzip(tile.Bytes), tile.X, tile.Y, data, raster);
            int polygons = 0;
            foreach (byte b in data.Mask) if (b != kNone) polygons++;
            Mod.log.Info($"OSM water: z{m_Zoom}, {m_Tiles.Count} tiles, {100.0 * polygons / data.Mask.Length:F1}% water, {data.Lines.Count} river pieces.");
            return data;
        }

        // The roads of the same tiles; a separate pass from the water.
        public List<Road> DecodeRoads(List<RoadName> names)
        {
            var roads = new List<Road>();
            m_Roads = roads;
            m_RoadNames = names;
            try
            {
                foreach (var tile in m_Tiles)
                    if (tile.Bytes.Length > 0) ReadTile(Unzip(tile.Bytes), tile.X, tile.Y, null, null);
            }
            finally
            {
                m_Roads = null;
                m_RoadNames = null;
            }
            Mod.log.Info($"OSM roads: z{m_Zoom}, {m_Tiles.Count} tiles, {roads.Count} road pieces, {names.Count} named stretches.");
            return roads;
        }

        private static byte[] Unzip(byte[] bytes)
        {
            if (bytes.Length < 2 || bytes[0] != 0x1f || bytes[1] != 0x8b) return bytes;
            using (var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                input.CopyTo(output);
                return output.ToArray();
            }
        }

        // Mapbox Vector Tile, protobuf: only what the water and waterway layers need.
        private void ReadTile(byte[] b, int tx, int ty, Data data, Raster raster)
        {
            int i = 0;
            while (i < b.Length)
            {
                ulong key = Varint(b, ref i);
                if ((key & 7) != 2) { Skip(b, ref i, (int)(key & 7)); continue; }
                int length = (int)Varint(b, ref i);
                if (key >> 3 == 3) ReadLayer(b, i, i + length, tx, ty, data, raster);
                i += length;
            }
        }

        private void ReadLayer(byte[] b, int start, int end, int tx, int ty, Data data, Raster raster)
        {
            string name = null;
            uint extent = 4096;
            var keys = new List<string>();
            var values = new List<string>();
            var features = new List<(int Start, int End)>();
            int i = start;
            while (i < end)
            {
                ulong key = Varint(b, ref i);
                int field = (int)(key >> 3);
                if (field == 5 && (key & 7) == 0) { extent = (uint)Varint(b, ref i); continue; }
                if ((key & 7) != 2) { Skip(b, ref i, (int)(key & 7)); continue; }
                int length = (int)Varint(b, ref i);
                if (field == 1) name = Encoding.UTF8.GetString(b, i, length);
                else if (field == 2) features.Add((i, i + length));
                else if (field == 3) keys.Add(Encoding.UTF8.GetString(b, i, length));
                else if (field == 4) values.Add(ReadValue(b, i, i + length));
                i += length;
            }
            if (m_Roads != null)
            {
                if (name == "transportation")
                    foreach (var feature in features)
                        ReadRoad(b, feature.Start, feature.End, keys, values, extent, tx, ty);
                else if (name == "transportation_name")
                    foreach (var feature in features)
                        ReadRoadName(b, feature.Start, feature.End, keys, values, extent, tx, ty);
                return;
            }
            bool polygons = name == "water", lines = name == "waterway";
            if (!polygons && !lines) return;
            foreach (var feature in features)
                ReadFeature(b, feature.Start, feature.End, keys, values, polygons, extent, tx, ty, data, raster);
        }

        private static string ReadValue(byte[] b, int start, int end)
        {
            int i = start;
            while (i < end)
            {
                ulong key = Varint(b, ref i);
                int wire = (int)(key & 7), field = (int)(key >> 3);
                if (wire == 2)
                {
                    int length = (int)Varint(b, ref i);
                    string text = Encoding.UTF8.GetString(b, i, length);
                    i += length;
                    return text;
                }
                if (wire == 0)
                {
                    // Signed values matter for roads: layer -1, oneway -1.
                    ulong v = Varint(b, ref i);
                    if (field == 6) return ((long)(v >> 1) ^ -(long)(v & 1)).ToString();
                    if (field == 4) return ((long)v).ToString();
                    return v.ToString();
                }
                Skip(b, ref i, wire);
            }
            return "";
        }

        private void ReadFeature(byte[] b, int start, int end, List<string> keys, List<string> values, bool polygons, uint extent, int tx, int ty, Data data, Raster raster)
        {
            string kind = null, brunnel = null, intermittent = null;
            var commands = new List<uint>();
            int i = start;
            while (i < end)
            {
                ulong key = Varint(b, ref i);
                int field = (int)(key >> 3), wire = (int)(key & 7);
                if (wire != 2) { Skip(b, ref i, wire); continue; }
                int length = (int)Varint(b, ref i), stop = i + length;
                if (field == 2)
                {
                    while (i < stop)
                    {
                        string k = keys[(int)Varint(b, ref i)];
                        string v = values[(int)Varint(b, ref i)];
                        if (k == "class") kind = v;
                        else if (k == "brunnel") brunnel = v;
                        else if (k == "intermittent") intermittent = v;
                    }
                }
                else if (field == 4)
                {
                    while (i < stop) commands.Add((uint)Varint(b, ref i));
                }
                i = stop;
            }
            if (brunnel == "tunnel" || intermittent == "1" || intermittent == "true") return;

            var rings = Paths(commands);
            if (polygons)
            {
                byte value = kind == "ocean" ? kOcean : kind == "river" || kind == "dock" ? kRiver : kind == "lake" || kind == "pond" ? kLake : kNone;
                if (value == kNone) return;
                var world = new List<List<(double X, double Z)>>(rings.Count);
                foreach (var ring in rings)
                {
                    var points = new List<(double X, double Z)>(ring.Count);
                    foreach (var p in ring) points.Add(ToWorld(tx + p.X / (double)extent, ty + p.Y / (double)extent));
                    world.Add(points);
                }
                raster.Fill(world, value);
            }
            else if (kind == "river" || kind == "canal")
            {
                foreach (var path in rings)
                    AddClipped(path, extent, tx, ty, kind == "canal", data.Lines);
            }
        }

        private void ReadRoad(byte[] b, int start, int end, List<string> keys, List<string> values, uint extent, int tx, int ty)
        {
            string kind = null, brunnel = null, oneway = null, ramp = null, layer = null, service = null;
            int type = 0;
            var commands = new List<uint>();
            int i = start;
            while (i < end)
            {
                ulong key = Varint(b, ref i);
                int field = (int)(key >> 3), wire = (int)(key & 7);
                if (field == 3 && wire == 0) { type = (int)Varint(b, ref i); continue; }
                if (wire != 2) { Skip(b, ref i, wire); continue; }
                int length = (int)Varint(b, ref i), stop = i + length;
                if (field == 2)
                {
                    while (i < stop)
                    {
                        string k = keys[(int)Varint(b, ref i)];
                        string v = values[(int)Varint(b, ref i)];
                        if (k == "class") kind = v;
                        else if (k == "brunnel") brunnel = v;
                        else if (k == "oneway") oneway = v;
                        else if (k == "ramp") ramp = v;
                        else if (k == "layer") layer = v;
                        else if (k == "service") service = v;
                    }
                }
                else if (field == 4)
                {
                    while (i < stop) commands.Add((uint)Varint(b, ref i));
                }
                i = stop;
            }
            // Lines only: the layer also holds squares and bridge decks as polygons.
            if (type != 2 || kind == null) return;
            // Car parks and private driveways would bury the streets in clutter.
            if (kind == "service" && (service == "parking_aisle" || service == "driveway")) return;

            bool reversed = oneway == "-1";
            int.TryParse(layer, out int layerValue);
            foreach (var path in Paths(commands))
            {
                int first = m_Roads.Count;
                AddClippedRoad(path, extent, tx, ty);
                for (int r = first; r < m_Roads.Count; r++)
                {
                    var road = m_Roads[r];
                    road.Class = kind;
                    road.Brunnel = brunnel == "bridge" ? 1 : brunnel == "tunnel" ? -1 : 0;
                    road.Layer = layerValue;
                    road.Oneway = oneway == "1" || reversed;
                    road.Ramp = ramp == "1";
                    if (reversed)
                    {
                        road.Points.Reverse();
                        bool cut = road.CutStart;
                        road.CutStart = road.CutEnd;
                        road.CutEnd = cut;
                    }
                }
            }
        }

        // Names come as their own lines, drawn along the roads they belong to. Left unclipped:
        // they are only matched to roads by position, so the overlap between tiles does no harm.
        private void ReadRoadName(byte[] b, int start, int end, List<string> keys, List<string> values, uint extent, int tx, int ty)
        {
            string text = null, number = null;
            int type = 0;
            var commands = new List<uint>();
            int i = start;
            while (i < end)
            {
                ulong key = Varint(b, ref i);
                int field = (int)(key >> 3), wire = (int)(key & 7);
                if (field == 3 && wire == 0) { type = (int)Varint(b, ref i); continue; }
                if (wire != 2) { Skip(b, ref i, wire); continue; }
                int length = (int)Varint(b, ref i), stop = i + length;
                if (field == 2)
                {
                    while (i < stop)
                    {
                        string k = keys[(int)Varint(b, ref i)];
                        string v = values[(int)Varint(b, ref i)];
                        if (k == "name") text = v;
                        else if (k == "ref") number = v;
                    }
                }
                else if (field == 4)
                {
                    while (i < stop) commands.Add((uint)Varint(b, ref i));
                }
                i = stop;
            }
            // Motorways often have only a number (A8, I-5).
            if (string.IsNullOrWhiteSpace(text) && number != null) text = number.Split(';')[0];
            if (type != 2 || string.IsNullOrWhiteSpace(text)) return;
            foreach (var path in Paths(commands))
            {
                if (path.Count < 2) continue;
                var line = new RoadName { Name = text.Trim() };
                foreach (var p in path) line.Points.Add(ToWorld(tx + p.X / (double)extent, ty + p.Y / (double)extent));
                m_RoadNames.Add(line);
            }
        }

        // Like AddClipped, remembering which ends were cut at the tile's edge.
        private void AddClippedRoad(List<(int X, int Y)> path, uint extent, int tx, int ty)
        {
            Road current = null;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                double x0 = path[i].X, y0 = path[i].Y, x1 = path[i + 1].X, y1 = path[i + 1].Y;
                double t0 = 0, t1 = 1, dx = x1 - x0, dy = y1 - y0;
                bool inside = Clip(-dx, x0, ref t0, ref t1) && Clip(dx, extent - x0, ref t0, ref t1)
                    && Clip(-dy, y0, ref t0, ref t1) && Clip(dy, extent - y0, ref t0, ref t1);
                if (!inside)
                {
                    current = null;
                    continue;
                }
                if (current == null)
                {
                    current = new Road { CutStart = t0 > 0 };
                    m_Roads.Add(current);
                    current.Points.Add(ToWorld(tx + (x0 + dx * t0) / extent, ty + (y0 + dy * t0) / extent));
                }
                current.Points.Add(ToWorld(tx + (x0 + dx * t1) / extent, ty + (y0 + dy * t1) / extent));
                if (t1 < 1)
                {
                    current.CutEnd = true;
                    current = null;
                }
            }
        }

        private (double X, double Z) ToWorld(double tileX, double tileY)
        {
            m_Frame.ToWorld(GeoMath.TileToLat(tileY, m_Zoom), GeoMath.TileToLon(tileX, m_Zoom), out double x, out double z);
            return (x, z);
        }

        // Tile lines carry a margin that overlaps the next tile: cut them at the tile's edge so
        // the pieces meet end to end and can be joined again.
        private void AddClipped(List<(int X, int Y)> path, uint extent, int tx, int ty, bool canal, List<Line> lines)
        {
            Line current = null;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                double x0 = path[i].X, y0 = path[i].Y, x1 = path[i + 1].X, y1 = path[i + 1].Y;
                double t0 = 0, t1 = 1, dx = x1 - x0, dy = y1 - y0;
                bool inside = Clip(-dx, x0, ref t0, ref t1) && Clip(dx, extent - x0, ref t0, ref t1)
                    && Clip(-dy, y0, ref t0, ref t1) && Clip(dy, extent - y0, ref t0, ref t1);
                if (!inside)
                {
                    current = null;
                    continue;
                }
                if (current == null)
                {
                    current = new Line { Canal = canal };
                    lines.Add(current);
                    current.Points.Add(ToWorld(tx + (x0 + dx * t0) / extent, ty + (y0 + dy * t0) / extent));
                }
                current.Points.Add(ToWorld(tx + (x0 + dx * t1) / extent, ty + (y0 + dy * t1) / extent));
                if (t1 < 1) current = null;
            }
        }

        // Liang-Barsky: narrows [t0, t1] to the part of a segment on the inner side of one edge.
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

        // Geometry commands to point lists; ClosePath just ends the ring, which is closed anyway.
        private static List<List<(int X, int Y)>> Paths(List<uint> commands)
        {
            var paths = new List<List<(int X, int Y)>>();
            List<(int X, int Y)> current = null;
            int x = 0, y = 0, i = 0;
            while (i < commands.Count)
            {
                uint command = commands[i++];
                uint op = command & 7, count = command >> 3;
                if (op == 7) continue;
                for (uint k = 0; k < count && i + 1 < commands.Count; k++)
                {
                    x += ZigZag(commands[i++]);
                    y += ZigZag(commands[i++]);
                    if (op == 1)
                    {
                        current = new List<(int X, int Y)>();
                        paths.Add(current);
                    }
                    current?.Add((x, y));
                }
            }
            return paths;
        }

        private static int ZigZag(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

        private static ulong Varint(byte[] b, ref int i)
        {
            ulong value = 0;
            int shift = 0;
            while (true)
            {
                byte c = b[i++];
                value |= (ulong)(c & 127) << shift;
                if (c < 128) return value;
                shift += 7;
            }
        }

        private static void Skip(byte[] b, ref int i, int wire)
        {
            if (wire == 0) Varint(b, ref i);
            else if (wire == 1) i += 8;
            else if (wire == 2) { int length = (int)Varint(b, ref i); i += length; }
            else if (wire == 5) i += 4;
            else throw new InvalidDataException("unknown protobuf field type");
        }

        // Scanline fill onto the map's cell grid, which may be turned against the tiles. Even-odd
        // over all of a feature's rings, so holes stay open.
        private sealed class Raster
        {
            private readonly byte[] m_Mask;
            private readonly int m_N;
            private readonly double m_Half, m_Cell;
            private readonly List<double>[] m_Rows;

            public Raster(byte[] mask, int n, double size)
            {
                m_Mask = mask;
                m_N = n;
                m_Half = size / 2;
                m_Cell = size / n;
                m_Rows = new List<double>[n];
                for (int i = 0; i < n; i++) m_Rows[i] = new List<double>();
            }

            public void Fill(List<List<(double X, double Z)>> rings, byte value)
            {
                int first = m_N, last = -1;
                foreach (var ring in rings)
                {
                    int count = ring.Count;
                    if (count < 3) continue;
                    for (int i = 0; i < count; i++)
                    {
                        var a = ring[i];
                        var b = ring[(i + 1) % count];
                        double ax = (a.X + m_Half) / m_Cell, az = (a.Z + m_Half) / m_Cell;
                        double bx = (b.X + m_Half) / m_Cell, bz = (b.Z + m_Half) / m_Cell;
                        if (az == bz) continue;
                        double low = Math.Min(az, bz), high = Math.Max(az, bz);
                        // Rows whose centre (r + 0.5) lies in [low, high).
                        int r0 = Math.Max(0, (int)Math.Ceiling(low - 0.5)), r1 = Math.Min(m_N - 1, (int)Math.Ceiling(high - 0.5) - 1);
                        for (int r = r0; r <= r1; r++)
                            m_Rows[r].Add(ax + (r + 0.5 - az) / (bz - az) * (bx - ax));
                        if (r0 <= r1)
                        {
                            first = Math.Min(first, r0);
                            last = Math.Max(last, r1);
                        }
                    }
                }
                for (int r = first; r <= last; r++)
                {
                    var row = m_Rows[r];
                    row.Sort();
                    for (int k = 0; k + 1 < row.Count; k += 2)
                    {
                        int c0 = Math.Max(0, (int)Math.Ceiling(row[k] - 0.5)), c1 = Math.Min(m_N - 1, (int)Math.Ceiling(row[k + 1] - 0.5) - 1);
                        int offset = r * m_N;
                        for (int c = c0; c <= c1; c++)
                        {
                            byte old = m_Mask[offset + c];
                            if (old == kNone || value < old) m_Mask[offset + c] = value;
                        }
                    }
                    row.Clear();
                }
            }
        }
    }
}
