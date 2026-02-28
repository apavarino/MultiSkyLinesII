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
        private const string ModFolderName = "MultiSkyLinesII";
        private const string LegacyModFolderName = "MultiSkyLineII";
        public static string DisplayVersion { get; private set; } = "1.0.0";
        public static string CurrentLocale { get; private set; } = "en-US";

        public static void SetCurrentLocale(string locale)
        {
            CurrentLocale = LocalizationCatalog.NormalizeLocale(locale);
        }
        private MultiplayerSettings _settings;
        private MultiplayerNetworkService _network;
        private MultiplayerLocaleSource _localeSource;
        private string[] _registeredLocales;
        private MultiplayerNativeUiBridge _nativeUiBridge;
        private string _cachedPlayerName = "Unknown Player";
        private bool _isApplyingSettings;

        public void OnLoad(UpdateSystem updateSystem)
        {
            ModDiagnostics.ResetForNewSession();
            ModDiagnostics.Write("OnLoad start.");
            ModDiagnostics.Write($"Diagnostics file: {ModDiagnostics.LogFilePath}");
            updateSystem.UpdateAt<ExchangeHubPrefabBootstrapSystem>(SystemUpdatePhase.PrefabUpdate);
            ModDiagnostics.Write("Registered ExchangeHubPrefabBootstrapSystem at PrefabUpdate.");
            updateSystem.UpdateAt<BuildingPlacementDiagnosticsSystem>(SystemUpdatePhase.GameSimulation);
            ModDiagnostics.Write("Registered BuildingPlacementDiagnosticsSystem at GameSimulation.");
            updateSystem.UpdateAt<NativeUiBootstrapSystem>(SystemUpdatePhase.UIUpdate);
            ModDiagnostics.Write("Registered NativeUiBootstrapSystem at UIUpdate.");
            ModDiagnostics.Info(nameof(OnLoad));

            _settings = new MultiplayerSettings(this);
            MultiplayerSettingsStorage.Load(_settings);
            if (_settings.Port < 1 || _settings.Port > 65535)
            {
                _settings.Port = 25565;
            }
            if (string.IsNullOrWhiteSpace(_settings.PlayerName))
            {
                _settings.PlayerName = MultiplayerSettings.CreateRandomPlayerName();
            }
            if (!string.Equals(_settings.CurrentLocale, "en-US", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_settings.CurrentLocale, "fr-FR", StringComparison.OrdinalIgnoreCase))
            {
                _settings.CurrentLocale = "en-US";
            }
            RefreshLocalizationSource();
            _settings.RegisterInOptionsUI();
            SetCurrentLocale(_settings.CurrentLocale);
            ModDiagnostics.Write($"Settings loaded: locale={CurrentLocale}");
            MultiplayerSettingsStorage.Save(_settings);
            _settings.onSettingsApplied += OnSettingsApplied;

            _network = new MultiplayerNetworkService(GetLocalState);
            _network.Start(_settings);
            ModDiagnostics.Write("Network service started.");
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                DisplayVersion = ResolveDisplayVersion(asset.path);
                ModDiagnostics.Info($"Current mod asset at {asset.path}");
                ModDiagnostics.Info($"Resolved mod version for HUD: {DisplayVersion}");
                ModDiagnostics.Write($"Asset path: {asset.path}");
                ModDiagnostics.Write($"DisplayVersion: {DisplayVersion}");
            }
            else
            {
                DisplayVersion = ResolveAssemblyDisplayVersion();
                ModDiagnostics.Write($"DisplayVersion from assembly: {DisplayVersion}");
            }

            try
            {
                var modsRoot = Path.Combine(Application.persistentDataPath, "Mods");
                var deployedDir = Path.Combine(modsRoot, ModFolderName);
                var legacyDir = Path.Combine(modsRoot, LegacyModFolderName);
                ModDiagnostics.Write($"UI file check: {Path.Combine(deployedDir, "MultiSkyLinesII.mjs")} exists={File.Exists(Path.Combine(deployedDir, "MultiSkyLinesII.mjs"))}");
                ModDiagnostics.Write($"UI file check: {Path.Combine(deployedDir, "mod.json")} exists={File.Exists(Path.Combine(deployedDir, "mod.json"))}");
                ModDiagnostics.Write($"UI file check (legacy): {Path.Combine(legacyDir, "MultiSkyLineII.mjs")} exists={File.Exists(Path.Combine(legacyDir, "MultiSkyLineII.mjs"))}");
            }
            catch (Exception e)
            {
                ModDiagnostics.Write($"UI file check failed: {e}");
            }

            ModDiagnostics.Write("HUD overlay creation skipped (native CS2 UI mode).");
            CreateNativeUiBridge();
            ModDiagnostics.Write("OnLoad complete.");
        }

        public void OnDispose()
        {
            ModDiagnostics.Write("OnDispose start.");
            if (_settings != null)
            {
                _settings.onSettingsApplied -= OnSettingsApplied;
                _settings.UnregisterInOptionsUI();
            }

            _network?.Dispose();
            _network = null;
            DestroyNativeUiBridge();
            UnregisterLocalizationSource();
            _localeSource = null;
            _settings = null;

            ModDiagnostics.Info(nameof(OnDispose));
            ModDiagnostics.Write("OnDispose complete.");
        }

        private void OnSettingsApplied(Setting _)
        {
            if (_isApplyingSettings)
                return;

            _isApplyingSettings = true;
            ModDiagnostics.Info("Settings applied, restarting multiplayer service.");
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
                if (!string.Equals(_settings.CurrentLocale, "en-US", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(_settings.CurrentLocale, "fr-FR", StringComparison.OrdinalIgnoreCase))
                {
                    _settings.CurrentLocale = "en-US";
                }

                SetCurrentLocale(_settings.CurrentLocale);
                ModDiagnostics.Write($"Settings applied: locale={CurrentLocale}");
                RefreshLocalizationSource();
                MultiplayerSettingsStorage.Save(_settings);
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

                var localLowModsFile = Path.Combine(Application.persistentDataPath, "Mods", ModFolderName, "PublishConfiguration.xml");
                if (TryReadModVersionFromFile(localLowModsFile, out var deployedVersion))
                    return deployedVersion;

                var legacyLocalLowModsFile = Path.Combine(Application.persistentDataPath, "Mods", LegacyModFolderName, "PublishConfiguration.xml");
                if (TryReadModVersionFromFile(legacyLocalLowModsFile, out deployedVersion))
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
                ModDiagnostics.Write($"Localization source registered. Selected locale={_settings?.CurrentLocale ?? "en-US"}");
            }
            catch (Exception e)
            {
                ModDiagnostics.Warn($"Failed to register localization source: {e.Message}");
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
                ModDiagnostics.Warn($"Failed to unregister localization source: {e.Message}");
            }
            finally
            {
                _registeredLocales = null;
            }
        }

        private void RefreshLocalizationSource()
        {
            UnregisterLocalizationSource();
            LocalizationCatalog.Invalidate();
            _localeSource = new MultiplayerLocaleSource(_settings);
            RegisterLocalizationSource();
        }

        private void CreateNativeUiBridge()
        {
            try
            {
                var go = new GameObject("MultiSkyLineII_NativeUIBridge");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _nativeUiBridge = go.AddComponent<MultiplayerNativeUiBridge>();
                _nativeUiBridge.Initialize(_network);
                ModDiagnostics.Info("Native UI bridge initialized.");
                ModDiagnostics.Write("Native UI bridge created and initialized.");
            }
            catch (Exception e)
            {
                ModDiagnostics.Warn($"Failed to create native UI bridge: {e.Message}");
                ModDiagnostics.Write($"Native UI bridge creation failed: {e}");
            }
        }

        private void DestroyNativeUiBridge()
        {
            if (_nativeUiBridge == null)
                return;

            try
            {
                var go = _nativeUiBridge.gameObject;
                _nativeUiBridge = null;
                UnityEngine.Object.Destroy(go);
                ModDiagnostics.Write("Native UI bridge destroyed.");
            }
            catch (Exception e)
            {
                ModDiagnostics.Warn($"Failed to destroy native UI bridge: {e.Message}");
                ModDiagnostics.Write($"Native UI bridge destroy failed: {e}");
            }
        }

    }
}
