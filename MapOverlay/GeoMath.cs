using System;

namespace MapOverlay
{
    // Geographic helpers. Tiles use Web Mercator (EPSG:3857); positions in the game world use a
    // local east/north plane in true metres around the map centre, so one game unit is one
    // real metre wherever the map is.
    internal static class GeoMath
    {
        public const double MercatorRadius = 6378137.0;
        public const double MaxLatitude = 85.05112878;
        public const int TileSize = 256;

        private const double kDeg2Rad = Math.PI / 180.0;
        private const double kRad2Deg = 180.0 / Math.PI;

        // WGS84 ellipsoid, used for the metre-per-degree scale at the map centre.
        private const double kSemiMajor = 6378137.0;
        private const double kEccentricity2 = 0.00669437999014;

        public static double ClampLatitude(double lat) => Math.Max(-MaxLatitude, Math.Min(MaxLatitude, lat));

        public static double WrapLongitude(double lon)
        {
            lon = (lon + 180.0) % 360.0;
            if (lon < 0) lon += 360.0;
            return lon - 180.0;
        }

        // Fractional tile coordinates of a point at the given zoom level.
        public static double TileX(double lon, int zoom) => (lon + 180.0) / 360.0 * (1 << zoom);

        public static double TileY(double lat, int zoom)
        {
            double rad = ClampLatitude(lat) * kDeg2Rad;
            return (1.0 - Math.Log(Math.Tan(rad) + 1.0 / Math.Cos(rad)) / Math.PI) / 2.0 * (1 << zoom);
        }

        public static double TileToLon(double x, int zoom) => x / (1 << zoom) * 360.0 - 180.0;

        public static double TileToLat(double y, int zoom)
        {
            double n = Math.PI - 2.0 * Math.PI * y / (1 << zoom);
            return Math.Atan(Math.Sinh(n)) * kRad2Deg;
        }

        // Ground size of one tile edge near the given latitude.
        public static double TileMetres(double lat, int zoom) =>
            2.0 * Math.PI * MercatorRadius * Math.Cos(ClampLatitude(lat) * kDeg2Rad) / (1 << zoom);

        // Metres per degree of latitude and longitude at the given latitude.
        public static void MetresPerDegree(double lat, out double perLat, out double perLon)
        {
            double s = Math.Sin(lat * kDeg2Rad);
            double w = 1.0 - kEccentricity2 * s * s;
            double meridian = kSemiMajor * (1.0 - kEccentricity2) / (w * Math.Sqrt(w));
            double normal = kSemiMajor / Math.Sqrt(w);
            perLat = meridian * kDeg2Rad;
            perLon = normal * Math.Cos(lat * kDeg2Rad) * kDeg2Rad;
        }
    }

    // Places a map centred on (CenterLat, CenterLon) at world point (AnchorX, AnchorZ), turned
    // clockwise by Rotation degrees and scaled by Scale (game metres per real metre, 1 = 1:1).
    // +X is east and +Z is north before rotating.
    internal readonly struct MapFrame
    {
        public readonly double CenterLat;
        public readonly double CenterLon;
        public readonly double AnchorX;
        public readonly double AnchorZ;
        private readonly double m_PerLat;
        private readonly double m_PerLon;
        private readonly double m_Cos;
        private readonly double m_Sin;
        public readonly double Scale;

        public MapFrame(double lat, double lon, double anchorX, double anchorZ, double rotationDeg, double scale)
        {
            Scale = scale > 0 ? scale : 1.0;
            CenterLat = lat;
            CenterLon = lon;
            AnchorX = anchorX;
            AnchorZ = anchorZ;
            GeoMath.MetresPerDegree(lat, out m_PerLat, out m_PerLon);
            double r = rotationDeg * Math.PI / 180.0;
            m_Cos = Math.Cos(r);
            m_Sin = Math.Sin(r);
        }

        public void ToWorld(double lat, double lon, out double x, out double z)
        {
            double dLon = lon - CenterLon;
            if (dLon > 180.0) dLon -= 360.0;
            else if (dLon < -180.0) dLon += 360.0;
            double east = dLon * m_PerLon * Scale;
            double north = (lat - CenterLat) * m_PerLat * Scale;
            x = AnchorX + east * m_Cos + north * m_Sin;
            z = AnchorZ - east * m_Sin + north * m_Cos;
        }

        public void ToGeo(double x, double z, out double lat, out double lon)
        {
            double dx = (x - AnchorX) / Scale;
            double dz = (z - AnchorZ) / Scale;
            double east = dx * m_Cos - dz * m_Sin;
            double north = dx * m_Sin + dz * m_Cos;
            lat = GeoMath.ClampLatitude(CenterLat + north / m_PerLat);
            lon = GeoMath.WrapLongitude(CenterLon + east / m_PerLon);
        }
    }
}
