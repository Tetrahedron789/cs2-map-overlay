using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine.Networking;

namespace MapOverlay
{
    // Reads ESA WorldCover 2021 (10 m land cover, CC-BY 4.0) for an area from the open
    // Cloud-Optimized GeoTIFFs on AWS. Only the TIFF header and the internal tiles that cover
    // the area are downloaded, with HTTP range requests: well under a megabyte for a city.
    internal sealed class CoverReader
    {
        public const byte kTrees = 10;
        public const byte kWater = 80;

        private const string kUrl = "https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/ESA_WorldCover_10m_2021_v200_{0}_Map.tif";
        private const int kHeaderBytes = 262144;
        private const int kMaxRequests = 4;

        private sealed class Cog
        {
            public string Url;
            public int Width, Height, TileW, TileH, Across;
            public long[] Offsets, Counts;
            public double Lon0, Lat0, Sx, Sy;
            public readonly Dictionary<int, byte[]> Tiles = new Dictionary<int, byte[]>();
            public bool Ready;
        }

        private sealed class Job
        {
            public Cog Cog;
            public int Tile = -1; // -1: header
            public UnityWebRequest Request;
            public bool Retried;
        }

        private readonly Dictionary<string, Cog> m_Cogs = new Dictionary<string, Cog>();
        private readonly Queue<Job> m_Queue = new Queue<Job>();
        private readonly List<Job> m_Active = new List<Job>();
        private MapFrame m_Frame;
        private double m_Size;
        private bool m_TilesQueued;
        private int m_TilesTotal;
        private int m_TilesDone;

        public bool Done { get; private set; }
        public bool Failed { get; private set; }
        public float Progress => m_TilesQueued ? (m_TilesTotal > 0 ? (float)m_TilesDone / m_TilesTotal : 1f) : 0f;

        // A square of `size` game metres centred on the world origin, placed by `frame`.
        public void Start(MapFrame frame, double size)
        {
            Cancel();
            m_Cogs.Clear();
            m_Frame = frame;
            m_Size = size;
            m_TilesQueued = false;
            m_TilesTotal = m_TilesDone = 0;
            Done = Failed = false;
            foreach (var (lat, lon) in SamplePoints())
            {
                string name = FileName(lat, lon);
                if (m_Cogs.ContainsKey(name)) continue;
                var cog = new Cog { Url = string.Format(kUrl, name) };
                m_Cogs[name] = cog;
                m_Queue.Enqueue(new Job { Cog = cog });
            }
        }

        public void Cancel()
        {
            foreach (var job in m_Active) job.Request.Dispose();
            m_Active.Clear();
            m_Queue.Clear();
        }

        private IEnumerable<(double, double)> SamplePoints()
        {
            const int n = 8;
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n; j++)
                {
                    m_Frame.ToGeo((i / (double)n - 0.5) * m_Size, (j / (double)n - 0.5) * m_Size, out double lat, out double lon);
                    yield return (lat, lon);
                }
        }

        // Files are 3°×3° and named after their south-west corner, e.g. N18W102.
        private static string FileName(double lat, double lon)
        {
            int la = (int)Math.Floor(lat / 3) * 3, lo = (int)Math.Floor(lon / 3) * 3;
            return $"{(la >= 0 ? "N" : "S")}{Math.Abs(la):00}{(lo >= 0 ? "E" : "W")}{Math.Abs(lo):000}";
        }

        // Main thread, every frame.
        public void Tick()
        {
            if (Done || Failed) return;
            while (m_Queue.Count > 0 && m_Active.Count < kMaxRequests) Send(m_Queue.Dequeue());

            for (int i = m_Active.Count - 1; i >= 0; i--)
            {
                var job = m_Active[i];
                if (!job.Request.isDone) continue;
                m_Active.RemoveAt(i);
                var request = job.Request;
                long code = request.responseCode;
                byte[] data = request.result == UnityWebRequest.Result.Success ? request.downloadHandler.data : null;
                string error = request.error;
                request.Dispose();
                bool ok = false;
                try
                {
                    // 404: no file for this square (open ocean). Treat it as "no data".
                    if (code == 404 && job.Tile < 0) { job.Cog.Ready = true; job.Cog.Width = 0; ok = true; }
                    else if (data != null && data.Length > 0)
                    {
                        if (job.Tile < 0) ParseHeader(job.Cog, data);
                        else { job.Cog.Tiles[job.Tile] = Inflate(data, job.Cog.TileW * job.Cog.TileH); m_TilesDone++; }
                        ok = true;
                    }
                }
                catch (Exception ex)
                {
                    Mod.log.Warn($"Land cover: could not read {job.Cog.Url} ({ex.Message})");
                }
                if (!ok)
                {
                    if (!job.Retried && code != 404)
                    {
                        job.Retried = true;
                        Send(job);
                        continue;
                    }
                    Mod.log.Warn($"Land cover: request failed ({code} {error}) for {job.Cog.Url}");
                    Failed = true;
                    Cancel();
                    return;
                }
            }

            if (!m_TilesQueued && AllHeadersReady()) QueueTiles();
            if (m_TilesQueued && m_Queue.Count == 0 && m_Active.Count == 0) Done = true;
        }

        private void Send(Job job)
        {
            var request = UnityWebRequest.Get(job.Cog.Url);
            request.SetRequestHeader("User-Agent", TileSources.UserAgent);
            if (job.Tile < 0) request.SetRequestHeader("Range", $"bytes=0-{kHeaderBytes - 1}");
            else
            {
                long start = job.Cog.Offsets[job.Tile];
                request.SetRequestHeader("Range", $"bytes={start}-{start + job.Cog.Counts[job.Tile] - 1}");
            }
            request.timeout = 60;
            request.SendWebRequest();
            job.Request = request;
            m_Active.Add(job);
        }

        private bool AllHeadersReady()
        {
            foreach (var cog in m_Cogs.Values)
                if (!cog.Ready) return false;
            return true;
        }

        // Internal tiles inside the area's bounding box in each file, with a one-pixel margin.
        private void QueueTiles()
        {
            m_TilesQueued = true;
            foreach (var cog in m_Cogs.Values)
            {
                if (cog.Width == 0) continue;
                double minC = double.MaxValue, minR = double.MaxValue, maxC = double.MinValue, maxR = double.MinValue;
                foreach (var (lat, lon) in SamplePoints())
                {
                    double c = (lon - cog.Lon0) / cog.Sx, r = (cog.Lat0 - lat) / cog.Sy;
                    minC = Math.Min(minC, c); maxC = Math.Max(maxC, c);
                    minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
                }
                int c0 = Math.Max(0, (int)Math.Floor(minC - 1) / cog.TileW), c1 = Math.Min(cog.Across - 1, (int)Math.Floor(maxC + 1) / cog.TileW);
                int down = (cog.Height + cog.TileH - 1) / cog.TileH;
                int r0 = Math.Max(0, (int)Math.Floor(minR - 1) / cog.TileH), r1 = Math.Min(down - 1, (int)Math.Floor(maxR + 1) / cog.TileH);
                if (maxC < 0 || minC >= cog.Width || maxR < 0 || minR >= cog.Height) continue;
                for (int ty = r0; ty <= r1; ty++)
                    for (int tx = c0; tx <= c1; tx++)
                    {
                        m_Queue.Enqueue(new Job { Cog = cog, Tile = ty * cog.Across + tx });
                        m_TilesTotal++;
                    }
            }
            Mod.log.Info($"Land cover: {m_Cogs.Count} file(s), {m_TilesTotal} tile(s)");
        }

        // Land cover class at a point, or 0 where there is no data. Safe from any thread once Done.
        public byte Sample(double lat, double lon)
        {
            if (!m_Cogs.TryGetValue(FileName(lat, lon), out var cog) || cog.Width == 0) return 0;
            int col = (int)Math.Floor((lon - cog.Lon0) / cog.Sx), row = (int)Math.Floor((cog.Lat0 - lat) / cog.Sy);
            if (col < 0 || row < 0 || col >= cog.Width || row >= cog.Height) return 0;
            int tile = row / cog.TileH * cog.Across + col / cog.TileW;
            if (!cog.Tiles.TryGetValue(tile, out var pixels)) return 0;
            return pixels[(row % cog.TileH) * cog.TileW + col % cog.TileW];
        }

        // Just enough TIFF for these files: little-endian classic TIFF, tiled, deflate, one band.
        private static void ParseHeader(Cog cog, byte[] h)
        {
            if (h.Length < 8 || h[0] != 'I' || h[1] != 'I' || BitConverter.ToUInt16(h, 2) != 42)
                throw new InvalidDataException("not a little-endian TIFF");
            int ifd = (int)BitConverter.ToUInt32(h, 4);
            int count = BitConverter.ToUInt16(h, ifd);
            var tags = new Dictionary<int, double[]>();
            for (int i = 0; i < count; i++)
            {
                int e = ifd + 2 + i * 12;
                int tag = BitConverter.ToUInt16(h, e);
                int type = BitConverter.ToUInt16(h, e + 2);
                int n = (int)BitConverter.ToUInt32(h, e + 4);
                int size = type == 3 ? 2 : type == 12 || type == 16 ? 8 : type == 1 || type == 2 ? 1 : 4;
                int at = size * n <= 4 ? e + 8 : (int)BitConverter.ToUInt32(h, e + 8);
                if (tag != 256 && tag != 257 && tag != 259 && tag != 322 && tag != 323 && tag != 324 && tag != 325
                    && tag != 33550 && tag != 33922) continue;
                if (at + size * n > h.Length) throw new InvalidDataException("header larger than expected");
                var values = new double[n];
                for (int k = 0; k < n; k++)
                {
                    int p = at + k * size;
                    values[k] = type == 3 ? BitConverter.ToUInt16(h, p) : type == 12 ? BitConverter.ToDouble(h, p)
                        : type == 16 ? BitConverter.ToUInt64(h, p) : type == 1 ? h[p] : BitConverter.ToUInt32(h, p);
                }
                tags[tag] = values;
            }
            if (tags[259][0] != 8 && tags[259][0] != 32946) throw new InvalidDataException("not deflate");
            cog.Width = (int)tags[256][0];
            cog.Height = (int)tags[257][0];
            cog.TileW = (int)tags[322][0];
            cog.TileH = (int)tags[323][0];
            cog.Across = (cog.Width + cog.TileW - 1) / cog.TileW;
            cog.Offsets = Array.ConvertAll(tags[324], v => (long)v);
            cog.Counts = Array.ConvertAll(tags[325], v => (long)v);
            cog.Sx = tags[33550][0];
            cog.Sy = tags[33550][1];
            cog.Lon0 = tags[33922][3];
            cog.Lat0 = tags[33922][4];
            cog.Ready = true;
        }

        // zlib stream: skip the 2-byte header, ignore the checksum.
        private static byte[] Inflate(byte[] data, int length)
        {
            var output = new byte[length];
            using (var stream = new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress))
            {
                int read = 0;
                while (read < length)
                {
                    int n = stream.Read(output, read, length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }
            return output;
        }
    }
}
