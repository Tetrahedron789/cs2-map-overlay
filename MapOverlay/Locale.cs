using System.Collections.Generic;
using Colossal;

namespace MapOverlay
{
    // English only; registered for every game language.
    public class Locale : IDictionarySource
    {
        // Keys read by the in-game panel (UI/src/mods/panel.tsx).
        public const string kUIPrefix = "MapOverlay.UI.";

        private readonly Setting m_Setting;

        public Locale(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var s = m_Setting;
            return new Dictionary<string, string>
            {
                { s.GetSettingsLocaleID(), "Map Overlay" },
                { s.GetOptionTabLocaleID(Setting.kSection), "Main" },

                { s.GetOptionGroupLocaleID(Setting.kViewGroup), "View" },
                { s.GetOptionGroupLocaleID(Setting.kMapGroup), "Map" },
                { s.GetOptionGroupLocaleID(Setting.kCacheGroup), "Cache" },

                { s.GetOptionLabelLocaleID(nameof(Setting.Visible)), "Show map" },
                { s.GetOptionDescLocaleID(nameof(Setting.Visible)), "Shows the map over the terrain. You can also use the mod's button at the top left of the game." },
                { s.GetOptionLabelLocaleID(nameof(Setting.Opacity)), "Opacity" },
                { s.GetOptionDescLocaleID(nameof(Setting.Opacity)), "How visible the map is. 100% makes it opaque." },
                { s.GetOptionLabelLocaleID(nameof(Setting.OnTop)), "On top of everything" },
                { s.GetOptionDescLocaleID(nameof(Setting.OnTop)), "Draws the map over buildings, roads and hills, like tracing paper. Turn off to lay it on the terrain so objects cover it." },
                { s.GetOptionLabelLocaleID(nameof(Setting.HeightOffset)), "Height above terrain (m)" },
                { s.GetOptionDescLocaleID(nameof(Setting.HeightOffset)), "Gap between the map and the terrain. Raise it if the map flickers or sinks into the ground with \"On top of everything\" off." },

                { s.GetOptionLabelLocaleID(nameof(Setting.Zoom)), "Detail level" },
                { s.GetOptionDescLocaleID(nameof(Setting.Zoom)), "Tile zoom level. It only changes the sharpness, not the scale. Higher = sharper, but more downloads. Large areas or small scales lower it automatically." },
                { s.GetOptionLabelLocaleID(nameof(Setting.AreaKm)), "Area size (km)" },
                { s.GetOptionDescLocaleID(nameof(Setting.AreaKm)), "Size of the square of map loaded around the anchor point. It loads whole tiles, so the real size is rounded to the nearest ring of tiles." },
                { s.GetOptionLabelLocaleID(nameof(Setting.WholeMap)), "Whole map" },
                { s.GetOptionDescLocaleID(nameof(Setting.WholeMap)), "Covers the whole playable area (14.3 km). The detail level drops as needed to keep the number of tiles manageable, and new tiles download only around where the camera looks." },
                { s.GetOptionLabelLocaleID(nameof(Setting.FollowCamera)), "Load where you look" },
                { s.GetOptionDescLocaleID(nameof(Setting.FollowCamera)), "When the camera moves away from the centre, loads the map of the new area without moving it." },

                { s.GetOptionLabelLocaleID(nameof(Setting.Source)), "Map style" },
                { s.GetOptionDescLocaleID(nameof(Setting.Source)), "Tile server. Topographic adds contour lines, handy with heightmaps. Custom uses the tile URL entered in the in-game panel." },
                { s.GetEnumValueLocaleID(TileSource.OpenStreetMap), "OpenStreetMap" },
                { s.GetEnumValueLocaleID(TileSource.OpenTopoMap), "Topographic (OpenTopoMap)" },
                { s.GetEnumValueLocaleID(TileSource.Custom), "Custom URL" },
                { s.GetOptionLabelLocaleID(nameof(Setting.ScalePercent)), "Map scale (%)" },
                { s.GetOptionDescLocaleID(nameof(Setting.ScalePercent)), "100% is 1:1: one real metre is one game metre. To match a Terraining heightmap: map size 57.344 km = 100%, 28.672 km = 200%, 114.688 km = 50%." },

                { s.GetOptionLabelLocaleID(nameof(Setting.ClearCache)), "Clear tile cache" },
                { s.GetOptionDescLocaleID(nameof(Setting.ClearCache)), "Deletes downloaded tiles in ModsData\\MapOverlay\\cache. They download again when shown." },
                { s.GetOptionWarningLocaleID(nameof(Setting.ClearCache)), "Delete all downloaded tiles?" },

                { kUIPrefix + "Title", "Map Overlay" },
                { kUIPrefix + "Visible", "Show map" },
                { kUIPrefix + "Opacity", "Opacity" },
                { kUIPrefix + "OnTop", "On top of everything" },
                { kUIPrefix + "Follow", "Load where you look" },
                { kUIPrefix + "SearchPlaceholder", "Place, address or coordinates" },
                { kUIPrefix + "Search", "Search" },
                { kUIPrefix + "Searching", "Searching…" },
                { kUIPrefix + "NoResults", "No results." },
                { kUIPrefix + "SearchError", "Search failed. Check your connection." },
                { kUIPrefix + "SearchHint", "The chosen place appears where the camera looks." },
                { kUIPrefix + "Move", "Position" },
                { kUIPrefix + "Step", "Step" },
                { kUIPrefix + "Drag", "Drag on the map" },
                { kUIPrefix + "DragActive", "Dragging: click to finish" },
                { kUIPrefix + "DragHint", "Drag to move the map. Shift + drag turns it around the point the camera looks at. Right click or Esc to finish." },
                { kUIPrefix + "MoveHint", "Arrows follow the camera view. ◎ brings the map centre to the camera." },
                { kUIPrefix + "BringHere", "Bring centre here" },
                { kUIPrefix + "Rotation", "Rotation" },
                { kUIPrefix + "Loading", "Map" },
                { kUIPrefix + "Source", "Map style" },
                { kUIPrefix + "SourceOsm", "OpenStreetMap" },
                { kUIPrefix + "SourceTopo", "Topographic" },
                { kUIPrefix + "SourceCustom", "Custom" },
                { kUIPrefix + "TopoHint", "Contour lines and hill shading: useful to line up a heightmap." },
                { kUIPrefix + "CustomHint", "Tile URL with {z}, {x} and {y}. Paste one from a provider where you have your own key (MapTiler, Stadia, Thunderforest…) and follow its terms." },
                { kUIPrefix + "CustomAttribution", "Credit your tile provider" },
                { kUIPrefix + "Apply", "Apply" },
                { kUIPrefix + "DetailLimited", "Using {z}: the area needs too many tiles for more." },
                { kUIPrefix + "Scale", "Scale" },
                { kUIPrefix + "ScaleLabel", "Map scale" },
                { kUIPrefix + "ScaleHint", "The playable area holds {km} of real ground." },
                { kUIPrefix + "TerrainingSize", "Terraining map size (km)" },
                { kUIPrefix + "TerrainingHint", "Same value as \"Map size\" in Terraining. 57.344 km is 1:1." },
                { kUIPrefix + "TerrainingRange", "Enter a size between {min} and {max} km." },
                { kUIPrefix + "AlignCentre", "Put map centre on the city centre" },
                { kUIPrefix + "Centred", "Map centre is on the city centre" },
                { kUIPrefix + "AlignHint", "Heightmaps are centred on the city. Search the same centre point you used in Terraining, then press this. Rotation must match too." },
                { kUIPrefix + "Detail", "Detail" },
                { kUIPrefix + "Area", "Area" },
                { kUIPrefix + "WholeMap", "Whole map" },
                { kUIPrefix + "WholeMapConfirm", "To load the whole map, detail drops from {from} to {to}." },
                { kUIPrefix + "WholeMapHint", "Covers the whole playable area. To go easy on the tile server, new tiles download only around where the camera looks; move the camera to fill in the rest." },
                { kUIPrefix + "Continue", "Continue" },
                { kUIPrefix + "Cancel", "Cancel" },
                { kUIPrefix + "Tiles", "Tiles" },
                { kUIPrefix + "Failed", "failed" },
                { kUIPrefix + "ProblemNetwork", "Some tiles did not download. Check your connection." },
                { kUIPrefix + "ProblemBlocked", "The server asked to wait. Downloads resume in a minute." },
                { kUIPrefix + "ProblemBadUrl", "Enter a tile URL with {z}, {x} and {y} in Map → Custom." },
            };
        }

        public void Unload()
        {
        }
    }
}
