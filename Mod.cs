using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using System;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using UnityEngine;

namespace MultiSkyLineII
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(MultiSkyLineII)}.{nameof(Mod)}")
            .SetShowsErrorsInUI(false);
        public static string DisplayVersion { get; private set; } = "1.0.0";
        private MultiplayerSettings _settings;
        private MultiplayerNetworkService _network;
        private MultiplayerLocaleSource _localeSource;
        private string[] _registeredLocales;
        private MultiplayerHudOverlay _hudOverlay;
        private string _cachedPlayerName = "Unknown Player";
        private bool _isApplyingSettings;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            _settings = new MultiplayerSettings(this);
            _localeSource = new MultiplayerLocaleSource(_settings);
            RegisterLocalizationSource();
            _settings.RegisterInOptionsUI();
            MultiplayerSettingsStorage.Load(_settings, log);
            if (_settings.Port < 1 || _settings.Port > 65535)
            {
                _settings.Port = 25565;
            }
            if (string.IsNullOrWhiteSpace(_settings.PlayerName))
            {
                _settings.PlayerName = MultiplayerSettings.CreateRandomPlayerName();
            }
            MultiplayerSettingsStorage.Save(_settings, log);
            _settings.onSettingsApplied += OnSettingsApplied;

            _network = new MultiplayerNetworkService(log, GetLocalState);
            _network.Start(_settings);
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                DisplayVersion = ResolveDisplayVersion(asset.path);
                log.Info($"Current mod asset at {asset.path}");
                log.Info($"Resolved mod version for HUD: {DisplayVersion}");
            }
            else
            {
                DisplayVersion = ResolveAssemblyDisplayVersion();
            }

            CreateHudOverlay();
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
            if (_isApplyingSettings)
                return;

            _isApplyingSettings = true;
            log.Info("Settings applied, restarting multiplayer service.");
            try
            {
                if (_settings.Port < 1 || _settings.Port > 65535)
                {
                    _settings.Port = 25565;
                }

                if (string.IsNullOrWhiteSpace(_settings.PlayerName))
                {
                    _settings.PlayerName = MultiplayerSettings.CreateRandomPlayerName();
                }

                MultiplayerSettingsStorage.Save(_settings, log);
                _network?.Restart(_settings);
            }
            finally
            {
                _isApplyingSettings = false;
            }
        }

        private MultiplayerResourceState GetLocalState()
        {
            var state = new MultiplayerResourceState
            {
                Name = ResolvePlayerName(),
                PingMs = 0,
                TimestampUtc = DateTime.UtcNow
            };

            MultiplayerResourceReader.TryRead(ref state);
            return state;
        }

        private string ResolvePlayerName()
        {
            var configuredName = _settings?.PlayerName;
            if (!string.IsNullOrWhiteSpace(configuredName))
            {
                _cachedPlayerName = configuredName.Trim();
                return _cachedPlayerName;
            }

            _cachedPlayerName = MultiplayerSettings.CreateRandomPlayerName();
            if (_settings != null)
            {
                _settings.PlayerName = _cachedPlayerName;
            }
            return _cachedPlayerName;
        }

        private static string ResolveDisplayVersion(string assetPath)
        {
            try
            {
                var assemblyDir = Path.GetDirectoryName(assetPath);
                if (!string.IsNullOrWhiteSpace(assemblyDir))
                {
                    var parent1 = Directory.GetParent(assemblyDir)?.FullName;
                    var parent2 = Directory.GetParent(parent1 ?? string.Empty)?.FullName;
                    var candidates = new[]
                    {
                        Path.Combine(assemblyDir, "PublishConfiguration.xml"),
                        Path.Combine(assemblyDir, "Properties", "PublishConfiguration.xml"),
                        string.IsNullOrWhiteSpace(parent1) ? null : Path.Combine(parent1, "Properties", "PublishConfiguration.xml"),
                        string.IsNullOrWhiteSpace(parent2) ? null : Path.Combine(parent2, "Properties", "PublishConfiguration.xml")
                    };

                    for (var i = 0; i < candidates.Length; i++)
                    {
                        var file = candidates[i];
                        if (TryReadModVersionFromFile(file, out var version))
                            return version;
                    }
                }

                var localLowModsFile = Path.Combine(Application.persistentDataPath, "Mods", "MultiSkyLineII", "PublishConfiguration.xml");
                if (TryReadModVersionFromFile(localLowModsFile, out var deployedVersion))
                    return deployedVersion;
            }
            catch
            {
            }

            return ResolveAssemblyDisplayVersion();
        }

        private static bool TryReadModVersionFromFile(string file, out string version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                return false;

            try
            {
                var doc = XDocument.Load(file);
                var value = doc.Root?.Element("ModVersion")?.Attribute("Value")?.Value;
                if (string.IsNullOrWhiteSpace(value))
                    return false;

                version = value.Trim();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveAssemblyDisplayVersion()
        {
            var assembly = typeof(Mod).Assembly;
            var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVersion))
            {
                var plusIndex = infoVersion.IndexOf('+');
                return plusIndex > 0 ? infoVersion.Substring(0, plusIndex) : infoVersion;
            }

            return assembly.GetName().Version?.ToString() ?? "1.0.0";
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
