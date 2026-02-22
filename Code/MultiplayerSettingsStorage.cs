using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Colossal.Logging;
using UnityEngine;

namespace MultiSkyLineII
{
    public static class MultiplayerSettingsStorage
    {
        private static readonly string DirectoryPath = Path.Combine(Application.persistentDataPath, "MultiSkyLineII");
        private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.cfg");

        public static void Load(MultiplayerSettings settings, ILog log)
        {
            if (settings == null || !File.Exists(FilePath))
                return;

            try
            {
                var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var lines = File.ReadAllLines(FilePath);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    var sep = line.IndexOf('=');
                    if (sep <= 0)
                        continue;

                    var key = line.Substring(0, sep).Trim();
                    var value = line.Substring(sep + 1);
                    entries[key] = Uri.UnescapeDataString(value ?? string.Empty);
                }

                if (entries.TryGetValue(nameof(MultiplayerSettings.NetworkEnabled), out var networkEnabled) &&
                    bool.TryParse(networkEnabled, out var networkEnabledBool))
                {
                    settings.NetworkEnabled = networkEnabledBool;
                }

                if (entries.TryGetValue(nameof(MultiplayerSettings.HostMode), out var hostMode) &&
                    bool.TryParse(hostMode, out var hostModeBool))
                {
                    settings.HostMode = hostModeBool;
                }

                if (entries.TryGetValue(nameof(MultiplayerSettings.BindAddress), out var bindAddress))
                {
                    settings.BindAddress = bindAddress;
                }

                if (entries.TryGetValue(nameof(MultiplayerSettings.ServerAddress), out var serverAddress))
                {
                    settings.ServerAddress = serverAddress;
                }

                if (entries.TryGetValue(nameof(MultiplayerSettings.Port), out var portText) &&
                    int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                {
                    settings.Port = port;
                }

                if (entries.TryGetValue(nameof(MultiplayerSettings.PlayerName), out var playerName))
                {
                    settings.PlayerName = playerName;
                }
            }
            catch (Exception e)
            {
                log?.Warn($"Failed to load settings from disk: {e.Message}");
            }
        }

        public static void Save(MultiplayerSettings settings, ILog log)
        {
            if (settings == null)
                return;

            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var lines = new[]
                {
                    $"# MultiSkyLineII settings ({DateTime.UtcNow:O})",
                    $"{nameof(MultiplayerSettings.NetworkEnabled)}={Uri.EscapeDataString(settings.NetworkEnabled.ToString())}",
                    $"{nameof(MultiplayerSettings.HostMode)}={Uri.EscapeDataString(settings.HostMode.ToString())}",
                    $"{nameof(MultiplayerSettings.BindAddress)}={Uri.EscapeDataString(settings.BindAddress ?? string.Empty)}",
                    $"{nameof(MultiplayerSettings.ServerAddress)}={Uri.EscapeDataString(settings.ServerAddress ?? string.Empty)}",
                    $"{nameof(MultiplayerSettings.Port)}={Uri.EscapeDataString(settings.Port.ToString(CultureInfo.InvariantCulture))}",
                    $"{nameof(MultiplayerSettings.PlayerName)}={Uri.EscapeDataString(settings.PlayerName ?? string.Empty)}"
                };

                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception e)
            {
                log?.Warn($"Failed to save settings to disk: {e.Message}");
            }
        }
    }
}
