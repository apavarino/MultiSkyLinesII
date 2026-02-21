using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using System;
using System.Collections.Generic;
using System.Reflection;
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
        private string _cachedCityName = "Unknown City";
        private DateTime _nextCityNameRefreshUtc;

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
            if (_settings.Port < 1 || _settings.Port > 65535)
            {
                _settings.Port = 25565;
            }
            _network?.Restart(_settings);
        }

        private MultiplayerResourceState GetLocalState()
        {
            var state = new MultiplayerResourceState
            {
                Name = ResolveCityName(),
                PingMs = 0,
                TimestampUtc = DateTime.UtcNow
            };

            MultiplayerResourceReader.TryRead(ref state);
            return state;
        }

        private string ResolveCityName()
        {
            if (DateTime.UtcNow < _nextCityNameRefreshUtc)
                return _cachedCityName;

            _nextCityNameRefreshUtc = DateTime.UtcNow.AddSeconds(2);
            if (TryResolveCityName(out var cityName))
            {
                _cachedCityName = cityName;
            }

            return _cachedCityName;
        }

        private static bool TryResolveCityName(out string cityName)
        {
            cityName = null;
            var manager = GameManager.instance;
            if (manager == null)
                return false;

            if (TryReadCityNameFromObject(manager, out cityName))
                return true;

            var directNodes = new[]
            {
                "configuration", "gameSession", "session", "activeGame", "gameMode", "city", "cityData", "save", "saveGame"
            };

            for (var i = 0; i < directNodes.Length; i++)
            {
                var child = GetMemberValue(manager, directNodes[i]);
                if (child != null && TryReadCityNameFromObject(child, out cityName))
                    return true;
            }

            var visited = new HashSet<object>();
            var objects = new System.Collections.ArrayList { manager };
            var depths = new System.Collections.ArrayList { 0 };
            visited.Add(manager);

            for (var cursor = 0; cursor < objects.Count; cursor++)
            {
                var obj = objects[cursor];
                var depth = (int)depths[cursor];
                if (obj == null || depth > 2)
                    continue;

                if (TryReadCityNameFromObject(obj, out cityName))
                    return true;

                var type = obj.GetType();
                var members = type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (var i = 0; i < members.Length; i++)
                {
                    object child = null;
                    switch (members[i])
                    {
                        case PropertyInfo propertyInfo:
                            if (!propertyInfo.CanRead || propertyInfo.GetIndexParameters().Length > 0)
                                continue;
                            if (propertyInfo.PropertyType.IsPrimitive || propertyInfo.PropertyType.IsEnum || propertyInfo.PropertyType == typeof(string))
                                continue;
                            try { child = propertyInfo.GetValue(obj); } catch { }
                            break;
                        case FieldInfo fieldInfo:
                            if (fieldInfo.FieldType.IsPrimitive || fieldInfo.FieldType.IsEnum || fieldInfo.FieldType == typeof(string))
                                continue;
                            try { child = fieldInfo.GetValue(obj); } catch { }
                            break;
                    }

                    if (child == null || visited.Contains(child))
                        continue;

                    visited.Add(child);
                    objects.Add(child);
                    depths.Add(depth + 1);
                }
            }

            return false;
        }

        private static bool TryReadCityNameFromObject(object target, out string cityName)
        {
            cityName = null;
            if (target == null)
                return false;

            var directNames = new[]
            {
                "cityName", "CityName", "city_name", "saveName", "SaveName", "displayName", "DisplayName", "name", "Name"
            };

            for (var i = 0; i < directNames.Length; i++)
            {
                var value = GetMemberValue(target, directNames[i]) as string;
                if (IsValidCityName(value, directNames[i]))
                {
                    cityName = value.Trim();
                    return true;
                }
            }

            var type = target.GetType();
            var members = type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (var i = 0; i < members.Length; i++)
            {
                string name = null;
                string value = null;

                switch (members[i])
                {
                    case PropertyInfo propertyInfo:
                        if (propertyInfo.PropertyType != typeof(string) || !propertyInfo.CanRead || propertyInfo.GetIndexParameters().Length > 0)
                            continue;
                        name = propertyInfo.Name;
                        try { value = propertyInfo.GetValue(target) as string; } catch { }
                        break;
                    case FieldInfo fieldInfo:
                        if (fieldInfo.FieldType != typeof(string))
                            continue;
                        name = fieldInfo.Name;
                        try { value = fieldInfo.GetValue(target) as string; } catch { }
                        break;
                }

                if (IsValidCityName(value, name))
                {
                    cityName = value.Trim();
                    return true;
                }
            }

            return false;
        }

        private static object GetMemberValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName))
                return null;

            var type = target.GetType();
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

            var propertyInfo = type.GetProperty(memberName, Flags);
            if (propertyInfo != null && propertyInfo.CanRead && propertyInfo.GetIndexParameters().Length == 0)
            {
                try { return propertyInfo.GetValue(target); } catch { }
            }

            var fieldInfo = type.GetField(memberName, Flags);
            if (fieldInfo != null)
            {
                try { return fieldInfo.GetValue(target); } catch { }
            }

            return null;
        }

        private static bool IsValidCityName(string value, string sourceMember)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var trimmed = value.Trim();
            if (trimmed.Length < 2 || trimmed.Length > 80)
                return false;

            if (trimmed.Contains("\\") || trimmed.Contains("/") || trimmed.Contains(":"))
                return false;

            var member = sourceMember ?? string.Empty;
            var lowerMember = member.ToLowerInvariant();
            if (lowerMember.Contains("cityname") || lowerMember.Contains("city_name") || lowerMember.Contains("savename"))
                return true;

            if (lowerMember == "name" || lowerMember == "displayname")
            {
                return trimmed.IndexOf("city", StringComparison.OrdinalIgnoreCase) >= 0 || trimmed.Length >= 3;
            }

            return false;
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
