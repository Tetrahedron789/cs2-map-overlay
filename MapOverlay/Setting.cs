using System;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;

namespace MapOverlay
{
    [FileLocation(nameof(MapOverlay))]
    [SettingsUIGroupOrder(kViewGroup, kMapGroup, kCacheGroup)]
    [SettingsUIShowGroupName(kViewGroup, kMapGroup, kCacheGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kViewGroup = "View";
        public const string kMapGroup = "Map";
        public const string kCacheGroup = "Cache";

        public const int kMinZoom = 14;
        public const int kMaxZoom = 15;
        public const int kMinArea = 1;
        public const int kMaxArea = 10;

        // Lowest detail used when a large area or a small scale would need too many tiles.
        public const int kFloorZoom = 12;

        // Map scale, in % (game metres per 100 real metres). Terraining's CS2 sizes span 28.672 to 229.376 km.
        public const float kMinScale = 25f;
        public const float kMaxScale = 200f;

        // Side of the whole CS2 world map (playable area plus surroundings), as heightmap tools size it.
        public const double kWorldMapMetres = 57344.0;

        // Side of the playable area of a CS2 map, centred on the world origin.
        public const double kWholeMapMetres = 14336.0;

        // Shown in the panel when the place is unknown: the centre of the Zócalo, Mexico City.
        public const double kDefaultLat = 19.432608;
        public const double kDefaultLon = -99.133209;

        private int m_Opacity = 50;
        private int m_Zoom = 15;
        private int m_AreaKm = 3;
        private float m_HeightOffset = 1f;
        private float m_Rotation;
        private float m_Scale = 100f;

        public Setting(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(kSection, kViewGroup)]
        public bool Visible { get; set; } = true;

        [SettingsUISlider(min = 0, max = 100, step = 1, unit = Unit.kPercentage, updateOnDragEnd = true)]
        [SettingsUISection(kSection, kViewGroup)]
        public int Opacity
        {
            get => m_Opacity;
            set => m_Opacity = Math.Max(0, Math.Min(100, value));
        }

        [SettingsUISection(kSection, kViewGroup)]
        public bool OnTop { get; set; } = true;

        [SettingsUISlider(min = 0, max = 50, step = 0.5f, unit = Unit.kFloatSingleFraction, updateOnDragEnd = true)]
        [SettingsUISection(kSection, kViewGroup)]
        public float HeightOffset
        {
            get => m_HeightOffset;
            set => m_HeightOffset = Math.Max(0f, Math.Min(50f, value));
        }

        [SettingsUISlider(min = kMinZoom, max = kMaxZoom, step = 1, unit = Unit.kInteger, updateOnDragEnd = true)]
        [SettingsUISection(kSection, kMapGroup)]
        public int Zoom
        {
            get => m_Zoom;
            set => m_Zoom = Math.Max(kMinZoom, Math.Min(kMaxZoom, value));
        }

        [SettingsUISlider(min = kMinArea, max = kMaxArea, step = 1, unit = Unit.kInteger, updateOnDragEnd = true)]
        [SettingsUISection(kSection, kMapGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsWholeMap))]
        public int AreaKm
        {
            get => m_AreaKm;
            set => m_AreaKm = Math.Max(kMinArea, Math.Min(kMaxArea, value));
        }

        // Covers the playable area; the detail level drops as needed to stay within the tile limit.
        [SettingsUISection(kSection, kMapGroup)]
        public bool WholeMap { get; set; }

        [SettingsUISection(kSection, kMapGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsWholeMap))]
        public bool FollowCamera { get; set; } = true;

        [SettingsUISection(kSection, kMapGroup)]
        public TileSource Source { get; set; } = TileSource.OpenStreetMap;

        // 100% is 1:1. Below 100% more real ground fits in the city.
        [SettingsUISlider(min = kMinScale, max = kMaxScale, step = 0.5f, unit = Unit.kFloatSingleFraction, updateOnDragEnd = true)]
        [SettingsUISection(kSection, kMapGroup)]
        public float ScalePercent
        {
            get => m_Scale;
            set => m_Scale = float.IsNaN(value) || float.IsInfinity(value) ? 100f : Math.Max(kMinScale, Math.Min(kMaxScale, value));
        }

        // Edited from the in-game panel ("Custom" source).
        [SettingsUIHidden]
        public string CustomUrl { get; set; } = "";

        // Setter-only bool renders as a button and is not saved.
        [SettingsUISection(kSection, kCacheGroup)]
        [SettingsUIConfirmation]
        public bool ClearCache { set { TileCache.RequestClear(); } }

        // Placement, edited from the in-game panel.
        [SettingsUIHidden]
        public double CenterLat { get; set; } = kDefaultLat;

        [SettingsUIHidden]
        public double CenterLon { get; set; } = kDefaultLon;

        [SettingsUIHidden]
        public double AnchorX { get; set; }

        [SettingsUIHidden]
        public double AnchorZ { get; set; }

        [SettingsUIHidden]
        public float Rotation
        {
            get => m_Rotation;
            set => m_Rotation = NormalizeAngle(value);
        }

        public bool IsWholeMap() => WholeMap;

        internal static float NormalizeAngle(float degrees)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees)) return 0f;
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            else if (degrees <= -180f) degrees += 360f;
            return degrees;
        }

        public override void SetDefaults()
        {
            Visible = true;
            Opacity = 50;
            OnTop = true;
            HeightOffset = 1f;
            Zoom = 15;
            AreaKm = 3;
            WholeMap = false;
            FollowCamera = true;
            Source = TileSource.OpenStreetMap;
            ScalePercent = 100f;
            CustomUrl = "";
            CenterLat = kDefaultLat;
            CenterLon = kDefaultLon;
            AnchorX = 0;
            AnchorZ = 0;
            Rotation = 0f;
        }
    }
}
