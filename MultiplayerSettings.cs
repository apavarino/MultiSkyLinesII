using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace MultiSkyLineII
{
    [FileLocation("MultiSkyLineII")]
    public sealed class MultiplayerSettings : ModSetting
    {
        [SettingsUISection("General")]
        public bool NetworkEnabled { get; set; }

        [SettingsUISection("General")]
        public bool HostMode { get; set; }

        [SettingsUISection("Host")]
        [SettingsUITextInput]
        public string BindAddress { get; set; }

        [SettingsUISection("Client")]
        [SettingsUITextInput]
        public string ServerAddress { get; set; }

        [SettingsUISection("General")]
        [SettingsUITextInput]
        public int Port { get; set; }

        public MultiplayerSettings(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        public override void SetDefaults()
        {
            NetworkEnabled = false;
            HostMode = true;
            BindAddress = "0.0.0.0";
            ServerAddress = "127.0.0.1";
            Port = 25565;
        }
    }
}
