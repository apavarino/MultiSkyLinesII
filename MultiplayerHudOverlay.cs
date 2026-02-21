using UnityEngine;

namespace MultiSkyLineII
{
    public sealed class MultiplayerHudOverlay : MonoBehaviour
    {
        private MultiplayerNetworkService _networkService;
        private Rect _windowRect = new Rect(16f, 120f, 430f, 260f);
        private bool _visible = true;
        private const int WindowId = 932104;

        public void Initialize(MultiplayerNetworkService networkService)
        {
            _networkService = networkService;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                _visible = !_visible;
            }
        }

        private void OnGUI()
        {
            if (_networkService == null || !_networkService.IsRunning || !_visible)
                return;

            _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, "MultiSkyLineII Multiplayer (F8)");
        }

        private void DrawWindow(int _)
        {
            var states = _networkService.GetConnectedStates();
            var y = 24f;

            GUI.Label(new Rect(10f, y, 390f, 20f), _networkService.IsHost ? "Mode: Host" : "Mode: Client");
            y += 22f;

            for (var i = 0; i < states.Count; i++)
            {
                var s = states[i];
                var pingText = s.PingMs >= 0 ? $"{s.PingMs} ms" : "-";
                GUI.Label(new Rect(10f, y, 410f, 20f), $"{s.Name} | Ping: {pingText} | $: {s.Money} | Pop: {s.Population}");
                y += 18f;
                GUI.Label(new Rect(18f, y, 400f, 20f), $"Elec {s.ElectricityFulfilledConsumption}/{s.ElectricityConsumption} (Prod {s.ElectricityProduction})");
                y += 18f;
                GUI.Label(new Rect(18f, y, 400f, 20f), $"Water {s.FreshWaterFulfilledConsumption}/{s.FreshWaterConsumption} (Cap {s.FreshWaterCapacity})");
                y += 18f;
                GUI.Label(new Rect(18f, y, 400f, 20f), $"Sewage {s.SewageFulfilledConsumption}/{s.SewageConsumption} (Cap {s.SewageCapacity})");
                y += 20f;
            }

            if (states.Count <= 1)
            {
                GUI.Label(new Rect(10f, y, 390f, 20f), "No remote city connected yet.");
            }

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }
    }
}
