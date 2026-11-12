using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace MapOverlay
{
    // Generates the road network of the project's place from OpenStreetMap, framed like the
    // overlay. This half downloads the tiles and plans the network off the main thread;
    // RoadPlacementSystem then hands the plan to the game.
    internal sealed class RoadBuilder
    {
        public enum State { Idle, Downloading, Planning, Placing, Done, Failed, Removing }

        private readonly OsmWater m_Osm = new OsmWater();
        private Task<RoadPlan> m_Task;
        private Task m_Running; // the last planning task, wanted or not: it reads the tiles until it ends
        private RoadPlan m_Ready;
        private int m_Detail;
        private bool m_Names;
        private bool m_RemoveRequested;

        public State Status { get; private set; }
        public float Progress { get; private set; }
        public string Problem { get; private set; } = "";
        public int Segments { get; private set; }
        public int Bridges { get; private set; }
        public int Tunnels { get; private set; }
        public int Skipped { get; private set; }
        public int Named { get; internal set; }
        public int Zoom { get; private set; }
        public bool CanRemove { get; internal set; }

        public bool Busy => Status == State.Downloading || Status == State.Planning || Status == State.Placing || Status == State.Removing;

        // options: bits 0-1 detail (0 main roads, 1 adds streets, 2 adds service roads and tracks),
        // bit 2 real street names.
        public void Start(MapFrame frame, int options)
        {
            if (Busy || (m_Running != null && !m_Running.IsCompleted)) return;
            // A second network on top of the first would double every road.
            if (CanRemove) return;
            Reset();
            m_Detail = Math.Min(2, options & 3);
            m_Names = (options & 4) != 0;
            Status = State.Downloading;
            m_Osm.Start(frame, Setting.kWholeMapMetres);
            if (m_Osm.Failed) Fail("Network");
        }

        public void Cancel()
        {
            if (Status != State.Downloading) return;
            m_Osm.Cancel();
            Reset();
        }

        // A new map or another project: what was reported belongs to the last one.
        public void Forget()
        {
            if (Status == State.Downloading) m_Osm.Cancel();
            // A plan still being worked out is dropped when it arrives.
            if (Status == State.Placing || Status == State.Removing) return;
            Reset();
        }

        // The map itself is gone: whatever stage this was in, it is over.
        internal void Abandon()
        {
            m_Osm.Cancel();
            CanRemove = false;
            Reset();
        }

        public void Dispose()
        {
            m_Osm.Cancel();
        }

        private void Reset()
        {
            m_Task = null;
            m_Ready = null;
            Status = State.Idle;
            Progress = 0f;
            Problem = "";
            Segments = Bridges = Tunnels = Skipped = Named = 0;
        }

        // Main thread, every frame.
        public void Tick()
        {
            if (Status == State.Downloading)
            {
                m_Osm.Tick();
                Progress = 0.6f * m_Osm.Progress;
                if (m_Osm.Failed)
                {
                    Fail("Network");
                    return;
                }
                if (!m_Osm.Done) return;
                Zoom = m_Osm.Zoom;
                Status = State.Planning;
                Progress = 0.65f;
                var osm = m_Osm;
                int detail = m_Detail;
                bool wantNames = m_Names;
                m_Task = Task.Run(() =>
                {
                    var names = new List<OsmWater.RoadName>();
                    var roads = osm.DecodeRoads(names);
                    return RoadPlanner.Build(roads, wantNames ? names : null, detail, Setting.kWholeMapMetres / 2);
                });
                m_Running = m_Task;
            }
            else if (Status == State.Planning)
            {
                // Forgotten while planning: the result is not wanted any more.
                if (m_Task == null)
                {
                    Reset();
                    return;
                }
                if (!m_Task.IsCompleted) return;
                if (m_Task.IsFaulted)
                {
                    Mod.log.Error(m_Task.Exception, "Roads: planning the network failed.");
                    Fail("Build");
                    return;
                }
                var plan = m_Task.Result;
                m_Task = null;
                Mod.log.Info($"Roads: planned {plan.Nodes.Count} junctions, {plan.Chains.Count} roads, {plan.Pieces} segments.");
                if (plan.Pieces == 0)
                {
                    Fail("NoRoads");
                    return;
                }
                m_Ready = plan;
                Status = State.Placing;
                Progress = 0.8f;
            }
        }

        internal RoadPlan TakePlan()
        {
            var plan = m_Ready;
            m_Ready = null;
            return plan;
        }

        internal void Placed(RoadPlan plan, int segments, int skipped)
        {
            Segments = segments;
            Skipped = skipped;
            Bridges = plan.Bridges;
            Tunnels = plan.Tunnels;
            Progress = 0.9f;
        }

        internal void Finished()
        {
            Status = State.Done;
            Progress = 1f;
            CanRemove = true;
        }

        internal void Fail(string problem)
        {
            m_Osm.Cancel();
            m_Task = null;
            m_Ready = null;
            Problem = problem;
            Status = State.Failed;
        }

        public void RequestRemove()
        {
            if (!Busy && CanRemove) m_RemoveRequested = true;
        }

        internal bool TakeRemoveRequest()
        {
            bool requested = m_RemoveRequested;
            m_RemoveRequested = false;
            return requested;
        }

        internal void Removing(float progress)
        {
            Status = State.Removing;
            Progress = progress;
        }

        internal void Removed()
        {
            CanRemove = false;
            Reset();
        }
    }

    // Places a planned network the way the game's own road tool does: one definition per segment
    // (a curve, a road type, heights), which the game turns into nodes, edges, lanes and
    // intersections in the same frame. Segments whose ends share the exact same point are joined
    // by the game, so the whole network goes in at once.
    //
    // Runs in the tool phase, before the game reads the definitions; later in the frame they
    // would be cleared unseen.
    public partial class RoadPlacementSystem : GameSystemBase
    {
        private const int kSettleFrames = 3;

        private const float kNameCell = 32f;

        private PrefabSystem m_PrefabSystem;
        private Game.UI.NameSystem m_NameSystem;
        // Where each named segment was placed, to find its name again on what the game built.
        private readonly Dictionary<long, List<(float3 Middle, string Name)>> m_Named = new Dictionary<long, List<(float3, string)>>();
        private readonly List<(Entity Street, string Name)> m_PendingNames = new List<(Entity, string)>();
        private TerrainSystem m_TerrainSystem;
        private TerrainHeightData m_Heights;
        private EntityArchetype m_Archetype;
        private NativeArray<Entity> m_One;
        private readonly List<Entity> m_Definitions = new List<Entity>();
        private readonly HashSet<Entity> m_Before = new HashSet<Entity>();
        private bool m_HasBefore;
        private int m_Settle;
        private bool m_Placing;
        private readonly List<Entity[]> m_RemoveBatches = new List<Entity[]>();
        private int m_RemoveNext;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_Definitions.Clear();
            m_Before.Clear();
            m_HasBefore = false;
            m_Placing = false;
            m_RemoveBatches.Clear();
            m_Named.Clear();
            m_PendingNames.Clear();
            Mod.Controller?.Roads?.Abandon();
        }

        protected override void OnUpdate()
        {
            var builder = Mod.Controller?.Roads;
            if (builder == null) return;
            try
            {
                if (builder.TakeRemoveRequest()) StartRemove(builder);
                if (m_RemoveBatches.Count > 0) RemoveBatch(builder);
                if (m_Placing) Settle(builder);
                var plan = builder.TakePlan();
                if (plan != null) Place(plan, builder);
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, "Roads: placing the network failed.");
                m_Placing = false;
                m_RemoveBatches.Clear();
                builder.Fail("Build");
            }
        }

        // ---- Road types --------------------------------------------------------------------

        // The game's road for each kind of real road, first one found wins. The tiles do not say
        // how many lanes a road has, so the width follows its importance.
        private static readonly string[] kAnyRoad = { "Small Road" };

        private static string[] Candidates(RoadKind kind, bool oneway)
        {
            switch (kind)
            {
                case RoadKind.Motorway:
                    return oneway ? new[] { "Highway Oneway - 3 lanes", "Highway Oneway - 2 lanes" }
                        : new[] { "Highway Twoway - 4 lanes", "Highway Twoway - 2 lanes", "Large Road Divided" };
                case RoadKind.Ramp:
                    return oneway ? new[] { "Highway Oneway - 1 lane", "Highway Oneway - 2 lanes" }
                        : new[] { "Highway Twoway - 2 lanes", "Small Road" };
                case RoadKind.Trunk:
                    return oneway ? new[] { "Highway Oneway - 2 lanes", "Medium Road Oneway" }
                        : new[] { "Large Road Divided", "Large Road", "Medium Road" };
                case RoadKind.Primary:
                    return oneway ? new[] { "Medium Road Oneway", "Small Road Oneway", "Small Road Oneway - 1 lane" }
                        : new[] { "Medium Road", "Small Road" };
                case RoadKind.Secondary:
                    return oneway ? new[] { "Small Road Oneway", "Small Road Oneway - 1 lane", "Medium Road Oneway" }
                        : new[] { "Small Road" };
                case RoadKind.Service:
                    return oneway ? new[] { "Alley Oneway", "Small Road Oneway - 1 lane", "Small Road Oneway" }
                        : new[] { "Alley", "Small Road" };
                case RoadKind.Track:
                    return oneway ? new[] { "Gravel Road Oneway", "Alley Oneway", "Small Road Oneway - 1 lane" }
                        : new[] { "Gravel Road", "Alley", "Small Road" };
                default:
                    return oneway ? new[] { "Small Road Oneway - 1 lane", "Small Road Oneway" } : kAnyRoad;
            }
        }

        private Entity[] ResolvePrefabs()
        {
            var byName = new Dictionary<string, Entity>();
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<RoadData>(), ComponentType.ReadOnly<NetData>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                {
                    if (!EntityManager.IsComponentEnabled<PrefabData>(entity)) continue;
                    if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(entity, out var prefab) || prefab == null) continue;
                    byName[prefab.name] = entity;
                }

            int kinds = Enum.GetValues(typeof(RoadKind)).Length;
            var result = new Entity[kinds * 2];
            var chosen = new List<string>();
            for (int k = 0; k < kinds; k++)
                for (int oneway = 0; oneway < 2; oneway++)
                {
                    var entity = Entity.Null;
                    string name = null;
                    foreach (string candidate in Candidates((RoadKind)k, oneway == 1))
                        if (byName.TryGetValue(candidate, out entity)) { name = candidate; break; }
                    // A one-way road is better as a two-way street than left out.
                    if (name == null)
                        foreach (string candidate in kAnyRoad)
                            if (byName.TryGetValue(candidate, out entity)) { name = candidate; break; }
                    if (name == null) entity = Entity.Null;
                    result[k * 2 + oneway] = entity;
                    chosen.Add($"{(RoadKind)k}{(oneway == 1 ? " one-way" : "")}: {name ?? "none"}");
                }
            Mod.log.Info("Roads: road types: " + string.Join("; ", chosen));
            if (chosen.Exists(c => c.EndsWith("none")))
            {
                var names = new List<string>(byName.Keys);
                names.Sort(StringComparer.Ordinal);
                Mod.log.Info("Roads: roads this game has: " + string.Join(", ", names));
            }
            return result;
        }

        // ---- Placing -----------------------------------------------------------------------

        private float Ground(double x, double z) => TerrainUtils.SampleHeight(ref m_Heights, new float3((float)x, 0f, (float)z));

        private void Place(RoadPlan plan, RoadBuilder builder)
        {
            m_Heights = m_TerrainSystem.GetHeightData();
            if (!m_Heights.isCreated)
            {
                builder.Fail("Build");
                return;
            }
            var prefabs = ResolvePrefabs();
            bool any = false;
            foreach (var prefab in prefabs) any |= prefab != Entity.Null;
            if (!any)
            {
                builder.Fail("NoPrefab");
                return;
            }

            // What is here before, so "remove" takes away only what this adds.
            if (!m_HasBefore)
            {
                m_Before.Clear();
                foreach (var edge in RoadEdges()) m_Before.Add(edge);
                m_HasBefore = true;
            }

            plan.ResolveHeights(Ground);

            if (!m_Archetype.Valid)
                m_Archetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<CreationDefinition>(), ComponentType.ReadWrite<NetCourse>(), ComponentType.ReadWrite<Updated>());

            // One position per junction, shared bit for bit by every road that ends there.
            var nodes = new float3[plan.Nodes.Count];
            for (int i = 0; i < nodes.Length; i++)
                nodes[i] = new float3((float)plan.Nodes[i].X, plan.Nodes[i].Y, (float)plan.Nodes[i].Z);

            int segments = 0, skipped = 0, seed = 1;
            m_Named.Clear();
            m_One = new NativeArray<Entity>(1, Allocator.Temp);
            foreach (var chain in plan.Chains)
            {
                var prefab = prefabs[(int)chain.Kind * 2 + (chain.Oneway ? 1 : 0)];
                var stops = chain.Stops;
                int last = stops.Length - 1;
                if (prefab == Entity.Null)
                {
                    skipped += last;
                    continue;
                }
                float3 from = nodes[chain.A];
                for (int i = 0; i < last; i++)
                {
                    var a = stops[i];
                    var b = stops[i + 1];
                    float3 to = i + 1 == last ? nodes[chain.B] : new float3((float)b.X, b.Y, (float)b.Z);
                    // A loop's last piece comes back to where its first one started, which is fine;
                    // a piece of no length is not.
                    if (math.distancesq(from, to) < 0.01f)
                    {
                        skipped++;
                        continue;
                    }
                    var middle = Define(prefab, from, to, a, b, seed++, chain.Level > 0);
                    if (chain.Name != null)
                    {
                        long key = NameKey(middle);
                        if (!m_Named.TryGetValue(key, out var list)) m_Named[key] = list = new List<(float3, string)>();
                        list.Add((middle, chain.Name));
                    }
                    segments++;
                    from = to;
                }
            }

            m_One.Dispose();
            m_Placing = true;
            m_Settle = 0;
            builder.Placed(plan, segments, skipped);
            Mod.log.Info($"Roads: placed {segments} segments ({plan.Bridges} bridges, {plan.Tunnels} tunnels, {skipped} skipped) from z{builder.Zoom} tiles.");
        }

        private static long NameKey(float3 p) => ((long)math.floor(p.x / kNameCell) << 32) ^ ((long)math.floor(p.z / kNameCell) & 0xffffffffL);

        private static float3 Middle(Bezier4x3 curve) => (curve.a + curve.b * 3f + curve.c * 3f + curve.d) * 0.125f;

        // Returns the middle of the curve.
        private float3 Define(Entity prefab, float3 from, float3 to, RoadPlan.Stop a, RoadPlan.Stop b, int seed, bool bridge)
        {
            // The segment's own height over the ground, which the game weighs with its two ends'
            // to pick its form. A bridge that leaves the ground is always on pillars, also where
            // it runs lower than the 2 x limit the game asks for (a low bridge over a river):
            // an embankment there would dam the water.
            float middle = (a.Elevation + b.Elevation) * 0.5f;
            if (bridge && (a.Elevation > 0f || b.Elevation > 0f))
            {
                float limit = EntityManager.HasComponent<NetGeometryData>(prefab) ? EntityManager.GetComponentData<NetGeometryData>(prefab).m_ElevationLimit : 4f;
                middle = math.max(middle, limit * 2f + 0.5f);
            }

            // A cubic curve leaving each end along the road's direction there. The handles are a
            // third of the way; the height runs straight between the ends.
            float handle = math.distance(from.xz, to.xz) / 3f;
            float rise = (to.y - from.y) / 3f;
            var curve = new Bezier4x3(
                from,
                new float3(from.x + (float)a.TX * handle, from.y + rise, from.z + (float)a.TZ * handle),
                new float3(to.x - (float)b.TX * handle, to.y - rise, to.z - (float)b.TZ * handle),
                to);

            // The array form: the one-entity overload needs System.Memory, which the mod does not reference.
            EntityManager.CreateEntity(m_Archetype, m_One);
            var entity = m_One[0];
            EntityManager.SetComponentData(entity, new CreationDefinition
            {
                m_Prefab = prefab,
                m_Flags = CreationFlags.Permanent,
                m_RandomSeed = seed,
            });
            EntityManager.SetComponentData(entity, new NetCourse
            {
                m_StartPosition = new CoursePos
                {
                    m_Position = from,
                    m_Rotation = quaternion.identity,
                    m_Elevation = new float2(a.Elevation, a.Elevation),
                    m_CourseDelta = 0f,
                    m_SplitPosition = 0f,
                    m_Flags = CoursePosFlags.IsFirst,
                    m_ParentMesh = -1,
                },
                m_EndPosition = new CoursePos
                {
                    m_Position = to,
                    m_Rotation = quaternion.identity,
                    m_Elevation = new float2(b.Elevation, b.Elevation),
                    m_CourseDelta = 1f,
                    m_SplitPosition = 1f,
                    m_Flags = CoursePosFlags.IsLast,
                    m_ParentMesh = -1,
                },
                m_Curve = curve,
                m_Elevation = new float2(middle, middle),
                m_Length = MathUtils.Length(curve),
                m_FixedIndex = -1,
            });
            m_Definitions.Add(entity);
            return Middle(curve);
        }

        // The game groups the segments it built into streets of its own and names each one. A
        // street takes the real name that most of its length carries, if that is a good part of it.
        private int ApplyNames(List<Entity> built)
        {
            if (m_Named.Count == 0) return 0;
            var streets = new Dictionary<Entity, Dictionary<string, float>>();
            foreach (var edge in built)
            {
                if (!EntityManager.HasComponent<Aggregated>(edge) || !EntityManager.HasComponent<Curve>(edge)) continue;
                var street = EntityManager.GetComponentData<Aggregated>(edge).m_Aggregate;
                if (street == Entity.Null || !EntityManager.Exists(street)) continue;
                var curve = EntityManager.GetComponentData<Curve>(edge);
                var middle = Middle(curve.m_Bezier);
                string name = "";
                float best = 24f * 24f;
                long cx = (long)math.floor(middle.x / kNameCell), cz = (long)math.floor(middle.z / kNameCell);
                for (long x = cx - 1; x <= cx + 1; x++)
                    for (long z = cz - 1; z <= cz + 1; z++)
                    {
                        if (!m_Named.TryGetValue((x << 32) ^ (z & 0xffffffffL), out var list)) continue;
                        foreach (var item in list)
                        {
                            float distance = math.distancesq(item.Middle, middle);
                            if (distance >= best) continue;
                            best = distance;
                            name = item.Name;
                        }
                    }
                if (!streets.TryGetValue(street, out var votes)) streets[street] = votes = new Dictionary<string, float>();
                votes.TryGetValue(name, out float length);
                votes[name] = length + math.max(1f, curve.m_Length);
            }

            int named = 0;
            foreach (var street in streets)
            {
                string winner = "";
                float most = 0f, total = 0f;
                foreach (var vote in street.Value)
                {
                    total += vote.Value;
                    if (vote.Key.Length > 0 && vote.Value > most) { most = vote.Value; winner = vote.Key; }
                }
                if (winner.Length == 0 || most < 0.4f * total) continue;
                m_PendingNames.Add((street.Key, winner));
                named++;
            }
            m_Named.Clear();
            return named;
        }

        // Called in the UI phase, where the game's own rename box runs: the name system may not
        // be used during the tool phase.
        internal void ApplyPendingNames()
        {
            if (m_PendingNames.Count == 0) return;
            int applied = 0;
            try
            {
                foreach (var (street, name) in m_PendingNames)
                {
                    if (!EntityManager.Exists(street)) continue;
                    m_NameSystem.SetCustomName(street, name);
                    applied++;
                }
                Mod.log.Info($"Roads: named {applied} streets.");
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, $"Roads: naming the streets failed after {applied}.");
            }
            m_PendingNames.Clear();
        }

        // The game builds from a definition in the frame it is created and clears its Updated tag
        // at the end of that frame. The active tool normally destroys it the next frame; any it
        // leaves behind are removed here so no later tool action applies them again.
        private void Settle(RoadBuilder builder)
        {
            int pending = 0;
            foreach (var entity in m_Definitions)
                if (EntityManager.Exists(entity) && EntityManager.HasComponent<Updated>(entity)) pending++;
            if (pending > 0)
            {
                m_Settle = 0;
                return;
            }
            if (++m_Settle < kSettleFrames) return;
            foreach (var entity in m_Definitions)
                if (EntityManager.Exists(entity) && EntityManager.HasComponent<CreationDefinition>(entity)) EntityManager.DestroyEntity(entity);
            m_Definitions.Clear();
            m_Placing = false;
            var built = new List<Entity>();
            foreach (var edge in RoadEdges())
                if (!m_Before.Contains(edge)) built.Add(edge);
            // The roads are in whatever happens to the names.
            int named = 0;
            try
            {
                named = ApplyNames(built);
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, "Roads: matching the street names failed.");
            }
            builder.Named = named;
            Mod.log.Info($"Roads: the game built {built.Count} road segments; {named} streets take their real names.");
            builder.Finished();
        }

        // ---- Removing ----------------------------------------------------------------------

        private List<Entity> RoadEdges()
        {
            var result = new List<Entity>();
            var description = new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Road>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Owner>() },
            };
            using (var query = EntityManager.CreateEntityQuery(description))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities) result.Add(entity);
            return result;
        }

        private const int kRemovePerFrame = 512;
        private const float kRemoveCell = 8f;

        // Z-order of a cell: neighbouring codes are neighbouring places, so a run of them is one
        // compact area.
        private static long Morton(int x, int z)
        {
            uint ux = (uint)(x + (1 << 20)) & 0x1FFFFF, uz = (uint)(z + (1 << 20)) & 0x1FFFFF;
            long code = 0;
            for (int bit = 0; bit < 21; bit++)
                code |= ((long)((ux >> bit) & 1) << (2 * bit)) | ((long)((uz >> bit) & 1) << (2 * bit + 1));
            return code;
        }

        // Deletes the roads added since the map was loaded, and the junctions only they use.
        // Deleting a whole network in one frame makes the game run out of memory and exit, so it
        // goes area by area, a batch a frame, each junction with the last of its roads: only the
        // junctions on a batch's border are left half-connected for a frame and rebuilt.
        private void StartRemove(RoadBuilder builder)
        {
            if (!m_HasBefore || m_Placing || m_RemoveBatches.Count > 0) return;
            var edges = new HashSet<Entity>();
            foreach (var edge in RoadEdges())
                if (!m_Before.Contains(edge)) edges.Add(edge);

            // For each junction, how many of its roads are going; -1 if any road stays.
            var left = new Dictionary<Entity, int>();
            var ordered = new List<(long Code, Entity Edge, Entity Start, Entity End)>(edges.Count);
            foreach (var entity in edges)
            {
                var edge = EntityManager.GetComponentData<Edge>(entity);
                float3 middle = default;
                if (EntityManager.HasComponent<Node>(edge.m_Start) && EntityManager.HasComponent<Node>(edge.m_End))
                    middle = (EntityManager.GetComponentData<Node>(edge.m_Start).m_Position + EntityManager.GetComponentData<Node>(edge.m_End).m_Position) * 0.5f;
                ordered.Add((Morton((int)math.floor(middle.x / kRemoveCell), (int)math.floor(middle.z / kRemoveCell)), entity, edge.m_Start, edge.m_End));
                Count(edge.m_Start, edges, left);
                Count(edge.m_End, edges, left);
            }
            ordered.Sort((a, b) => a.Code != b.Code ? a.Code.CompareTo(b.Code) : a.Edge.Index.CompareTo(b.Edge.Index));

            var batch = new List<Entity>();
            int inBatch = 0;
            foreach (var item in ordered)
            {
                batch.Add(item.Edge);
                Release(item.Start, left, batch);
                if (item.End != item.Start) Release(item.End, left, batch);
                if (++inBatch < kRemovePerFrame) continue;
                m_RemoveBatches.Add(batch.ToArray());
                batch.Clear();
                inBatch = 0;
            }
            if (batch.Count > 0) m_RemoveBatches.Add(batch.ToArray());
            m_RemoveNext = 0;
            Mod.log.Info($"Roads: removing {edges.Count} road segments in {m_RemoveBatches.Count} steps.");
            if (m_RemoveBatches.Count == 0) builder.Removed();
            else builder.Removing(0f);
        }

        private void Count(Entity node, HashSet<Entity> edges, Dictionary<Entity, int> left)
        {
            if (left.ContainsKey(node) || !EntityManager.Exists(node)) return;
            int count = 0;
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                var connected = EntityManager.GetBuffer<ConnectedEdge>(node, true);
                for (int i = 0; i < connected.Length; i++)
                {
                    if (edges.Contains(connected[i].m_Edge)) count++;
                    else if (EntityManager.Exists(connected[i].m_Edge) && !EntityManager.HasComponent<Deleted>(connected[i].m_Edge))
                    {
                        count = -1;
                        break;
                    }
                }
            }
            left[node] = count;
        }

        private static void Release(Entity node, Dictionary<Entity, int> left, List<Entity> batch)
        {
            if (!left.TryGetValue(node, out int count) || count < 0) return;
            left[node] = --count;
            if (count <= 0)
            {
                left[node] = -1;
                batch.Add(node);
            }
        }

        private void RemoveBatch(RoadBuilder builder)
        {
            var alive = new List<Entity>();
            foreach (var entity in m_RemoveBatches[m_RemoveNext])
                if (EntityManager.Exists(entity) && !EntityManager.HasComponent<Deleted>(entity)) alive.Add(entity);
            if (alive.Count > 0)
            {
                var array = new NativeArray<Entity>(alive.ToArray(), Allocator.Temp);
                EntityManager.AddComponent<Deleted>(array);
                EntityManager.AddComponent<Updated>(array);
                array.Dispose();
            }
            m_RemoveNext++;
            if (m_RemoveNext < m_RemoveBatches.Count)
            {
                builder.Removing((float)m_RemoveNext / m_RemoveBatches.Count);
                return;
            }
            m_RemoveBatches.Clear();
            Mod.log.Info("Roads: removed.");
            builder.Removed();
        }
    }
}
