using System;
using UnityEngine;

namespace MultiSkyLineII
{
    public sealed class MultiplayerHudOverlay : MonoBehaviour
    {
        private MultiplayerNetworkService _networkService;
        private Rect _windowRect = new Rect(18f, 120f, 500f, 360f);
        private bool _visible = true;
        private const int WindowId = 932104;
        private Vector2 _scrollPosition;
        private DateTime _nextGameStateRefreshUtc;
        private bool _isInGameplayCached;
        private GUIStyle _windowStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _metaStyle;
        private GUIStyle _cardStyle;
        private GUIStyle _nameStyle;
        private GUIStyle _textStyle;
        private GUIStyle _smallStyle;
        private Texture2D _windowBg;
        private Texture2D _cardBg;
        private Texture2D _meterBg;
        private Texture2D _meterFill;
        private bool _stylesReady;

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
            if (_networkService == null || !_networkService.IsRunning || !_visible || !IsInGameplay())
                return;

            EnsureStyles();
            _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, "MultiSkyLineII Multiplayer (F8)");
        }

        private bool IsInGameplay()
        {
            if (DateTime.UtcNow < _nextGameStateRefreshUtc)
                return _isInGameplayCached;

            _nextGameStateRefreshUtc = DateTime.UtcNow.AddMilliseconds(500);
            _isInGameplayCached = MultiplayerResourceReader.HasActiveCity();
            return _isInGameplayCached;
        }

        private void EnsureStyles()
        {
            if (_stylesReady)
                return;

            _windowBg = CreateSolidTexture(new Color32(16, 22, 31, 230));
            _cardBg = CreateSolidTexture(new Color32(27, 35, 48, 238));
            _meterBg = CreateSolidTexture(new Color32(55, 69, 88, 255));
            _meterFill = CreateSolidTexture(new Color32(73, 194, 146, 255));

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _windowBg;
            _windowStyle.fontSize = 14;
            _windowStyle.padding = new RectOffset(12, 12, 28, 12);

            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.fontSize = 16;
            _titleStyle.normal.textColor = new Color32(235, 242, 248, 255);

            _metaStyle = new GUIStyle(GUI.skin.label);
            _metaStyle.fontSize = 12;
            _metaStyle.normal.textColor = new Color32(153, 173, 194, 255);

            _cardStyle = new GUIStyle(GUI.skin.box);
            _cardStyle.normal.background = _cardBg;
            _cardStyle.border = new RectOffset(8, 8, 8, 8);

            _nameStyle = new GUIStyle(GUI.skin.label);
            _nameStyle.fontSize = 14;
            _nameStyle.normal.textColor = new Color32(244, 248, 252, 255);

            _textStyle = new GUIStyle(GUI.skin.label);
            _textStyle.fontSize = 12;
            _textStyle.normal.textColor = new Color32(215, 226, 237, 255);

            _smallStyle = new GUIStyle(GUI.skin.label);
            _smallStyle.fontSize = 11;
            _smallStyle.normal.textColor = new Color32(153, 173, 194, 255);

            _stylesReady = true;
        }

        private static Texture2D CreateSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply(false, true);
            return tex;
        }

        private void DrawWindow(int _)
        {
            var states = _networkService.GetConnectedStates();
            var modeText = _networkService.IsHost ? "Host" : "Client";

            GUI.Box(new Rect(8f, 30f, _windowRect.width - 16f, _windowRect.height - 40f), GUIContent.none, _windowStyle);
            GUI.Label(new Rect(16f, 36f, 280f, 22f), "Session Multiplayer", _titleStyle);
            GUI.Label(new Rect(_windowRect.width - 170f, 40f, 150f, 18f), $"Mode: {modeText}", _metaStyle);

            var contentTop = 64f;
            var viewWidth = _windowRect.width - 40f;
            var viewHeight = Mathf.Max(150f, _windowRect.height - 90f);
            var contentHeight = Mathf.Max(viewHeight, states.Count * 112f);
            var viewRect = new Rect(16f, contentTop, viewWidth, viewHeight);
            var contentRect = new Rect(0f, 0f, viewWidth - 20f, contentHeight);
            _scrollPosition = GUI.BeginScrollView(viewRect, _scrollPosition, contentRect, false, true);

            var y = 0f;
            for (var i = 0; i < states.Count; i++)
            {
                DrawStateCard(states[i], y, contentRect.width);
                y += 108f;
            }

            if (states.Count <= 1)
            {
                GUI.Label(new Rect(8f, 10f, contentRect.width - 16f, 20f), "Aucune autre ville connectee pour le moment.", _smallStyle);
            }
            GUI.EndScrollView();

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 26f));
        }

        private void DrawStateCard(MultiplayerResourceState s, float y, float width)
        {
            GUI.Box(new Rect(4f, y, width - 8f, 96f), GUIContent.none, _cardStyle);

            var pingText = s.PingMs >= 0 ? $"{s.PingMs} ms" : "n/a";
            GUI.Label(new Rect(14f, y + 8f, 240f, 20f), s.Name, _nameStyle);
            GUI.Label(new Rect(width - 120f, y + 10f, 100f, 18f), $"Ping {pingText}", _metaStyle);
            GUI.Label(new Rect(14f, y + 28f, width - 30f, 18f), $"Population {s.Population:N0}   •   Budget ${s.Money:N0}", _textStyle);

            DrawMeter("Electricite", s.ElectricityFulfilledConsumption, s.ElectricityConsumption, s.ElectricityProduction, "prod", y + 50f, width);
            DrawMeter("Eau", s.FreshWaterFulfilledConsumption, s.FreshWaterConsumption, s.FreshWaterCapacity, "cap", y + 66f, width);
            DrawMeter("Eaux usees", s.SewageFulfilledConsumption, s.SewageConsumption, s.SewageCapacity, "cap", y + 82f, width);
        }

        private void DrawMeter(string label, int fulfilled, int demand, int aux, string auxLabel, float y, float width)
        {
            var left = 14f;
            var meterX = 102f;
            var meterWidth = width - 230f;
            var meterHeight = 10f;
            var ratio = demand <= 0 ? 1f : Mathf.Clamp01((float)fulfilled / demand);

            GUI.Label(new Rect(left, y - 2f, 84f, 16f), label, _smallStyle);
            GUI.DrawTexture(new Rect(meterX, y, meterWidth, meterHeight), _meterBg);
            GUI.DrawTexture(new Rect(meterX, y, meterWidth * ratio, meterHeight), _meterFill);
            GUI.Label(new Rect(meterX + meterWidth + 8f, y - 2f, 100f, 16f), $"{fulfilled}/{demand} ({auxLabel} {aux})", _smallStyle);
        }

        private void OnDestroy()
        {
            if (_windowBg != null)
                Destroy(_windowBg);
            if (_cardBg != null)
                Destroy(_cardBg);
            if (_meterBg != null)
                Destroy(_meterBg);
            if (_meterFill != null)
                Destroy(_meterFill);
        }
    }
}
