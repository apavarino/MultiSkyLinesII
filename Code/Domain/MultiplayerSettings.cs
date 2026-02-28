using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using System;
using Game.UI.Widgets;

namespace MultiSkyLineII
{
    [FileLocation("MultiSkyLineII")]
    public sealed class MultiplayerSettings : ModSetting
    {
        [SettingsUISection("General")]
        public bool NetworkEnabled { get; set; }

        [SettingsUISection("General")]
        public bool HostMode { get; set; }

        [SettingsUISection("General")]
        [SettingsUIDropdown(typeof(MultiplayerSettings), nameof(GetLanguageOptions))]
        [SettingsUISetter(typeof(MultiplayerSettings), nameof(OnCurrentLocaleSet))]
        public string CurrentLocale { get; set; }

        [SettingsUISection("Host")]
        [SettingsUITextInput]
        public string BindAddress { get; set; }

        [SettingsUISection("Client")]
        [SettingsUITextInput]
        public string ServerAddress { get; set; }

        [SettingsUISection("General")]
        [SettingsUITextInput]
        public int Port { get; set; }

        [SettingsUISection("General")]
        [SettingsUITextInput]
        public string PlayerName { get; set; }

        public MultiplayerSettings(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        public override void SetDefaults()
        {
            NetworkEnabled = false;
            HostMode = true;
            CurrentLocale = "en-US";
            BindAddress = "0.0.0.0";
            ServerAddress = "127.0.0.1";
            Port = 25565;
            PlayerName = CreateRandomPlayerName();
        }

        public DropdownItem<string>[] GetLanguageOptions()
        {
            return new[]
            {
                new DropdownItem<string> { value = "en-US", displayName = "English" },
                new DropdownItem<string> { value = "fr-FR", displayName = "Francais" }
            };
        }

        public void OnCurrentLocaleSet(string value)
        {
            CurrentLocale = string.IsNullOrWhiteSpace(value) ? "en-US" : value;
            Mod.SetCurrentLocale(CurrentLocale);
        }

        public static string CreateRandomPlayerName()
        {
            return $"Player-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
        }
    }
}
