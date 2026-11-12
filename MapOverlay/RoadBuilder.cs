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
        public enum State { Idle, Downloading, Planning, Placing, Done, Failed }

        private readonly OsmWater m_Osm = new OsmWater();
        private Task<RoadPlan> m_Task;
        private Task m_Running; // the last planning task, wanted or not: it reads the tiles until it ends
        private RoadPlan m_Ready;
        private int m_Detail;
        private bool m_RemoveRequested;

        public State Status { get; private set; }
        public float Progress { get; private set; }
        public string Problem { get; private set; } = "";
        public int Segments { get; private set; }
        public int Bridges { get; private set; }
        public int Tunnels { get; private set; }
        public int Skipped { get; private set; }
        public int Zoom { get; private set; }
        public bool CanRemove { get; internal set; }

        public bool Busy => Status == State.Downloading || Status == State.Planning || Status == State.Placing;

        // options: bits 0-1 detail (0 main roads, 1 adds streets, 2 adds service roads and tracks).
        public void Start(MapFrame frame, int options)
        {
            if (Busy || (m_Running != null && !m_Running.IsCompleted)) return;
            Reset();
            m_Detail = Math.Min(2, options & 3);
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
            if (Status == State.Placing) return;
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
            Segments = Bridges = Tunnels = Skipped = 0;
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
                m_Task = Task.Run(() => RoadPlanner.Build(osm.DecodeRoads(), detail, Setting.kWholeMapMetres / 2));
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

        private PrefabSystem m_PrefabSystem;
        private TerrainSystem m_TerrainSystem;
        private TerrainHeightData m_Heights;
        private EntityArchetype m_Archetype;
        private NativeArray<Entity> m_One;
        private readonly List<Entity> m_Definitions = new List<Entity>();
        private readonly HashSet<Entity> m_Before = new HashSet<Entity>();
        private bool m_HasBefore;
        private int m_Settle;
        private bool m_Placing;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_Definitions.Clear();
            m_Before.Clear();
            m_HasBefore = false;
            m_Placing = false;
            Mod.Controller?.Roads?.Abandon();
        }

        protected override void OnUpdate()
        {
            var builder = Mod.Controller?.Roads;
            if (builder == null) return;
            try
            {
                if (builder.TakeRemoveRequest()) Remove(builder);
                if (m_Placing) Settle(builder);
                var plan = builder.TakePlan();
                if (plan != null) Place(plan, builder);
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, "Roads: placing the network failed.");
                m_Placing = false;
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
                    Define(prefab, from, to, a, b, seed++);
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

        private void Define(Entity prefab, float3 from, float3 to, RoadPlan.Stop a, RoadPlan.Stop b, int seed)
        {
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
                m_Elevation = new float2(a.Elevation, b.Elevation),
                m_Length = MathUtils.Length(curve),
                m_FixedIndex = -1,
            });
            m_Definitions.Add(entity);
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
            int built = 0;
            foreach (var edge in RoadEdges())
                if (!m_Before.Contains(edge)) built++;
            Mod.log.Info($"Roads: the game built {built} road segments.");
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

        // Deletes the roads added since the map was loaded, and the junctions only they use. All in
        // one frame: a deleted road whose junction stays makes the game rebuild that junction.
        private void Remove(RoadBuilder builder)
        {
            if (!m_HasBefore || m_Placing) return;
            var edges = new HashSet<Entity>();
            foreach (var edge in RoadEdges())
                if (!m_Before.Contains(edge)) edges.Add(edge);
            var doomed = new List<Entity>(edges);
            var nodes = new HashSet<Entity>();
            foreach (var entity in edges)
            {
                var edge = EntityManager.GetComponentData<Edge>(entity);
                nodes.Add(edge.m_Start);
                nodes.Add(edge.m_End);
            }
            var shared = new List<Entity>();
            foreach (var node in nodes)
            {
                if (!EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node)) continue;
                bool used = false;
                if (EntityManager.HasBuffer<ConnectedEdge>(node))
                {
                    var connected = EntityManager.GetBuffer<ConnectedEdge>(node, true);
                    for (int i = 0; i < connected.Length; i++)
                        if (!edges.Contains(connected[i].m_Edge) && EntityManager.Exists(connected[i].m_Edge) && !EntityManager.HasComponent<Deleted>(connected[i].m_Edge))
                        {
                            used = true;
                            break;
                        }
                }
                if (used) shared.Add(node);
                else doomed.Add(node);
            }
            if (doomed.Count > 0)
                using (var array = new NativeArray<Entity>(doomed.ToArray(), Allocator.Temp))
                {
                    EntityManager.AddComponent<Deleted>(array);
                    EntityManager.AddComponent<Updated>(array);
                }
            // Junctions that keep other roads rebuild themselves without the removed ones.
            foreach (var node in shared)
                if (!EntityManager.HasComponent<Updated>(node)) EntityManager.AddComponent<Updated>(node);
            Mod.log.Info($"Roads: removed {edges.Count} road segments.");
            builder.Removed();
        }
    }
}
