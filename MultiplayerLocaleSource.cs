using System.Collections.Generic;
using Colossal;

namespace MultiSkyLineII
{
    public sealed class MultiplayerLocaleSource : IDictionarySource
    {
        private readonly Dictionary<string, string> _entries = new Dictionary<string, string>();

        public MultiplayerLocaleSource(MultiplayerSettings settings)
        {
            _entries[settings.GetSettingsLocaleID()] = "MultiSkyLineII Multiplayer";

            _entries[settings.GetOptionGroupLocaleID("General")] = "General";
            _entries[settings.GetOptionGroupLocaleID("Host")] = "Host";
            _entries[settings.GetOptionGroupLocaleID("Client")] = "Client";

            AddOption(settings, nameof(MultiplayerSettings.NetworkEnabled), "Enable Network", "Enable or disable multiplayer networking.");
            AddOption(settings, nameof(MultiplayerSettings.HostMode), "Host Mode", "If enabled, this instance acts as server/host.");
            AddOption(settings, nameof(MultiplayerSettings.BindAddress), "Bind Address", "IP address used by the host listener.");
            AddOption(settings, nameof(MultiplayerSettings.ServerAddress), "Server Address", "Host IP address to connect to as client.");
            AddOption(settings, nameof(MultiplayerSettings.Port), "Port", "TCP port used by host and client.");
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
