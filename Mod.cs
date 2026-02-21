using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using System;
using UnityEngine;

namespace MultiSkyLineII
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(MultiSkyLineII)}.{nameof(Mod)}")
            .SetShowsErrorsInUI(false);
        private MultiplayerSettings _settings;
        private MultiplayerNetworkService _network;
        private MultiplayerLocaleSource _localeSource;
        private string[] _registeredLocales;
        private MultiplayerHudOverlay _hudOverlay;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            _settings = new MultiplayerSettings(this);
            _localeSource = new MultiplayerLocaleSource(_settings);
            RegisterLocalizationSource();
            _settings.RegisterInOptionsUI();
            _settings.onSettingsApplied += OnSettingsApplied;

            _network = new MultiplayerNetworkService(log, GetLocalState);
            _network.Start(_settings);
            CreateHudOverlay();

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");
        }

        public void OnDispose()
        {
            if (_settings != null)
            {
                _settings.onSettingsApplied -= OnSettingsApplied;
                _settings.UnregisterInOptionsUI();
            }

            _network?.Dispose();
            _network = null;
            DestroyHudOverlay();
            UnregisterLocalizationSource();
            _localeSource = null;
            _settings = null;

            log.Info(nameof(OnDispose));
        }

        private void OnSettingsApplied(Setting _)
        {
            log.Info("Settings applied, restarting multiplayer service.");
            if (string.IsNullOrWhiteSpace(_settings.CityName))
            {
                _settings.CityName = "My City";
            }
            _network?.Restart(_settings);
        }

        private MultiplayerResourceState GetLocalState()
        {
            var state = new MultiplayerResourceState
            {
                Name = string.IsNullOrWhiteSpace(_settings?.CityName) ? "My City" : _settings.CityName.Trim(),
                PingMs = 0,
                TimestampUtc = DateTime.UtcNow
            };

            MultiplayerResourceReader.TryRead(ref state);
            return state;
        }

        private void RegisterLocalizationSource()
        {
            try
            {
                var localizationManager = typeof(GameManager).GetProperty("localizationManager")?.GetValue(GameManager.instance);
                if (localizationManager == null || _localeSource == null)
                    return;

                var managerType = localizationManager.GetType();
                var getSupportedLocales = managerType.GetMethod("GetSupportedLocales", Type.EmptyTypes);
                var addSource = managerType.GetMethod("AddSource", new[] { typeof(string), typeof(Colossal.IDictionarySource) });
                if (getSupportedLocales == null || addSource == null)
                    return;

                _registeredLocales = getSupportedLocales.Invoke(localizationManager, null) as string[];
                if (_registeredLocales == null || _registeredLocales.Length == 0)
                {
                    _registeredLocales = new[] { "en-US" };
                }

                foreach (var locale in _registeredLocales)
                {
                    addSource.Invoke(localizationManager, new object[] { locale, _localeSource });
                }
            }
            catch (Exception e)
            {
                log.Warn($"Failed to register localization source: {e.Message}");
            }
        }

        private void UnregisterLocalizationSource()
        {
            try
            {
                var localizationManager = typeof(GameManager).GetProperty("localizationManager")?.GetValue(GameManager.instance);
                if (localizationManager == null || _localeSource == null || _registeredLocales == null)
                    return;

                var removeSource = localizationManager.GetType().GetMethod("RemoveSource", new[] { typeof(string), typeof(Colossal.IDictionarySource) });
                if (removeSource == null)
                    return;

                foreach (var locale in _registeredLocales)
                {
                    removeSource.Invoke(localizationManager, new object[] { locale, _localeSource });
                }
            }
            catch (Exception e)
            {
                log.Warn($"Failed to unregister localization source: {e.Message}");
            }
            finally
            {
                _registeredLocales = null;
            }
        }

        private void CreateHudOverlay()
        {
            try
            {
                var go = new GameObject("MultiSkyLineII_HUD");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _hudOverlay = go.AddComponent<MultiplayerHudOverlay>();
                _hudOverlay.Initialize(_network);
            }
            catch (Exception e)
            {
                log.Warn($"Failed to create HUD overlay: {e.Message}");
            }
        }

        private void DestroyHudOverlay()
        {
            if (_hudOverlay == null)
                return;

            try
            {
                var go = _hudOverlay.gameObject;
                _hudOverlay = null;
                UnityEngine.Object.Destroy(go);
            }
            catch (Exception e)
            {
                log.Warn($"Failed to destroy HUD overlay: {e.Message}");
            }
        }
    }
}
