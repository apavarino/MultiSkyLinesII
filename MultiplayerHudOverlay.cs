using System;
using UnityEngine;
namespace MultiSkyLineII
{
    public sealed class MultiplayerHudOverlay : MonoBehaviour
    {
        private MultiplayerNetworkService _networkService;
        private Rect _windowRect = new Rect(18f, 72f, 540f, 520f);
        private bool _visible;
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
        private int _proposalTargetIndex;
        private MultiplayerContractResource _proposalResource = MultiplayerContractResource.Electricity;
        private float _proposalUnits = 120f;
        private float _proposalPrice = 3f;
        private float _proposalDuration = 180f;
        private string _proposalFeedback;
        private DateTime _proposalFeedbackUntilUtc;
        private string _windowTitle = "MultiSkyLineII Multiplayer";
        private int _activeTab;
        private Vector2 _debugScrollPosition;

        public void Initialize(MultiplayerNetworkService networkService)
        {
            _networkService = networkService;
            var shortVersion = Mod.DisplayVersion;
            if (!string.IsNullOrWhiteSpace(shortVersion))
            {
                _windowTitle = $"MultiSkyLineII Multiplayer v{shortVersion}";
            }
        }

        private void Update()
        {
            var inGameplay = IsInGameplay();
            if (!inGameplay)
            {
                _visible = false;
                return;
            }

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
            _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, $"{_windowTitle} (F8)");
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
            if (GUI.Button(new Rect(_windowRect.width - 28f, 4f, 22f, 18f), "X"))
            {
                _visible = false;
                return;
            }

            var states = _networkService.GetConnectedStates();
            var modeText = _networkService.IsHost ? "Host" : "Client";
            var destinationText = _networkService.DestinationEndpoint;
            var activeContracts = _networkService.GetActiveContracts();
            var pendingProposals = _networkService.GetPendingProposals();

            GUI.Box(new Rect(8f, 30f, _windowRect.width - 16f, _windowRect.height - 40f), GUIContent.none, _windowStyle);
            GUI.Label(new Rect(16f, 36f, 280f, 22f), "Session Multiplayer", _titleStyle);
            GUI.Label(new Rect(_windowRect.width - 170f, 40f, 150f, 18f), $"Mode: {modeText}", _metaStyle);
            GUI.Label(new Rect(16f, 56f, _windowRect.width - 32f, 16f), $"Destination: {destinationText}", _smallStyle);
            if (GUI.Button(new Rect(16f, 72f, 72f, 20f), "Vue"))
            {
                _activeTab = 0;
            }
            if (GUI.Button(new Rect(92f, 72f, 72f, 20f), "Debug"))
            {
                _activeTab = 1;
            }

            var contentTop = 96f;
            var viewWidth = _windowRect.width - 40f;
            var viewHeight = Mathf.Max(150f, _windowRect.height - 90f);
            if (_activeTab == 1)
            {
                DrawDebugTab(contentTop, viewWidth, viewHeight);
                GUI.DragWindow(new Rect(0f, 0f, _windowRect.width - 34f, 26f));
                return;
            }

            var statesHeight = Mathf.Max(112f, states.Count * 112f);
            var contractsHeight = Mathf.Max(84f, activeContracts.Count * 22f + 34f);
            var pendingHeight = Mathf.Max(84f, pendingProposals.Count * 26f + 34f);
            var proposalHeight = 158f;
            var contentHeight = Mathf.Max(viewHeight, statesHeight + contractsHeight + pendingHeight + proposalHeight + 28f);
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
                GUI.Label(new Rect(8f, 10f, contentRect.width - 16f, 20f), "Aucun autre joueur connecte pour le moment.", _smallStyle);
            }

            y = statesHeight + 6f;
            DrawContractsSection(activeContracts, y, contentRect.width);
            y += contractsHeight + 8f;
            DrawPendingProposalsSection(pendingProposals, y, contentRect.width);
            y += pendingHeight + 8f;
            DrawProposalSection(states, y, contentRect.width);
            GUI.EndScrollView();

            GUI.DragWindow(new Rect(0f, 0f, _windowRect.width - 34f, 26f));
        }

        private void DrawDebugTab(float contentTop, float viewWidth, float viewHeight)
        {
            var logs = _networkService.GetDebugLogLines();
            if (GUI.Button(new Rect(_windowRect.width - 96f, 72f, 80f, 20f), "Clear"))
            {
                _networkService.ClearDebugLog();
            }

            var viewRect = new Rect(16f, contentTop, viewWidth, viewHeight);
            var contentHeight = Mathf.Max(viewHeight, logs.Count * 18f + 12f);
            var contentRect = new Rect(0f, 0f, viewWidth - 20f, contentHeight);
            _debugScrollPosition = GUI.BeginScrollView(viewRect, _debugScrollPosition, contentRect, false, true);
            if (logs.Count == 0)
            {
                GUI.Label(new Rect(8f, 8f, contentRect.width - 16f, 18f), "Aucun log reseau.", _smallStyle);
            }
            else
            {
                var y = 0f;
                for (var i = 0; i < logs.Count; i++)
                {
                    GUI.Label(new Rect(8f, y, contentRect.width - 16f, 18f), logs[i], _smallStyle);
                    y += 18f;
                }
            }
            GUI.EndScrollView();
        }

        private void DrawContractsSection(System.Collections.Generic.IReadOnlyList<MultiplayerContract> contracts, float y, float width)
        {
            GUI.Box(new Rect(4f, y, width - 8f, Mathf.Max(84f, contracts.Count * 22f + 34f)), GUIContent.none, _cardStyle);
            GUI.Label(new Rect(12f, y + 8f, width - 24f, 18f), $"Contrats actifs: {contracts.Count}", _nameStyle);
            if (contracts.Count == 0)
            {
                GUI.Label(new Rect(12f, y + 28f, width - 24f, 16f), "Aucun contrat actif.", _smallStyle);
                return;
            }

            var lineY = y + 28f;
            for (var i = 0; i < contracts.Count; i++)
            {
                var c = contracts[i];
                var secondsLeft = Mathf.Max(0, (int)(c.CreatedUtc.AddSeconds(c.DurationSeconds) - DateTime.UtcNow).TotalSeconds);
                var resource = GetResourceLabel(c.Resource);
                GUI.Label(new Rect(12f, lineY, width - 24f, 18f), $"{c.SellerPlayer} -> {c.BuyerPlayer} | {resource} {c.UnitsPerTick}/tick | ${c.PricePerUnit}/u | {secondsLeft}s", _smallStyle);
                lineY += 20f;
            }
        }

        private void DrawProposalSection(System.Collections.Generic.IReadOnlyList<MultiplayerResourceState> states, float y, float width)
        {
            var local = _networkService.GetLocalState();
            var targetNames = new System.Collections.Generic.List<string>();
            for (var i = 0; i < states.Count; i++)
            {
                if (!string.Equals(states[i].Name, local.Name, StringComparison.OrdinalIgnoreCase))
                {
                    targetNames.Add(states[i].Name);
                }
            }

            GUI.Box(new Rect(4f, y, width - 8f, 152f), GUIContent.none, _cardStyle);
            GUI.Label(new Rect(12f, y + 8f, width - 24f, 18f), "Proposer un contrat", _nameStyle);
            if (targetNames.Count == 0)
            {
                GUI.Label(new Rect(12f, y + 30f, width - 24f, 16f), "Aucun joueur cible disponible.", _smallStyle);
                return;
            }

            if (_proposalTargetIndex >= targetNames.Count)
                _proposalTargetIndex = 0;

            var selectedTarget = targetNames[_proposalTargetIndex];
            if (GUI.Button(new Rect(12f, y + 30f, 210f, 22f), $"Acheter a: {selectedTarget}"))
            {
                _proposalTargetIndex = (_proposalTargetIndex + 1) % targetNames.Count;
            }

            if (GUI.Button(new Rect(228f, y + 30f, 120f, 22f), $"Ressource: {GetResourceLabel(_proposalResource)}"))
            {
                _proposalResource = (MultiplayerContractResource)(((int)_proposalResource + 1) % 3);
            }

            GUI.Label(new Rect(12f, y + 58f, 150f, 16f), $"Quantite/tick: {(int)_proposalUnits}", _smallStyle);
            _proposalUnits = GUI.HorizontalSlider(new Rect(125f, y + 62f, 220f, 14f), _proposalUnits, 10f, 1000f);

            GUI.Label(new Rect(12f, y + 80f, 150f, 16f), $"Prix/unite: ${(int)_proposalPrice}", _smallStyle);
            _proposalPrice = GUI.HorizontalSlider(new Rect(125f, y + 84f, 220f, 14f), _proposalPrice, 1f, 50f);

            GUI.Label(new Rect(12f, y + 102f, 150f, 16f), $"Duree: {(int)_proposalDuration}s", _smallStyle);
            _proposalDuration = GUI.HorizontalSlider(new Rect(125f, y + 106f, 220f, 14f), _proposalDuration, 30f, 600f);

            if (GUI.Button(new Rect(width - 156f, y + 122f, 140f, 22f), "Envoyer contrat"))
            {
                var ok = _networkService.TryProposeContract(
                    sellerPlayer: selectedTarget,
                    buyerPlayer: local.Name,
                    resource: _proposalResource,
                    unitsPerTick: Mathf.RoundToInt(_proposalUnits),
                    pricePerUnit: Mathf.RoundToInt(_proposalPrice),
                    durationSeconds: Mathf.RoundToInt(_proposalDuration),
                    out var error);

                _proposalFeedback = ok ? "Demande envoyee." : $"Erreur: {error}";
                _proposalFeedbackUntilUtc = DateTime.UtcNow.AddSeconds(3);
            }

            if (DateTime.UtcNow < _proposalFeedbackUntilUtc && !string.IsNullOrWhiteSpace(_proposalFeedback))
            {
                GUI.Label(new Rect(12f, y + 126f, width - 176f, 16f), _proposalFeedback, _smallStyle);
            }
        }

        private void DrawPendingProposalsSection(System.Collections.Generic.IReadOnlyList<MultiplayerContractProposal> proposals, float y, float width)
        {
            var local = _networkService.GetLocalState();
            GUI.Box(new Rect(4f, y, width - 8f, Mathf.Max(84f, proposals.Count * 26f + 34f)), GUIContent.none, _cardStyle);
            GUI.Label(new Rect(12f, y + 8f, width - 24f, 18f), $"Demandes en attente: {proposals.Count}", _nameStyle);
            if (proposals.Count == 0)
            {
                GUI.Label(new Rect(12f, y + 28f, width - 24f, 16f), "Aucune demande en attente.", _smallStyle);
                return;
            }

            var lineY = y + 30f;
            for (var i = 0; i < proposals.Count; i++)
            {
                var p = proposals[i];
                var resource = GetResourceLabel(p.Resource);
                var expiresIn = Mathf.Max(0, 120 - (int)(DateTime.UtcNow - p.CreatedUtc).TotalSeconds);
                GUI.Label(new Rect(12f, lineY, width - 200f, 18f), $"{p.BuyerPlayer} demande {resource} {p.UnitsPerTick}/tick a {p.SellerPlayer} | ${p.PricePerUnit}/u | {expiresIn}s", _smallStyle);

                if (string.Equals(p.SellerPlayer, local.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (GUI.Button(new Rect(width - 184f, lineY, 84f, 20f), "Accepter"))
                    {
                        var ok = _networkService.TryRespondToProposal(p.Id, true, out var error);
                        _proposalFeedback = ok ? "Demande acceptee." : $"Erreur: {error}";
                        _proposalFeedbackUntilUtc = DateTime.UtcNow.AddSeconds(3);
                    }

                    if (GUI.Button(new Rect(width - 94f, lineY, 78f, 20f), "Refuser"))
                    {
                        var ok = _networkService.TryRespondToProposal(p.Id, false, out var error);
                        _proposalFeedback = ok ? "Demande refusee." : $"Erreur: {error}";
                        _proposalFeedbackUntilUtc = DateTime.UtcNow.AddSeconds(3);
                    }
                }

                lineY += 24f;
            }
        }

        private static string GetResourceLabel(MultiplayerContractResource resource)
        {
            switch (resource)
            {
                case MultiplayerContractResource.Electricity:
                    return "Electricite";
                case MultiplayerContractResource.FreshWater:
                    return "Eau";
                case MultiplayerContractResource.Sewage:
                    return "Eaux usees";
                default:
                    return "Inconnue";
            }
        }

        private void DrawStateCard(MultiplayerResourceState s, float y, float width)
        {
            GUI.Box(new Rect(4f, y, width - 8f, 96f), GUIContent.none, _cardStyle);

            var pingText = s.PingMs >= 0 ? $"{s.PingMs} ms" : "n/a";
            GUI.Label(new Rect(14f, y + 8f, 240f, 20f), s.Name, _nameStyle);
            GUI.Label(new Rect(width - 120f, y + 10f, 100f, 18f), $"Ping {pingText}", _metaStyle);
            GUI.Label(new Rect(14f, y + 28f, width - 30f, 18f), $"Population {s.Population:N0}   -   Budget ${s.Money:N0}", _textStyle);

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



