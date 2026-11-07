using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace MapOverlay
{
    public enum TileSource
    {
        OpenStreetMap,
        OpenTopoMap,
        Custom,
    }

    // Tile servers and the on-disk tile cache.
    internal static class TileSources
    {
        // Tile servers ask apps to identify themselves.
        public const string UserAgent = "CS2-MapOverlay/" + Mod.kVersion + " (Cities: Skylines II mod)";

        public const string OsmTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
        public const string OsmAttribution = "© OpenStreetMap contributors";

        // Contour lines and hill shading: handy to line the map up with a heightmap.
        public const string TopoTemplate = "https://tile.opentopomap.org/{z}/{x}/{y}.png";
        public const string TopoAttribution = "© OpenStreetMap contributors, SRTM · Style © OpenTopoMap (CC-BY-SA)";

        // Template in use, or null when the custom one is not valid.
        public static string Template(Setting setting)
        {
            switch (setting.Source)
            {
                case TileSource.OpenTopoMap: return TopoTemplate;
                case TileSource.Custom: return IsValidTemplate(setting.CustomUrl) ? setting.CustomUrl.Trim() : null;
                default: return OsmTemplate;
            }
        }

        // Empty for a custom server: the panel shows a reminder to credit it instead.
        public static string Attribution(Setting setting)
        {
            switch (setting.Source)
            {
                case TileSource.OpenTopoMap: return TopoAttribution;
                case TileSource.Custom: return "";
                default: return OsmAttribution;
            }
        }

        public static bool IsValidTemplate(string template)
        {
            if (string.IsNullOrWhiteSpace(template)) return false;
            template = template.Trim();
            return template.Length < 2048
                && (template.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || template.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                && template.Contains("{z}") && template.Contains("{x}") && template.Contains("{y}");
        }

        public static string Url(string template, int z, int x, int y) =>
            template
                .Replace("{z}", z.ToString())
                .Replace("{x}", x.ToString())
                .Replace("{y}", y.ToString());

        // Folder name for a template, so switching servers never mixes tiles. A key in the
        // URL ends up hashed, never written to disk as text.
        public static string CacheKey(string template)
        {
            uint hash = 2166136261;
            foreach (char c in template)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash.ToString("x8");
        }
    }

    internal static class TileCache
    {
        // Tiles older than this are checked again with the server; the old file is used if that fails.
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

        // Tiles are saved with this extension, not .png: the game indexes every image under its
        // data folder and would offer the tiles in the asset editor's image pickers.
        private const string kExtension = ".tile";

        private static volatile bool s_ClearRequested;

        private static string ModData => Path.Combine(Application.persistentDataPath, "ModsData", nameof(MapOverlay));

        public static string Root => Path.Combine(ModData, "cache");

        // Version 0.1 kept .png files here.
        private static string LegacyRoot => Path.Combine(ModData, "tiles");

        public static string PathFor(string cacheKey, int z, int x, int y) =>
            Path.Combine(Root, cacheKey, z.ToString(), x.ToString(), y + kExtension);

        public static void RequestClear() => s_ClearRequested = true;

        public static bool ConsumeClearRequest()
        {
            if (!s_ClearRequested) return false;
            s_ClearRequested = false;
            return true;
        }

        public static void ClearDisk()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
                Mod.log.Info("Tile cache cleared.");
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, "Could not clear the tile cache.");
            }
        }

        // Deletes the .png cache of earlier versions in the background.
        public static void RemoveLegacyCache()
        {
            string legacy = LegacyRoot;
            if (!Directory.Exists(legacy)) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.Delete(legacy, true);
                    Mod.log.Info("Removed the old .png tile cache.");
                }
                catch (Exception ex)
                {
                    Mod.log.Warn($"Could not remove the old tile cache {legacy}: {ex.Message}");
                }
            });
        }

        public static bool TryRead(string path, out byte[] bytes, out bool stale)
        {
            bytes = null;
            stale = false;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0) return false;
                stale = DateTime.UtcNow - info.LastWriteTimeUtc > MaxAge;
                bytes = File.ReadAllBytes(path);
                return true;
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Could not read cached tile {path}: {ex.Message}");
                return false;
            }
        }

        public static DateTime LastWrite(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        // The server said the tile has not changed: it counts as fresh again.
        public static void Touch(string path)
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (Exception ex) { Mod.log.Warn($"Could not touch cached tile {path}: {ex.Message}"); }
        }

        public static void Write(string path, byte[] bytes)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Could not cache tile {path}: {ex.Message}");
            }
        }
    }
}
