using Colossal.UI.Binding;
using Game.UI;

namespace MapOverlay
{
    // Exposes the settings and the overlay state to the in-game panel (UI\src). Values are read
    // every UI update, so changes made from the Options menu also show up in the panel.
    public partial class MapOverlayUISystem : UISystemBase
    {
        private const string kGroup = "mapOverlay";

        private MapDragTool m_DragTool;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_DragTool = World.GetOrCreateSystemManaged<MapDragTool>();
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "dragging", () => m_DragTool.IsActive));
            AddBinding(new TriggerBinding<bool>(kGroup, "setDragging", v => m_DragTool.SetActive(v && !(Mod.Controller?.Locked ?? false))));

            AddUpdateBinding(new GetterValueBinding<string>(kGroup, "projects", () => Mod.Controller?.Projects?.Json ?? "[]"));
            AddUpdateBinding(new GetterValueBinding<string>(kGroup, "activeProject", () => Mod.Controller?.Projects?.Active?.Id ?? ""));
            AddUpdateBinding(new GetterValueBinding<string>(kGroup, "activeProjectName", () => Mod.Controller?.Projects?.Active?.Name ?? ""));
            AddUpdateBinding(new GetterValueBinding<string>(kGroup, "activeProjectTerrain", () => Mod.Controller?.Projects?.ActiveTerrainDate ?? ""));
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "projectLocked", () => Mod.Controller?.Locked ?? false));
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "projectDirty", () => Mod.Controller?.Projects?.Dirty ?? false));
            AddBinding(new TriggerBinding<string>(kGroup, "createProject", name => { m_DragTool.SetActive(false); Mod.Controller?.CreateProject(name); }));
            AddBinding(new TriggerBinding<string>(kGroup, "openProject", id => { m_DragTool.SetActive(false); Mod.Controller?.OpenProject(id); }));
            AddBinding(new TriggerBinding(kGroup, "saveProject", () => Mod.Controller?.Projects?.Save()));
            AddBinding(new TriggerBinding(kGroup, "closeProject", () => { m_DragTool.SetActive(false); Mod.Controller?.CloseProject(); }));
            AddBinding(new TriggerBinding<string>(kGroup, "deleteProject", id => { m_DragTool.SetActive(false); Mod.Controller?.DeleteProject(id); }));
            AddBinding(new TriggerBinding<bool>(kGroup, "setProjectLocked", locked =>
            {
                if (locked) m_DragTool.SetActive(false);
                Mod.Controller?.Projects?.SetLocked(locked);
            }));

            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "visible", () => Mod.Setting?.Visible ?? false));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "opacity", () => Mod.Setting?.Opacity ?? 50));
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "onTop", () => Mod.Setting?.OnTop ?? true));
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "follow", () => Mod.Setting?.FollowCamera ?? true));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "zoom", () => Mod.Setting?.Zoom ?? 15));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "effectiveZoom", () => Mod.Controller?.EffectiveZoom ?? 15));
            AddUpdateBinding(new GetterValueBinding<float>(kGroup, "scale", () => Mod.Setting?.ScalePercent ?? 100f));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "source", () => (int)(Mod.Setting?.Source ?? TileSource.OpenStreetMap)));
            AddUpdateBinding(new GetterValueBinding<string>(kGroup, "customUrl", () => Mod.Setting?.CustomUrl ?? ""));
            AddUpdateBinding(new GetterValueBinding<double>(kGroup, "anchorX", () => Mod.Setting?.AnchorX ?? 0));
            AddUpdateBinding(new GetterValueBinding<double>(kGroup, "anchorZ", () => Mod.Setting?.AnchorZ ?? 0));
            AddUpdateBinding(new GetterValueBinding<double>(kGroup, "areaMetres", () => Mod.Controller?.AreaMetres ?? 3000));
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "canGrowArea", () => Mod.Controller?.CanGrowArea ?? false));
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "canShrinkArea", () => Mod.Controller?.CanShrinkArea ?? false));
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "wholeMap", () => Mod.Setting?.WholeMap ?? false));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "wholeMapZoom", () => Mod.Controller?.WholeMapZoom ?? Setting.kMinZoom));
            AddUpdateBinding(new GetterValueBinding<float>(kGroup, "rotation", () => Mod.Setting?.Rotation ?? 0f));
            AddUpdateBinding(new GetterValueBinding<double>(kGroup, "lat", () => Mod.Setting?.CenterLat ?? 0));
            AddUpdateBinding(new GetterValueBinding<double>(kGroup, "lon", () => Mod.Setting?.CenterLon ?? 0));
            AddUpdateBinding(new GetterValueBinding<string>(kGroup, "attribution", () => Mod.Controller?.Attribution ?? ""));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "tilesReady", () => Mod.Controller?.TilesReady ?? 0));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "tilesTotal", () => Mod.Controller?.TilesTotal ?? 0));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "tilesFailed", () => Mod.Controller?.TilesFailed ?? 0));
            AddUpdateBinding(new GetterValueBinding<string>(kGroup, "problem", () => Mod.Controller?.Problem ?? ""));
            AddUpdateBinding(new GetterValueBinding<string>(kGroup, "searchStatus", () => Mod.Controller?.Geocoder?.Status ?? ""));
            AddUpdateBinding(new GetterValueBinding<string>(kGroup, "searchResults", () => Mod.Controller?.Geocoder?.ResultsJson ?? "[]"));

            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "inEditor", () => TerrainBuilder.InEditor));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "terrainState", () => (int)(Mod.Controller?.Terrain?.Status ?? TerrainBuilder.State.Idle)));
            AddUpdateBinding(new GetterValueBinding<float>(kGroup, "terrainProgress", () => Mod.Controller?.Terrain?.Progress ?? 0f));
            AddUpdateBinding(new GetterValueBinding<string>(kGroup, "terrainProblem", () => Mod.Controller?.Terrain?.Problem ?? ""));
            AddUpdateBinding(new GetterValueBinding<float>(kGroup, "terrainLowest", () => Mod.Controller?.Terrain?.LowestMetres ?? 0f));
            AddUpdateBinding(new GetterValueBinding<float>(kGroup, "terrainHighest", () => Mod.Controller?.Terrain?.HighestMetres ?? 0f));
            AddUpdateBinding(new GetterValueBinding<float>(kGroup, "terrainWaterLevel", () => Mod.Controller?.Terrain?.WaterLevel ?? 0f));
            AddUpdateBinding(new GetterValueBinding<float>(kGroup, "terrainLowestInGame", () => Mod.Controller?.Terrain?.LowestInGame ?? 0f));
            AddUpdateBinding(new GetterValueBinding<float>(kGroup, "terrainHeightScale", () => Mod.Controller?.Terrain?.HeightScale ?? 0f));
            AddBinding(new TriggerBinding<int>(kGroup, "buildTerrain", options => Mod.Controller?.BuildTerrain(options)));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "terrainRivers", () => Mod.Controller?.Terrain?.RiverCount ?? 0));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "terrainTrees", () => Mod.Controller?.Terrain?.TreeCount ?? 0));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "terrainLakes", () => Mod.Controller?.Terrain?.LakeCount ?? 0));
            AddUpdateBinding(new GetterValueBinding<int>(kGroup, "terrainSources", () => Mod.Controller?.Terrain?.SourceCount ?? 0));
            AddBinding(new TriggerBinding(kGroup, "cancelTerrain", () => Mod.Controller?.Terrain?.Cancel()));

            AddBinding(new TriggerBinding<bool>(kGroup, "setVisible", v => Change(s => s.Visible = v)));
            AddBinding(new TriggerBinding<int>(kGroup, "setOpacity", v => Change(s => s.Opacity = v)));
            AddBinding(new TriggerBinding<bool>(kGroup, "setOnTop", v => Change(s => s.OnTop = v)));
            AddBinding(new TriggerBinding<bool>(kGroup, "setFollow", v => Change(s => s.FollowCamera = v)));
            AddBinding(new TriggerBinding<int>(kGroup, "setZoom", v => Change(s => s.Zoom = v)));
            AddBinding(new TriggerBinding<int>(kGroup, "stepArea", d => Mod.Controller?.StepArea(d)));
            AddBinding(new TriggerBinding<bool>(kGroup, "setWholeMap", v => Change(s => s.WholeMap = v)));
            AddBinding(new TriggerBinding<float>(kGroup, "setRotation", v => Mod.Controller?.SetRotation(v)));
            AddBinding(new TriggerBinding<float>(kGroup, "setScale", v => Mod.Controller?.SetScale(v)));
            AddBinding(new TriggerBinding<int>(kGroup, "setSource", v => Change(s => s.Source = (TileSource)System.Math.Max(0, System.Math.Min(2, v)))));
            AddBinding(new TriggerBinding<string>(kGroup, "setCustomUrl", v => Change(s => s.CustomUrl = (v ?? "").Trim())));
            AddBinding(new TriggerBinding(kGroup, "alignCentre", () => Mod.Controller?.AlignToMapCentre()));
            AddBinding(new TriggerBinding<float, float>(kGroup, "nudge", (right, forward) => Mod.Controller?.Nudge(right, forward)));
            AddBinding(new TriggerBinding(kGroup, "bringHere", () => Mod.Controller?.BringHere()));
            AddBinding(new TriggerBinding<string>(kGroup, "search", q => Mod.Controller?.Geocoder?.Search(q)));
            AddBinding(new TriggerBinding<string>(kGroup, "suggest", q => Mod.Controller?.Geocoder?.Suggest(q)));
            AddBinding(new TriggerBinding(kGroup, "clearSearch", () => Mod.Controller?.Geocoder?.Clear()));
            AddBinding(new TriggerBinding<double, double>(kGroup, "goTo", (lat, lon) => Mod.Controller?.GoTo(lat, lon)));
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, Game.GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            Mod.Controller?.OnMapLoaded();
        }

        private static void Change(System.Action<Setting> change)
        {
            var setting = Mod.Setting;
            if (setting == null) return;
            change(setting);
            setting.ApplyAndSave();
        }
    }
}
