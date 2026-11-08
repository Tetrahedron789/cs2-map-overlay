using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Game;
using Game.Common;
using Game.SceneFlow;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Networking;

namespace MapOverlay
{
    // Builds the editor's heightmap (playable area) and worldmap from real elevation data, framed
    // exactly like the overlay, and loads them into the terrain. Elevation comes from the open
    // AWS Terrain Tiles (Terrarium encoding), which allow bulk downloads.
    internal sealed class TerrainBuilder
    {
        public enum State { Idle, Downloading, Building, Done, Failed, Planting }

        private const string kUrl = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{0}/{1}/{2}.png";
        private const string kCacheKey = "elevation";
        private const int kRes = 4096;
        private const int kTile = 256;
        private const int kMaxDownloads = 6;
        private const int kMaxTilesPerLayer = 160;
        private const float kDefaultHeightScale = 4096f;
        private const float kMaxHeightScale = 10000f; // the editor's own limit
        private const int kTreesPerFrame = 4000;
        private const float kForestPatch = 300f;       // game metres: one main species per patch
        private static readonly float[] kTreeSpacing = { 24f, 16f, 11f }; // low, medium, high density
        private const float kLowMargin = 20f;          // lowest land this far above the water
        private const float kWorldSeaBelow = 0.5f;     // real metres: the world map's sea
        private const float kWorldSeaFloor = -4f;
        private const int kShoreBlur = 3;              // pixels
        private const int kSeedDelayFrames = 3;

        private sealed class Layer
        {
            public double Size;    // game metres per side
            public int Zoom;
            public int X0, Y0, W, H;
            public float[] Mosaic; // W*256 × H*256 metres, row 0 north
            public float[] Heights; // kRes² metres, row 0 south (texture order)
        }

        private sealed class Job
        {
            public Layer Layer;
            public int X, Y;
            public UnityWebRequest Request;
            public bool Retried;
        }

        private readonly List<Layer> m_Layers = new List<Layer>();
        private readonly Queue<Job> m_Queue = new Queue<Job>();
        private readonly List<Job> m_Active = new List<Job>();
        private MapFrame m_Frame;
        private Task m_Task;
        private int m_TilesTotal;
        private int m_TilesDone;
        private Texture2D m_Decoder;
        private readonly CoverReader m_Cover = new CoverReader();
        private readonly OsmWater m_Osm = new OsmWater();
        private bool m_WantLakes, m_WantRivers, m_WantTrees;
        private float m_TreeSpacing;
        private int m_Smoothing;
        private bool m_WaterFailed;
        private bool m_Finished;
        private Lakes.Result m_Lakes;
        private float[] m_SeedSurface, m_SeedGround;
        private int m_SeedFrame;
        private List<Trees.Item> m_TreePlan;
        private int m_TreesPlaced;
        private readonly List<TreePrefab> m_TreePrefabs = new List<TreePrefab>();
        private float m_Baseline, m_OffsetY;

        private struct TreePrefab
        {
            public Entity Entity;
            public EntityArchetype Archetype;
            public bool Physical;
        }

        public State Status { get; private set; } = State.Idle;
        public string Problem { get; private set; } = "";
        public float Progress { get; private set; }
        public float LowestMetres { get; private set; }
        public float HighestMetres { get; private set; }
        public float WaterLevel { get; private set; }   // editor sea level the land was fitted to
        public float LowestInGame { get; private set; } // game height of the lowest land
        public float HeightScale { get; private set; }
        public int LakeCount { get; private set; }
        public int SourceCount { get; private set; }
        public int RiverCount { get; private set; }
        public int TreeCount { get; private set; }

        public bool Busy => Status == State.Downloading || Status == State.Building || Status == State.Planting;

        public static bool InEditor => GameManager.instance != null && GameManager.instance.gameMode.IsEditor();

        // options: bit 0 lakes, bit 1 rivers, bit 2 trees, bits 4-5 tree density and bits 6-7
        // terrain smoothing (0 low, 1 medium, 2 high).
        public void Start(MapFrame frame, int options)
        {
            if (Busy) return;
            if (!InEditor)
            {
                Fail("NotEditor");
                return;
            }
            Reset();
            m_Frame = frame;
            m_WantLakes = (options & 1) != 0;
            m_WantRivers = (options & 2) != 0;
            m_WantTrees = (options & 4) != 0;
            m_TreeSpacing = kTreeSpacing[Math.Min(2, (options >> 4) & 3)];
            m_Smoothing = Math.Min(2, (options >> 6) & 3);
            // Water comes from OSM; trees from land cover, kept off the water OSM maps.
            if (m_WantTrees) m_Cover.Start(frame, Setting.kWholeMapMetres);
            if (m_WantLakes || m_WantRivers || m_WantTrees) m_Osm.Start(frame, Setting.kWholeMapMetres);
            m_Layers.Add(Plan(Setting.kWholeMapMetres, 13));
            m_Layers.Add(Plan(Setting.kWorldMapMetres, 12));
            foreach (var layer in m_Layers)
                for (int y = 0; y < layer.H; y++)
                    for (int x = 0; x < layer.W; x++)
                        m_Queue.Enqueue(new Job { Layer = layer, X = layer.X0 + x, Y = layer.Y0 + y });
            m_TilesTotal = m_Queue.Count;
            Status = State.Downloading;
            Mod.log.Info($"Terrain: {m_TilesTotal} elevation tiles (z{m_Layers[0].Zoom} {m_Layers[0].W}×{m_Layers[0].H}, " +
                $"z{m_Layers[1].Zoom} {m_Layers[1].W}×{m_Layers[1].H}) at {frame.CenterLat:F5},{frame.CenterLon:F5}");
        }

        public void Cancel()
        {
            foreach (var job in m_Active) job.Request.Dispose();
            m_Active.Clear();
            m_Queue.Clear();
            m_Cover.Cancel();
            m_Osm.Cancel();
            if (Status == State.Downloading) Status = State.Idle;
        }

        // What it reports belongs to the map it was built on: a new map or project starts clean.
        public void Forget()
        {
            Reset();
            m_Finished = false;
            Status = State.Idle;
        }

        // True once after each finished generation.
        public bool ConsumeFinished()
        {
            bool finished = m_Finished;
            m_Finished = false;
            return finished;
        }

        public void Dispose()
        {
            Cancel();
            if (m_Decoder != null) UnityEngine.Object.Destroy(m_Decoder);
            m_Decoder = null;
        }

        private void Reset()
        {
            Cancel();
            m_Layers.Clear();
            m_Task = null;
            m_Lakes = null;
            m_SeedSurface = m_SeedGround = null;
            m_TreePlan = null;
            m_TreesPlaced = 0;
            m_WaterFailed = false;
            LakeCount = SourceCount = RiverCount = TreeCount = 0;
            m_TilesDone = 0;
            Progress = 0;
            Problem = "";
        }

        private void Fail(string problem)
        {
            Cancel();
            Problem = problem;
            Status = State.Failed;
        }

        // Tile range for a square of `size` game metres centred on the world origin.
        private Layer Plan(double size, int maxZoom)
        {
            var layer = new Layer { Size = size };
            for (int z = maxZoom; z >= 8; z--)
            {
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var (cx, cz) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
                {
                    m_Frame.ToGeo(cx * size / 2, cz * size / 2, out double lat, out double lon);
                    double tx = GeoMath.TileX(lon, z), ty = GeoMath.TileY(lat, z);
                    minX = Math.Min(minX, tx); maxX = Math.Max(maxX, tx);
                    minY = Math.Min(minY, ty); maxY = Math.Max(maxY, ty);
                }
                layer.Zoom = z;
                layer.X0 = (int)Math.Floor(minX);
                layer.Y0 = (int)Math.Floor(minY);
                layer.W = (int)Math.Floor(maxX) - layer.X0 + 1;
                layer.H = (int)Math.Floor(maxY) - layer.Y0 + 1;
                if (layer.W * layer.H <= kMaxTilesPerLayer) break;
            }
            layer.Mosaic = new float[layer.W * kTile * layer.H * kTile];
            return layer;
        }

        // Main thread, every frame.
        public void Tick()
        {
            try
            {
                if (m_SeedSurface != null && UnityEngine.Time.frameCount >= m_SeedFrame) SeedWater();
                if (Status == State.Downloading) TickDownloads();
                else if (Status == State.Planting) PlantBatch();
                else if (Status == State.Building && m_Task != null && m_Task.IsCompleted)
                {
                    if (m_Task.IsFaulted)
                    {
                        Mod.log.Error(m_Task.Exception, "Terrain: building the heightmaps failed.");
                        Fail("Build");
                    }
                    else Apply();
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, "Terrain: generation failed.");
                Fail("Build");
            }
        }

        private void TickDownloads()
        {
            // Cached tiles never change: elevation data is static.
            int fromCache = 0;
            while (m_Queue.Count > 0 && m_Active.Count < kMaxDownloads)
            {
                var job = m_Queue.Peek();
                string path = TileCache.PathFor(kCacheKey, job.Layer.Zoom, job.X, job.Y);
                if (File.Exists(path) && fromCache < 8)
                {
                    m_Queue.Dequeue();
                    fromCache++;
                    if (!Decode(job, File.ReadAllBytes(path))) { Fail("Network"); return; }
                    m_TilesDone++;
                    continue;
                }
                if (File.Exists(path)) break; // more cached tiles next frame
                m_Queue.Dequeue();
                StartRequest(job);
            }

            for (int i = m_Active.Count - 1; i >= 0; i--)
            {
                var job = m_Active[i];
                if (!job.Request.isDone) continue;
                m_Active.RemoveAt(i);
                var request = job.Request;
                byte[] data = request.result == UnityWebRequest.Result.Success ? request.downloadHandler.data : null;
                request.Dispose();
                if (data == null || data.Length == 0 || !Decode(job, data))
                {
                    if (!job.Retried)
                    {
                        job.Retried = true;
                        StartRequest(job);
                        continue;
                    }
                    Mod.log.Warn($"Terrain: tile {job.Layer.Zoom}/{job.X}/{job.Y} failed: {request.error}");
                    Fail("Network");
                    return;
                }
                Save(job, data);
                m_TilesDone++;
            }

            if (m_WantTrees)
            {
                m_Cover.Tick();
                if (m_Cover.Failed)
                {
                    Fail("Cover");
                    return;
                }
            }
            // Water is optional: without it the rest of the terrain is still good.
            bool wantOsm = m_WantLakes || m_WantRivers || m_WantTrees;
            if (wantOsm) m_Osm.Tick();
            float elevation = m_TilesTotal > 0 ? (float)m_TilesDone / m_TilesTotal : 1f;
            float others = (wantOsm ? m_Osm.Progress : 1f) * 0.5f + (m_WantTrees ? m_Cover.Progress : 1f) * 0.5f;
            Progress = 0.7f * (0.6f * elevation + 0.4f * others);
            bool osmReady = !wantOsm || m_Osm.Done || m_Osm.Failed;
            if (m_TilesDone == m_TilesTotal && (!m_WantTrees || m_Cover.Done) && osmReady)
            {
                Status = State.Building;
                Progress = 0.75f;
                var frame = m_Frame;
                var layers = m_Layers.ToArray();
                var cover = m_Cover;
                var osm = wantOsm && m_Osm.Done ? m_Osm : null;
                bool lakes = m_WantLakes, rivers = m_WantRivers, trees = m_WantTrees;
                float spacing = m_TreeSpacing;
                int smoothing = m_Smoothing;
                m_WaterFailed = wantOsm && m_Osm.Failed;
                m_Task = Task.Run(() =>
                {
                    Build(frame, layers, smoothing);
                    var top = layers[0];
                    var result = new Lakes.Result { Cells = Lakes.kCells };
                    var blocked = new bool[Lakes.kCells * Lakes.kCells];
                    if (osm != null)
                    {
                        try
                        {
                            var water = osm.Decode(Lakes.kCells);
                            result.Mask = water.Mask;
                            if (lakes || rivers)
                            {
                                result = Lakes.Process(top.Heights, kRes, top.Size, water.Mask, lakes);
                                // Shores come from a 10 m grid: round off its steps. Rivers are
                                // shaped smooth and come after.
                                TerrainSmoothing.BoxBlur(top.Heights, kRes, kShoreBlur, 2);
                                if (rivers) Rivers.Process(water.Lines, frame.Scale, top.Heights, kRes, top.Size, result, blocked);
                                if (result.HasSea) DeepenWorldSea(layers[1].Heights);
                                TerrainSmoothing.BoxBlur(top.Heights, kRes, 1, 1);
                            }
                        }
                        catch (Exception ex)
                        {
                            Mod.log.Error(ex, "Terrain: could not process the water data.");
                            m_WaterFailed = true;
                        }
                    }
                    if (trees) m_TreePlan = Trees.Plan(frame, top.Heights, kRes, top.Size, cover, result, blocked, spacing);
                    m_Lakes = result;
                });
            }
        }

        // Beyond the playable area there is no water data, only heights flat at 0 m over the sea.
        private static void DeepenWorldSea(float[] heights)
        {
            Parallel.For(0, heights.Length, i =>
            {
                if (heights[i] < kWorldSeaBelow) heights[i] = Math.Min(heights[i], kWorldSeaFloor);
            });
        }

        private void StartRequest(Job job)
        {
            int n = 1 << job.Layer.Zoom;
            int x = ((job.X % n) + n) % n;
            var request = UnityWebRequest.Get(string.Format(kUrl, job.Layer.Zoom, x, job.Y));
            request.SetRequestHeader("User-Agent", TileSources.UserAgent);
            request.timeout = 30;
            request.SendWebRequest();
            job.Request = request;
            m_Active.Add(job);
        }

        private static void Save(Job job, byte[] data)
        {
            try
            {
                string path = TileCache.PathFor(kCacheKey, job.Layer.Zoom, job.X, job.Y);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, data);
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Terrain: could not cache an elevation tile: {ex.Message}");
            }
        }

        // Terrarium: metres = R * 256 + G + B / 256 - 32768.
        private bool Decode(Job job, byte[] png)
        {
            if (m_Decoder == null) m_Decoder = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!m_Decoder.LoadImage(png, false) || m_Decoder.width != kTile || m_Decoder.height != kTile) return false;
            var pixels = m_Decoder.GetPixels32();
            var layer = job.Layer;
            int stride = layer.W * kTile;
            int ox = (job.X - layer.X0) * kTile;
            int oy = (job.Y - layer.Y0) * kTile;
            for (int row = 0; row < kTile; row++)
            {
                int src = (kTile - 1 - row) * kTile; // textures are stored bottom row first
                int dst = (oy + row) * stride + ox;
                for (int col = 0; col < kTile; col++)
                {
                    var p = pixels[src + col];
                    layer.Mosaic[dst + col] = p.r * 256f + p.g + p.b / 256f - 32768f;
                }
            }
            return true;
        }

        // Background thread: sample both layers in the overlay's frame and smooth them.
        private static void Build(MapFrame frame, Layer[] layers, int smoothing)
        {
            Parallel.ForEach(layers, layer =>
            {
                var heights = new float[kRes * kRes];
                int mw = layer.W * kTile, mh = layer.H * kTile;
                double px = layer.Size / kRes;
                double n = 1 << layer.Zoom;
                Parallel.For(0, kRes, row =>
                {
                    double z = -layer.Size / 2 + (row + 0.5) * px; // row 0 is the south edge
                    for (int col = 0; col < kRes; col++)
                    {
                        double x = -layer.Size / 2 + (col + 0.5) * px;
                        frame.ToGeo(x, z, out double lat, out double lon);
                        double fx = (GeoMath.TileX(lon, layer.Zoom) - layer.X0) * kTile - 0.5;
                        double fy = (GeoMath.TileY(lat, layer.Zoom) - layer.Y0) * kTile - 0.5;
                        // Longitude may wrap across the antimeridian relative to the planned range.
                        if (fx < -kTile) fx += n * kTile;
                        else if (fx > mw + kTile) fx -= n * kTile;
                        heights[row * kRes + col] = Bilinear(layer.Mosaic, mw, mh, fx, fy);
                    }
                });
                double sourcePx = GeoMath.TileMetres(frame.CenterLat, layer.Zoom) / kTile * frame.Scale;
                TerrainSmoothing.Apply(heights, kRes, px, sourcePx, smoothing);
                layer.Heights = heights;
                layer.Mosaic = null;
            });
        }

        private static float Bilinear(float[] m, int w, int h, double fx, double fy)
        {
            if (fx < 0) fx = 0; else if (fx > w - 1) fx = w - 1;
            if (fy < 0) fy = 0; else if (fy > h - 1) fy = h - 1;
            int ix = Math.Min((int)fx, w - 2), iy = Math.Min((int)fy, h - 2);
            float ax = (float)(fx - ix), ay = (float)(fy - iy);
            int i = iy * w + ix;
            float top = m[i] + (m[i + 1] - m[i]) * ax;
            float bottom = m[i + w] + (m[i + w + 1] - m[i + w]) * ax;
            return top + (bottom - top) * ay;
        }

        // Main thread: convert to 16-bit textures and hand them to the terrain like the editor's
        // own "Import heightmap" / "Import worldmap" buttons do.
        private void Apply()
        {
            if (!InEditor)
            {
                Fail("NotEditor");
                return;
            }
            var terrain = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<TerrainSystem>();
            if (terrain == null)
            {
                Fail("Build");
                return;
            }

            float min = float.MaxValue, max = float.MinValue;
            foreach (var layer in m_Layers)
                foreach (float h in layer.Heights)
                {
                    if (h < min) min = h;
                    if (h > max) max = h;
                }
            // Fit the land to the editor's water level (511.7 m by default) instead of moving the water:
            // inland, the lowest land sits just above it; on the coast, real sea level matches it
            // and the sea floor is cut off at game height 0.
            var water = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<WaterSystem>();
            float waterLevel = water != null ? water.SeaLevel : 511.7f;
            // With water data the sea is known; low land without a sea (Dead Sea, Death Valley) is inland.
            bool coast = m_Lakes != null && m_Lakes.Processed ? m_Lakes.HasSea : min < 1f;
            float baseline = coast ? -waterLevel : (float)Math.Floor(min) - waterLevel - kLowMargin;
            float range = max - baseline;
            float scale = Math.Max(kDefaultHeightScale, Math.Min(kMaxHeightScale, (float)Math.Ceiling(range * 1.02f / 256f) * 256f));
            LowestMetres = min;
            HighestMetres = max;
            WaterLevel = waterLevel;
            LowestInGame = min - baseline;
            HeightScale = scale;

            // Water from whatever map was here before would run over the new ground.
            water?.Reset();
            var playable = ToTexture(m_Layers[0].Heights, baseline, scale);
            var world = ToTexture(m_Layers[1].Heights, baseline, scale);
            try
            {
                terrain.SetTerrainProperties(new float2(scale, terrain.heightScaleOffset.y));
                terrain.ReplaceHeightmap(playable);
                terrain.ReplaceWorldHeightmap(world);
            }
            finally
            {
                // The editor copies both maps into its own render textures.
                UnityEngine.Object.Destroy(playable);
                UnityEngine.Object.Destroy(world);
            }
            // The water goes in a few frames later, once the terrain has taken the new heights.
            m_SeedSurface = m_Lakes != null ? m_Lakes.Surface : null;
            m_SeedGround = m_Layers[0].Heights;
            m_SeedFrame = UnityEngine.Time.frameCount + kSeedDelayFrames;
            if (m_SeedSurface == null) water?.UpdateSeaLevel();
            m_Layers.Clear();
            m_Baseline = baseline;
            m_OffsetY = terrain.positionOffset.y;
            if ((m_WantLakes || m_WantRivers) && m_Lakes != null && m_Lakes.Processed) PlaceWaterSources(baseline, m_OffsetY);
            m_Lakes = null;
            if (m_WaterFailed && Problem.Length == 0) Problem = "Water";
            Mod.log.Info($"Terrain: applied. Real heights {min:F0}..{max:F0} m, base {baseline:F0} m, water {waterLevel:F1}, {(coast ? "coast" : "inland")}, height scale {scale:F0}" +
                (range > scale ? " (top clipped)" : ""));
            if (m_WantTrees && StartPlanting()) return;
            Finish();
        }

        private void Finish()
        {
            m_TreePlan = null;
            m_TreePrefabs.Clear();
            Progress = 1;
            Status = State.Done;
            m_Finished = true;
        }

        // Clears the old trees and lists the tree species this game has.
        private bool StartPlanting()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var oldTrees = new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Tree>() },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            };
            using (var old = em.CreateEntityQuery(oldTrees))
                em.AddComponent<Deleted>(old);

            var prefabSystem = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<Game.Prefabs.PrefabSystem>();
            var species = new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Prefabs.TreeData>(), ComponentType.ReadOnly<Game.Prefabs.ObjectData>(), ComponentType.ReadOnly<Game.Prefabs.PrefabData>() },
                None = new[] { ComponentType.ReadOnly<Game.Prefabs.PlaceholderObjectData>() },
            };
            m_TreePrefabs.Clear();
            var names = new List<string>();
            using (var query = em.CreateEntityQuery(species))
            using (var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp))
                foreach (var entity in entities)
                {
                    if (!em.IsComponentEnabled<Game.Prefabs.PrefabData>(entity)) continue;
                    string name = prefabSystem.GetPrefab<Game.Prefabs.PrefabBase>(entity).name;
                    // Palms and dead trees do not belong in a generic forest.
                    if (name.IndexOf("Palm", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Dead", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Stump", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var archetype = em.GetComponentData<Game.Prefabs.ObjectData>(entity).m_Archetype;
                    if (!archetype.Valid) continue;
                    bool physical = em.HasComponent<Game.Prefabs.ObjectGeometryData>(entity)
                        && (em.GetComponentData<Game.Prefabs.ObjectGeometryData>(entity).m_Flags & Game.Objects.GeometryFlags.Physical) != 0;
                    m_TreePrefabs.Add(new TreePrefab { Entity = entity, Archetype = archetype, Physical = physical });
                    names.Add(name);
                }
            Mod.log.Info($"Trees: {m_TreePrefabs.Count} species: {string.Join(", ", names)}");
            if (m_TreePrefabs.Count == 0 || m_TreePlan == null || m_TreePlan.Count == 0)
            {
                if (m_TreePrefabs.Count == 0 && Problem.Length == 0) Problem = "NoTrees";
                return false;
            }
            m_TreesPlaced = 0;
            Status = State.Planting;
            Progress = 0.9f;
            return true;
        }

        private void PlantBatch()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            int end = Math.Min(m_TreePlan.Count, m_TreesPlaced + kTreesPerFrame);
            int species = m_TreePrefabs.Count;
            var one = new Unity.Collections.NativeArray<Entity>(1, Unity.Collections.Allocator.Temp);
            for (int i = m_TreesPlaced; i < end; i++)
            {
                var item = m_TreePlan[i];
                // Each patch of forest has one main species, with others mixed in.
                int patch = (int)Math.Floor(item.X / kForestPatch) * 73856093 ^ (int)Math.Floor(item.Z / kForestPatch) * 19349663;
                int seed = item.Seed;
                int choice = (seed & 7) < 5 ? patch : seed >> 8;
                var prefab = m_TreePrefabs[(int)((uint)choice % (uint)species)];

                em.CreateEntity(prefab.Archetype, one);
                var e = one[0];
                float angle = (seed >> 4 & 1023) / 1023f * 6.2831853f;
                em.SetComponentData(e, new Game.Objects.Transform(new float3(item.X, item.HeightReal - m_Baseline + m_OffsetY, item.Z), quaternion.RotateY(angle)));
                em.SetComponentData(e, new Game.Prefabs.PrefabRef(prefab.Entity));
                if (em.HasComponent<PseudoRandomSeed>(e)) em.SetComponentData(e, new PseudoRandomSeed((ushort)seed));
                em.SetComponentData(e, Game.Objects.ObjectUtils.InitializeTreeState(0.15f + (seed >> 14 & 1023) / 1023f * 0.55f));
                em.SetComponentEnabled<Game.Objects.Decoration>(e, false);
                if (prefab.Physical) em.AddComponent<Game.Objects.Surface>(e);
            }
            one.Dispose();
            m_TreesPlaced = end;
            TreeCount = end;
            Progress = 0.9f + 0.1f * end / m_TreePlan.Count;
            if (end >= m_TreePlan.Count)
            {
                Mod.log.Info($"Trees: placed {end}.");
                Finish();
            }
        }

        // Replaces the map's water sources with ones that hold each lake at its real level. Each
        // source keeps the water m_Height above the ground at its position, within its radius.
        private void PlaceWaterSources(float baseline, float offsetY)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var water = world?.GetExistingSystemManaged<WaterSystem>();
            if (water == null) return;
            LakeCount = m_Lakes.LakeCount;
            RiverCount = m_Lakes.RiverCount;
            if (water.UseLegacyWaterSources)
            {
                Problem = "LegacyWater";
                Mod.log.Warn("Terrain: this map uses the old water sources; lakes were carved but not filled.");
                return;
            }
            var em = world.EntityManager;
            var desc = new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<WaterSourceData>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            };
            using (var old = em.CreateEntityQuery(desc))
            {
                Mod.log.Info($"Terrain: removing {old.CalculateEntityCount()} old water source(s).");
                em.AddComponent<Deleted>(old);
            }

            foreach (var s in m_Lakes.Sources)
            {
                var e = em.CreateEntity();
                float bed = s.BedReal - baseline + offsetY;
                em.AddComponentData(e, new WaterSourceData
                {
                    m_Radius = s.Radius,
                    m_Height = Math.Max(1f, s.SurfaceReal - s.BedReal),
                    m_Modifier = 1f,
                    m_Id = -1,
                });
                em.AddComponentData(e, new Game.Objects.Transform(new float3((float)s.X, bed, (float)s.Z), quaternion.identity));
                em.AddComponent<Created>(e);
                em.AddComponent<Updated>(e);
            }
            SourceCount = m_Lakes.Sources.Count;
            Mod.log.Info($"Terrain: {LakeCount} lake(s), {SourceCount} water source(s).");
        }

        // Puts the planned water straight into the simulation, so rivers and lakes are full at
        // once instead of filling from their sources. The sea is left to the game. If this ever
        // fails the sources still fill everything, only slowly.
        private void SeedWater()
        {
            var surface = m_SeedSurface;
            var ground = m_SeedGround;
            m_SeedSurface = m_SeedGround = null;
            var water = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<WaterSystem>();
            if (water == null || !InEditor) return;
            if (water.UseLegacyWaterSources)
            {
                water.UpdateSeaLevel();
                return;
            }
            Texture2D texture = null;
            try
            {
                var target = water.WaterTexture;
                int width = target.width, height = target.height;
                if (width != height || kRes % width != 0 || target.graphicsFormat != GraphicsFormat.R32G32B32A32_SFloat)
                    throw new InvalidOperationException($"unexpected water texture {width}x{height} {target.graphicsFormat}");
                int block = kRes / width;
                var data = new float[width * height * 4]; // depth, velocity x, velocity z, pollution
                int filled = 0;
                Parallel.For(0, height, () => 0, (row, _, count) =>
                {
                    for (int col = 0; col < width; col++)
                    {
                        float sumSurface = 0f, sumGround = 0f;
                        int wet = 0;
                        for (int r = row * block; r < (row + 1) * block; r++)
                            for (int c = col * block; c < (col + 1) * block; c++)
                            {
                                int i = r * kRes + c;
                                sumGround += ground[i];
                                if (!float.IsNaN(surface[i]))
                                {
                                    sumSurface += surface[i];
                                    wet++;
                                }
                            }
                        if (wet == 0) continue;
                        float depth = sumSurface / wet - sumGround / (block * block);
                        if (depth <= 0f) continue;
                        data[(row * width + col) * 4] = depth;
                        count++;
                    }
                    return count;
                }, count => System.Threading.Interlocked.Add(ref filled, count));

                texture = new Texture2D(width, height, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.DontInitializePixels);
                texture.SetPixelData(data, 0);
                texture.Apply(false, false);
                Graphics.CopyTexture(texture, target);
                Mod.log.Info($"Terrain: water placed directly in {filled} of {width * height} cells.");
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Terrain: could not place the water directly, the sources will fill it ({ex.Message}).");
            }
            finally
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
            water.UpdateSeaLevel(); // brings the sea back over the new ground
        }

        private static Texture2D ToTexture(float[] heights, float baseline, float scale)
        {
            var data = new ushort[heights.Length];
            float k = 65535f / scale;
            for (int i = 0; i < heights.Length; i++)
            {
                float v = (heights[i] - baseline) * k;
                data[i] = (ushort)(v <= 0 ? 0 : v >= 65535 ? 65535 : v + 0.5f);
            }
            var texture = new Texture2D(kRes, kRes, GraphicsFormat.R16_UNorm, TextureCreationFlags.DontInitializePixels);
            texture.SetPixelData(data, 0);
            texture.Apply(false, false);
            return texture;
        }
    }
}
