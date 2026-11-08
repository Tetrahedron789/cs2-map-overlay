using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace MapOverlay
{
    // One city the player works on: where its map sits and how it is shown.
    internal sealed class Project
    {
        public string Id = "";
        public string Name = "";
        public DateTime Saved;
        public double CenterLat, CenterLon, AnchorX, AnchorZ;
        public float Rotation;
        public float ScalePercent = 100f;
        public int Zoom = 15;
        public int AreaKm = 3;
        public bool WholeMap;
        public bool FollowCamera = true;
        public int Source;
        public string CustomUrl = "";
        public int Opacity = 50;
        public bool OnTop = true;
        public bool Locked;
        public DateTime? TerrainGenerated;

        public void Capture(Setting s)
        {
            CenterLat = s.CenterLat;
            CenterLon = s.CenterLon;
            AnchorX = s.AnchorX;
            AnchorZ = s.AnchorZ;
            Rotation = s.Rotation;
            ScalePercent = s.ScalePercent;
            Zoom = s.Zoom;
            AreaKm = s.AreaKm;
            WholeMap = s.WholeMap;
            FollowCamera = s.FollowCamera;
            Source = (int)s.Source;
            CustomUrl = s.CustomUrl;
            Opacity = s.Opacity;
            OnTop = s.OnTop;
        }

        public void Apply(Setting s)
        {
            s.CenterLat = CenterLat;
            s.CenterLon = CenterLon;
            s.AnchorX = AnchorX;
            s.AnchorZ = AnchorZ;
            s.Rotation = Rotation;
            s.ScalePercent = ScalePercent;
            s.Zoom = Zoom;
            s.AreaKm = AreaKm;
            s.WholeMap = WholeMap;
            s.FollowCamera = FollowCamera;
            s.Source = (TileSource)Source;
            s.CustomUrl = CustomUrl ?? "";
            s.Opacity = Opacity;
            s.OnTop = OnTop;
        }

        public bool Matches(Setting s) =>
            CenterLat == s.CenterLat && CenterLon == s.CenterLon && AnchorX == s.AnchorX && AnchorZ == s.AnchorZ
            && Rotation == s.Rotation && ScalePercent == s.ScalePercent && Zoom == s.Zoom && AreaKm == s.AreaKm
            && WholeMap == s.WholeMap && FollowCamera == s.FollowCamera && Source == (int)s.Source
            && (CustomUrl ?? "") == s.CustomUrl && Opacity == s.Opacity && OnTop == s.OnTop;
    }

    // Projects live as one JSON file each, outside the tile cache so clearing it keeps them.
    // None is active when a map loads: the player picks one, so an empty map starts empty.
    internal sealed class ProjectStore
    {
        private const int kMaxName = 40;

        private readonly Setting m_Setting;
        private readonly string m_Folder;
        private readonly List<Project> m_Projects = new List<Project>();
        private string m_Json;

        public Project Active { get; private set; }
        public bool Locked => Active != null && Active.Locked;
        public bool Dirty => Active != null && !Active.Matches(m_Setting);

        // For the panel; rebuilt only after a change.
        public string Json
        {
            get
            {
                if (m_Json != null) return m_Json;
                var rows = new List<object>();
                foreach (var p in m_Projects)
                    rows.Add(new
                    {
                        id = p.Id,
                        name = p.Name,
                        lat = p.CenterLat,
                        lon = p.CenterLon,
                        locked = p.Locked,
                        terrain = FormatDate(p.TerrainGenerated),
                    });
                m_Json = JsonConvert.SerializeObject(rows);
                return m_Json;
            }
        }

        public string ActiveTerrainDate => FormatDate(Active?.TerrainGenerated);

        public ProjectStore(Setting setting)
        {
            m_Setting = setting;
            m_Folder = Path.Combine(Application.persistentDataPath, "ModsData", nameof(MapOverlay), "projects");
            Load();
        }

        private static string FormatDate(DateTime? date) =>
            date.HasValue ? date.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

        private void Load()
        {
            if (!Directory.Exists(m_Folder)) return;
            foreach (string path in Directory.GetFiles(m_Folder, "*.json"))
            {
                try
                {
                    var project = JsonConvert.DeserializeObject<Project>(File.ReadAllText(path));
                    if (project == null || string.IsNullOrEmpty(project.Id)) throw new InvalidDataException("no project id");
                    m_Projects.Add(project);
                }
                catch (Exception ex)
                {
                    // The file stays where it is: the player may want it back.
                    Mod.log.Warn($"Projects: could not read {path} ({ex.Message}).");
                }
            }
            Sort();
        }

        private void Sort() => m_Projects.Sort((a, b) => b.Saved.CompareTo(a.Saved));

        private void Write(Project project)
        {
            try
            {
                Directory.CreateDirectory(m_Folder);
                string path = Path.Combine(m_Folder, project.Id + ".json");
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(project, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, $"Projects: could not save \"{project.Name}\".");
            }
        }

        private void Changed()
        {
            Sort();
            m_Json = null;
        }

        private string UniqueName(string wanted)
        {
            string name = wanted;
            for (int i = 2; m_Projects.Exists(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
                name = $"{wanted} ({i})";
            return name;
        }

        // A new project starts unturned, at 1:1, centred on the map, showing the last place until
        // the player searches for its city.
        public void Create(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return;
            if (name.Length > kMaxName) name = name.Substring(0, kMaxName).Trim();
            Close();
            m_Setting.Rotation = 0f;
            m_Setting.ScalePercent = 100f;
            m_Setting.AnchorX = 0;
            m_Setting.AnchorZ = 0;
            m_Setting.Visible = true;
            m_Setting.ApplyAndSave();
            var project = new Project { Id = Guid.NewGuid().ToString("N"), Name = UniqueName(name) };
            m_Projects.Add(project);
            Active = project;
            Save();
        }

        public void Open(string id)
        {
            var project = m_Projects.Find(p => p.Id == id);
            if (project == null || project == Active) return;
            Close();
            project.Apply(m_Setting);
            m_Setting.Visible = true;
            m_Setting.ApplyAndSave();
            Active = project;
            Mod.log.Info($"Project: opened \"{project.Name}\".");
        }

        public void Save()
        {
            if (Active == null) return;
            Active.Capture(m_Setting);
            Active.Saved = DateTime.UtcNow;
            Write(Active);
            Changed();
        }

        // Keeps what changed: leaving a project never loses its placement.
        public void Close()
        {
            if (Dirty) Save();
            Active = null;
        }

        public void Delete(string id)
        {
            var project = m_Projects.Find(p => p.Id == id);
            if (project == null) return;
            if (project == Active) Active = null;
            m_Projects.Remove(project);
            try
            {
                string path = Path.Combine(m_Folder, project.Id + ".json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, $"Projects: could not delete \"{project.Name}\".");
            }
            Changed();
        }

        public void SetLocked(bool locked)
        {
            if (Active == null || Active.Locked == locked) return;
            Active.Locked = locked;
            Save();
        }

        public void MarkTerrainGenerated()
        {
            if (Active == null) return;
            Active.TerrainGenerated = DateTime.UtcNow;
            Save();
        }
    }
}
