using System.Collections.Generic;
using Colossal;

namespace MultiSkyLineII
{
    public sealed class MultiplayerLocaleSource : IDictionarySource
    {
        private readonly Dictionary<string, string> _entries = new Dictionary<string, string>();

        public MultiplayerLocaleSource(MultiplayerSettings settings)
        {
            var locale = LocalizationCatalog.NormalizeLocale(settings?.CurrentLocale);
            string T(string key, string fallback) => LocalizationCatalog.GetText(locale, key, fallback);

            _entries[settings.GetSettingsLocaleID()] = T("settings.title", "MultiSkyLineII Multiplayer");

            _entries[settings.GetOptionGroupLocaleID("General")] = T("group.general", "General");
            _entries[settings.GetOptionGroupLocaleID("Host")] = T("group.host", "Host");
            _entries[settings.GetOptionGroupLocaleID("Client")] = T("group.client", "Client");

            AddOption(settings, nameof(MultiplayerSettings.NetworkEnabled),
                T("option.network_enabled.label", "Enable Network"),
                T("option.network_enabled.desc", "Enable or disable multiplayer networking."));
            AddOption(settings, nameof(MultiplayerSettings.HostMode),
                T("option.host_mode.label", "Host Mode"),
                T("option.host_mode.desc", "If enabled, this instance acts as server/host."));
            AddOption(settings, nameof(MultiplayerSettings.CurrentLocale),
                T("option.language.label", "Language"),
                T("option.language.desc", "UI language for the mod."));
            AddOption(settings, nameof(MultiplayerSettings.BindAddress),
                T("option.bind_address.label", "Bind Address"),
                T("option.bind_address.desc", "IP address used by the host listener."));
            AddOption(settings, nameof(MultiplayerSettings.ServerAddress),
                T("option.server_address.label", "Server Address"),
                T("option.server_address.desc", "Host IP address to connect to as client."));
            AddOption(settings, nameof(MultiplayerSettings.Port),
                T("option.port.label", "Port"),
                T("option.port.desc", "TCP port used by host and client."));
            AddOption(settings, nameof(MultiplayerSettings.PlayerName),
                T("option.player_name.label", "Player Name"),
                T("option.player_name.desc", "Displayed multiplayer name."));

            _entries["Assets.NAME[BuildingPrefab:MS2 Exchange Hub]"] = T("asset.exchange_hub.name", "MS2 Exchange Hub");
            _entries["Assets.DESCRIPTION[BuildingPrefab:MS2 Exchange Hub]"] = T("asset.exchange_hub.desc", "Energy exchange hub used by MultiSkyLineII contracts.");
            _entries["Assets.NAME[MS2 Exchange Hub]"] = T("asset.exchange_hub.name", "MS2 Exchange Hub");
            _entries["Assets.DESCRIPTION[MS2 Exchange Hub]"] = T("asset.exchange_hub.desc", "Energy exchange hub used by MultiSkyLineII contracts.");
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return _entries;
        }

        public void Unload()
        {
        }

        private void AddOption(MultiplayerSettings settings, string propertyName, string label, string description)
        {
            _entries[settings.GetOptionLabelLocaleID(propertyName)] = label;
            _entries[settings.GetOptionDescLocaleID(propertyName)] = description;
        }
    }
}
