using System;
using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Localization;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using UnityEngine;

namespace MapOverlay
{
    public class Mod : IMod
    {
        public const string kVersion = "0.3.0";

        public static ILog log = LogManager.GetLogger(nameof(MapOverlay)).SetShowsErrorsInUI(false);

        // The game creates IMod instances without running constructors or field
        // initializers, so every field is set up in OnLoad.
        private Setting m_Setting;
        internal static Setting Setting { get; private set; }
        internal static OverlayController Controller { get; private set; }
        private LocalizationManager m_Localization;
        private List<KeyValuePair<string, Locale>> m_Locales;
        private GameObject m_Host;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));
            try
            {
                if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                    log.Info($"Current mod asset at {asset.path}");

                m_Setting = new Setting(this);
                AssetDatabase.global.LoadSettings(nameof(MapOverlay), m_Setting, new Setting(this));
                m_Setting.RegisterInOptionsUI();
                Setting = m_Setting;

                m_Localization = GameManager.instance.localizationManager;
                m_Locales = new List<KeyValuePair<string, Locale>>();
                foreach (string id in m_Localization.GetSupportedLocales())
                    AddLocale(id);

                // Rendering and downloads run on a main-thread Unity callback, which also runs while paused.
                m_Host = new GameObject(nameof(MapOverlay)) { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(m_Host);
                Controller = m_Host.AddComponent<OverlayController>();
                Controller.Initialize(m_Setting);

                // In-game panel bindings (top-left button, see UI/src).
                updateSystem.UpdateAt<MapOverlayUISystem>(SystemUpdatePhase.UIUpdate);
                updateSystem.UpdateAt<MapDragTool>(SystemUpdatePhase.ToolUpdate);
                // Road definitions must exist before the game's Modification phases read them.
                updateSystem.UpdateAt<RoadPlacementSystem>(SystemUpdatePhase.ToolUpdate);

                log.Info($"Loaded {kVersion}. Zoom={m_Setting.Zoom} Area={m_Setting.AreaKm} km WholeMap={m_Setting.WholeMap} " +
                    $"Center={m_Setting.CenterLat:F6},{m_Setting.CenterLon:F6} Anchor={m_Setting.AnchorX:F0},{m_Setting.AnchorZ:F0}");
            }
            catch (Exception ex)
            {
                // Never let a failure here stop the game from loading other mods.
                log.Error(ex, "Could not initialize. The mod is disabled for this session.");
                OnDispose();
            }
        }

        private void AddLocale(string id)
        {
            var source = new Locale(m_Setting);
            m_Locales.Add(new KeyValuePair<string, Locale>(id, source));
            m_Localization.AddSource(id, source);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            var controller = Controller;
            var host = m_Host;
            var setting = m_Setting;
            var localization = m_Localization;
            var locales = m_Locales;
            Controller = null;
            m_Host = null;
            m_Setting = null;
            Setting = null;
            m_Localization = null;
            m_Locales = null;

            if (controller != null) Cleanup(controller.Shutdown, "controller");
            if (host != null) Cleanup(() => UnityEngine.Object.Destroy(host), "host object");
            if (setting != null) Cleanup(setting.UnregisterInOptionsUI, "settings");
            if (localization != null && locales != null)
                foreach (var pair in locales)
                    Cleanup(() => localization.RemoveSource(pair.Key, pair.Value), "locale " + pair.Key);
        }

        private static void Cleanup(Action action, string what)
        {
            try { action(); }
            catch (Exception ex) { log.Error(ex, "Could not clean up " + what + "."); }
        }
    }
}
