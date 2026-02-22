using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Game;
using Game.Input;
using Game.SceneFlow;
using Game.UI;
using UnityEngine;
namespace MultiSkyLineII
{
    public sealed class MultiplayerHudOverlay : MonoBehaviour
    {
        private MultiplayerNetworkService _networkService;
        private Rect _windowRect = new Rect(16f, 56f, 780f, 680f);
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
        private GUIStyle _buttonStyle;
        private GUIStyle _tabStyle;
        private GUIStyle _tabActiveStyle;
        private Texture2D _windowBg;
        private Texture2D _cardBg;
        private Texture2D _meterBg;
        private Texture2D _meterFillGood;
        private Texture2D _meterFillWarn;
        private Texture2D _meterFillBad;
        private Texture2D _buttonBg;
        private Texture2D _buttonBgHover;
        private Texture2D _tabBg;
        private Texture2D _tabBgActive;
        private bool _stylesReady;
        private int _proposalTargetIndex;
        private MultiplayerContractResource _proposalResource = MultiplayerContractResource.Electricity;
        private float _proposalUnits = 5000f;
        private float _proposalPrice = 1000f;
        private string _proposalFeedback;
        private DateTime _proposalFeedbackUntilUtc;
        private string _windowTitle = "MultiSkyLineII Multiplayer";
        private int _activeTab;
        private Vector2 _debugScrollPosition;
        private DateTime _nextLocalStateCaptureUtc;
        private bool _mouseControlsBlocked;
        private InputBarrier _hudInputBarrier;
        private static MethodInfo s_createAllBlockedBarrierMethod;
        private static bool s_createAllBlockedBarrierResolved;

        public void Initialize(MultiplayerNetworkService networkService)
        {
            _networkService = networkService;
            _networkService.CaptureLocalStateOnMainThread();
            var shortVersion = Mod.DisplayVersion;
            if (!string.IsNullOrWhiteSpace(shortVersion))
            {
                _windowTitle = $"MultiSkyLineII Multiplayer v{shortVersion}";
            }
        }

        private void Update()
        {
            if (_networkService != null && DateTime.UtcNow >= _nextLocalStateCaptureUtc)
            {
                _networkService.CaptureLocalStateOnMainThread();
                _nextLocalStateCaptureUtc = DateTime.UtcNow.AddMilliseconds(400);
            }

            var inGameplay = IsInGameplay();
            if (!inGameplay)
            {
                ReleaseMouseControlBlock();
                _visible = false;
                return;
            }

            var hoveringHud = _visible && IsMouseOverHudWindow();
            if (hoveringHud)
            {
                // Prevent camera/game controls from reacting while interacting with HUD.
                Input.ResetInputAxes();
            }

            UpdateMouseControlBlock(hoveringHud);
        }

        private void OnGUI()
        {
            if (_networkService == null || !_networkService.IsRunning || !IsInGameplay())
                return;

            EnsureStyles();
            DrawLauncherButtons();
        }

        private void DrawLauncherButtons()
        {
            var x = Screen.width - 110f;
            var y = 16f;

            if (GUI.Button(new Rect(x, y, 96f, 24f), "MS2", _buttonStyle))
            {
                ShowNativeHubDialog();
                ModDiagnostics.Write("HudOverlay MS2 launcher clicked. Opened native CS2 hub dialog.");
            }
        }

        private void ShowNativeHubDialog()
        {
            try
            {
                var body = new StringBuilder(12288);
                var states = _networkService.GetConnectedStates();
                var contracts = _networkService.GetActiveContracts();
                var proposals = _networkService.GetPendingProposals();
                var logs = _networkService.GetDebugLogLines();
                var local = _networkService.GetLocalState();

                body.AppendLine("## Session");
                body.Append("- Version: ").AppendLine(Mod.DisplayVersion);
                body.Append("- Mode: ").Append(_networkService.IsHost ? "Host" : "Client").Append(" | Destination: ").AppendLine(_networkService.DestinationEndpoint);
                body.Append("- Joueur local: ").Append(local.Name).Append(" | Population: ").Append(local.Population.ToString("N0")).Append(" | Budget: $").AppendLine(local.Money.ToString("N0"));
                body.AppendLine();

                body.Append("## Joueurs (").Append(states.Count).AppendLine(")");
                if (states.Count == 0)
                {
                    body.AppendLine("- Aucun joueur.");
                }
                else
                {
                    for (var i = 0; i < states.Count; i++)
                    {
                        var s = states[i];
                        body.Append("- ").Append(s.Name).Append(" | Ping ").Append(s.PingMs >= 0 ? s.PingMs.ToString() : "n/a").AppendLine(" ms");
                        body.Append("  Elec ").Append(FormatResourceAmount(MultiplayerContractResource.Electricity, s.ElectricityFulfilledConsumption)).Append('/')
                            .Append(FormatResourceAmount(MultiplayerContractResource.Electricity, s.ElectricityConsumption)).Append(" | ");
                        body.Append("Eau ").Append(FormatResourceAmount(MultiplayerContractResource.FreshWater, s.FreshWaterFulfilledConsumption)).Append('/')
                            .Append(FormatResourceAmount(MultiplayerContractResource.FreshWater, s.FreshWaterConsumption)).Append(" | ");
                        body.Append("Eaux ").Append(FormatResourceAmount(MultiplayerContractResource.Sewage, s.SewageFulfilledConsumption)).Append('/')
                            .AppendLine(FormatResourceAmount(MultiplayerContractResource.Sewage, s.SewageConsumption));
                    }
                }

                body.AppendLine();
                body.Append("## Contrats actifs (").Append(contracts.Count).AppendLine(")");
                if (contracts.Count == 0)
                {
                    body.AppendLine("- Aucun contrat actif.");
                }
                else
                {
                    for (var i = 0; i < contracts.Count; i++)
                    {
                        var c = contracts[i];
                        body.Append("- ").Append(c.SellerPlayer).Append(" -> ").Append(c.BuyerPlayer).Append(" | ")
                            .Append(GetResourceLabel(c.Resource)).Append(' ')
                            .Append(FormatResourceRate(c.Resource, c.UnitsPerTick))
                            .Append(" | $").Append(c.PricePerTick).AppendLine("/tick");
                    }
                }

                body.AppendLine();
                body.Append("## Demandes en attente (").Append(proposals.Count).AppendLine(")");
                if (proposals.Count == 0)
                {
                    body.AppendLine("- Aucune demande.");
                }
                else
                {
                    for (var i = 0; i < proposals.Count; i++)
                    {
                        var p = proposals[i];
                        var expiresIn = Mathf.Max(0, 120 - (int)(DateTime.UtcNow - p.CreatedUtc).TotalSeconds);
                        body.Append("- ").Append(p.BuyerPlayer).Append(" -> ").Append(p.SellerPlayer).Append(" | ")
                            .Append(GetResourceLabel(p.Resource)).Append(' ')
                            .Append(FormatResourceRate(p.Resource, p.UnitsPerTick))
                            .Append(" | $").Append(p.PricePerTick).Append(" | ")
                            .Append(expiresIn).AppendLine("s");
                    }
                }

                body.AppendLine();
                body.AppendLine("## Logs reseau (10 derniers)");
                var start = Mathf.Max(0, logs.Count - 10);
                if (logs.Count == 0)
                {
                    body.AppendLine("- Aucun log reseau.");
                }
                else
                {
                    for (var i = start; i < logs.Count; i++)
                    {
                        body.Append("- ").AppendLine(logs[i]);
                    }
                }

                body.AppendLine();
                body.Append("Diagnostics file: ").Append(ModDiagnostics.LogFilePath);

                var dialog = new MessageDialog(
                    "MultiSkyLineII",
                    "Native CS2 panel",
                    body.ToString(),
                    copyButton: true,
                    Game.UI.Localization.LocalizedString.Id("Common.OK"));
                GameManager.instance?.userInterface?.appBindings?.ShowMessageDialog(dialog, _ => { });
            }
            catch (Exception e)
            {
                ModDiagnostics.Write($"HudOverlay failed to open native hub dialog: {e.Message}");
            }
        }

        private bool IsMouseOverHudWindow()
        {
            var mouseGui = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return _windowRect.Contains(mouseGui);
        }

        private void UpdateMouseControlBlock(bool block)
        {
            try
            {
                var input = InputManager.instance;
                if (input == null)
                    return;

                input.mouseOverUI = block;
                EnsureHudInputBarrier(input);
                if (_hudInputBarrier == null)
                    return;

                if (block && !_mouseControlsBlocked)
                {
                    _hudInputBarrier.mask = InputManager.DeviceType.Mouse;
                    _hudInputBarrier.blocked = true;
                    _mouseControlsBlocked = true;
                }
                else if (!block && _mouseControlsBlocked)
                {
                    _hudInputBarrier.blocked = false;
                    _mouseControlsBlocked = false;
                }
            }
            catch
            {
            }
        }

        private void ReleaseMouseControlBlock()
        {
            if (!_mouseControlsBlocked)
                return;

            try
            {
                var input = InputManager.instance;
                if (input != null)
                {
                    input.mouseOverUI = false;
                    if (_hudInputBarrier != null)
                    {
                        _hudInputBarrier.blocked = false;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _mouseControlsBlocked = false;
            }
        }

        private void EnsureHudInputBarrier(InputManager input)
        {
            if (_hudInputBarrier != null)
                return;

            try
            {
                if (!s_createAllBlockedBarrierResolved)
                {
                    s_createAllBlockedBarrierMethod = input.GetType().GetMethod(
                        "CreateAllBlockedBarrier",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new[] { typeof(string) },
                        null);
                    s_createAllBlockedBarrierResolved = true;
                }

                if (s_createAllBlockedBarrierMethod != null)
                {
                    _hudInputBarrier = s_createAllBlockedBarrierMethod.Invoke(input, new object[] { "MultiSkyLineII_HUD" }) as InputBarrier;
                    if (_hudInputBarrier != null)
                    {
                        _hudInputBarrier.mask = InputManager.DeviceType.Mouse;
                        _hudInputBarrier.blocked = false;
                    }
                }
            }
            catch
            {
            }
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

            _windowBg = CreateSolidTexture(new Color32(30, 36, 45, 236));
            _cardBg = CreateSolidTexture(new Color32(45, 54, 66, 238));
            _meterBg = CreateSolidTexture(new Color32(79, 92, 110, 255));
            _meterFillGood = CreateSolidTexture(new Color32(73, 194, 146, 255));
            _meterFillWarn = CreateSolidTexture(new Color32(232, 180, 62, 255));
            _meterFillBad = CreateSolidTexture(new Color32(223, 89, 89, 255));
            _buttonBg = CreateSolidTexture(new Color32(63, 119, 185, 255));
            _buttonBgHover = CreateSolidTexture(new Color32(84, 138, 201, 255));
            _tabBg = CreateSolidTexture(new Color32(57, 66, 80, 255));
            _tabBgActive = CreateSolidTexture(new Color32(79, 137, 202, 255));

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _windowBg;
            _windowStyle.fontSize = 15;
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

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.normal.background = _buttonBg;
            _buttonStyle.hover.background = _buttonBgHover;
            _buttonStyle.active.background = _buttonBgHover;
            _buttonStyle.normal.textColor = new Color32(242, 247, 252, 255);
            _buttonStyle.fontSize = 12;
            _buttonStyle.padding = new RectOffset(8, 8, 4, 4);

            _tabStyle = new GUIStyle(_buttonStyle);
            _tabStyle.normal.background = _tabBg;
            _tabStyle.hover.background = _buttonBgHover;

            _tabActiveStyle = new GUIStyle(_buttonStyle);
            _tabActiveStyle.normal.background = _tabBgActive;
            _tabActiveStyle.hover.background = _tabBgActive;

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
            if (GUI.Button(new Rect(_windowRect.width - 30f, 4f, 24f, 18f), "X", _buttonStyle))
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
            if (GUI.Button(new Rect(16f, 72f, 88f, 22f), "Vue", _activeTab == 0 ? _tabActiveStyle : _tabStyle))
            {
                _activeTab = 0;
            }
            if (GUI.Button(new Rect(108f, 72f, 88f, 22f), "Debug", _activeTab == 1 ? _tabActiveStyle : _tabStyle))
            {
                _activeTab = 1;
            }

            var contentTop = 100f;
            var viewWidth = _windowRect.width - 40f;
            var viewHeight = Mathf.Max(180f, _windowRect.height - 120f);
            if (_activeTab == 1)
            {
                DrawDebugTab(contentTop, viewWidth, viewHeight);
                GUI.DragWindow(new Rect(0f, 0f, _windowRect.width - 34f, 26f));
                return;
            }

            var statesHeight = Mathf.Max(142f, states.Count * 138f);
            var contractsHeight = Mathf.Max(84f, activeContracts.Count * 22f + 34f);
            var pendingHeight = Mathf.Max(84f, pendingProposals.Count * 26f + 34f);
            var proposalHeight = 132f;
            var contentHeight = Mathf.Max(viewHeight, statesHeight + contractsHeight + pendingHeight + proposalHeight + 28f);
            var viewRect = new Rect(16f, contentTop, viewWidth, viewHeight);
            var contentRect = new Rect(0f, 0f, viewWidth - 20f, contentHeight);
            _scrollPosition = GUI.BeginScrollView(viewRect, _scrollPosition, contentRect, false, true);

            var y = 0f;
            for (var i = 0; i < states.Count; i++)
            {
                DrawStateCard(states[i], y, contentRect.width);
                y += 138f;
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
            if (GUI.Button(new Rect(_windowRect.width - 274f, 72f, 82f, 20f), "Log file", _buttonStyle))
            {
                OpenDiagnosticsLog();
            }

            if (GUI.Button(new Rect(_windowRect.width - 186f, 72f, 82f, 20f), "Center", _buttonStyle))
            {
                CenterWindowOnScreen();
            }

            if (GUI.Button(new Rect(_windowRect.width - 96f, 72f, 80f, 20f), "Clear", _buttonStyle))
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

        private void OpenDiagnosticsLog()
        {
            try
            {
                var path = ModDiagnostics.LogFilePath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                    ModDiagnostics.Write($"HudOverlay opened diagnostics log: {path}");
                }
            }
            catch (Exception e)
            {
                ModDiagnostics.Write($"HudOverlay failed to open diagnostics log: {e.Message}");
            }
        }

        private void ShowNativeLogsDialog()
        {
            try
            {
                var logs = _networkService?.GetDebugLogLines();
                var logCount = logs?.Count ?? 0;
                var start = Mathf.Max(0, logCount - 40);
                var body = new StringBuilder(4096);
                body.AppendLine("Recent network logs:");
                body.AppendLine();

                if (logCount == 0)
                {
                    body.AppendLine("(no network logs yet)");
                }
                else
                {
                    for (var i = start; i < logCount; i++)
                    {
                        body.AppendLine(logs[i]);
                    }
                }

                body.AppendLine();
                body.Append("Diagnostics file: ");
                body.Append(ModDiagnostics.LogFilePath);

                var dialog = new MessageDialog(
                    "MultiSkyLineII Logs",
                    "Native CS2 log panel",
                    body.ToString(),
                    copyButton: true,
                    Game.UI.Localization.LocalizedString.Id("Common.OK"));
                GameManager.instance?.userInterface?.appBindings?.ShowMessageDialog(dialog, _ => { });
            }
            catch (Exception e)
            {
                ModDiagnostics.Write($"HudOverlay failed to open native log dialog: {e.Message}");
            }
        }

        private void ShowNativeOverviewDialog()
        {
            try
            {
                var body = new StringBuilder(4096);
                var states = _networkService.GetConnectedStates();
                var local = _networkService.GetLocalState();
                body.Append("Version: ").AppendLine(Mod.DisplayVersion);
                body.Append("Mode: ").Append(_networkService.IsHost ? "Host" : "Client").Append(" | Destination: ").AppendLine(_networkService.DestinationEndpoint);
                body.Append("Joueur local: ").AppendLine(local.Name);
                body.Append("Population: ").Append(local.Population.ToString("N0")).Append(" | Budget: $").AppendLine(local.Money.ToString("N0"));
                body.AppendLine();
                body.Append("Joueurs connectes: ").AppendLine(states.Count.ToString());
                body.AppendLine();

                for (var i = 0; i < states.Count; i++)
                {
                    var s = states[i];
                    body.Append("- ").Append(s.Name).Append(" | Ping ").Append(s.PingMs >= 0 ? s.PingMs.ToString() : "n/a").AppendLine(" ms");
                    body.Append("  Elec ").Append(FormatResourceAmount(MultiplayerContractResource.Electricity, s.ElectricityFulfilledConsumption)).Append('/')
                        .AppendLine(FormatResourceAmount(MultiplayerContractResource.Electricity, s.ElectricityConsumption));
                    body.Append("  Eau  ").Append(FormatResourceAmount(MultiplayerContractResource.FreshWater, s.FreshWaterFulfilledConsumption)).Append('/')
                        .AppendLine(FormatResourceAmount(MultiplayerContractResource.FreshWater, s.FreshWaterConsumption));
                    body.Append("  Eaux ").Append(FormatResourceAmount(MultiplayerContractResource.Sewage, s.SewageFulfilledConsumption)).Append('/')
                        .AppendLine(FormatResourceAmount(MultiplayerContractResource.Sewage, s.SewageConsumption));
                }

                var dialog = new MessageDialog(
                    "MultiSkyLineII Session",
                    "Native CS2 overview",
                    body.ToString(),
                    copyButton: true,
                    Game.UI.Localization.LocalizedString.Id("Common.OK"));
                GameManager.instance?.userInterface?.appBindings?.ShowMessageDialog(dialog, _ => { });
            }
            catch (Exception e)
            {
                ModDiagnostics.Write($"HudOverlay failed to open native overview dialog: {e.Message}");
            }
        }

        private void ShowNativeContractsDialog()
        {
            try
            {
                var body = new StringBuilder(4096);
                var contracts = _networkService.GetActiveContracts();
                var proposals = _networkService.GetPendingProposals();

                body.Append("Contrats actifs: ").AppendLine(contracts.Count.ToString());
                if (contracts.Count == 0)
                {
                    body.AppendLine("- Aucun");
                }
                else
                {
                    for (var i = 0; i < contracts.Count; i++)
                    {
                        var c = contracts[i];
                        body.Append("- ").Append(c.SellerPlayer).Append(" -> ").Append(c.BuyerPlayer).Append(" | ")
                            .Append(GetResourceLabel(c.Resource)).Append(' ')
                            .Append(FormatResourceRate(c.Resource, c.UnitsPerTick))
                            .Append(" | $").Append(c.PricePerTick).AppendLine("/tick");
                    }
                }

                body.AppendLine();
                body.Append("Demandes en attente: ").AppendLine(proposals.Count.ToString());
                if (proposals.Count == 0)
                {
                    body.AppendLine("- Aucune");
                }
                else
                {
                    for (var i = 0; i < proposals.Count; i++)
                    {
                        var p = proposals[i];
                        var expiresIn = Mathf.Max(0, 120 - (int)(DateTime.UtcNow - p.CreatedUtc).TotalSeconds);
                        body.Append("- ").Append(p.BuyerPlayer).Append(" -> ").Append(p.SellerPlayer).Append(" | ")
                            .Append(GetResourceLabel(p.Resource)).Append(' ')
                            .Append(FormatResourceRate(p.Resource, p.UnitsPerTick))
                            .Append(" | $").Append(p.PricePerTick).Append(" | ")
                            .Append(expiresIn).AppendLine("s");
                    }
                }

                var dialog = new MessageDialog(
                    "MultiSkyLineII Contrats",
                    "Native CS2 contracts",
                    body.ToString(),
                    copyButton: true,
                    Game.UI.Localization.LocalizedString.Id("Common.OK"));
                GameManager.instance?.userInterface?.appBindings?.ShowMessageDialog(dialog, _ => { });
            }
            catch (Exception e)
            {
                ModDiagnostics.Write($"HudOverlay failed to open native contracts dialog: {e.Message}");
            }
        }

        private void CenterWindowOnScreen()
        {
            var width = Mathf.Min(_windowRect.width, Mathf.Max(560f, Screen.width - 24f));
            var height = Mathf.Min(_windowRect.height, Mathf.Max(420f, Screen.height - 24f));
            var x = Mathf.Max(12f, (Screen.width - width) * 0.5f);
            var y = Mathf.Max(12f, (Screen.height - height) * 0.5f);
            _windowRect = new Rect(x, y, width, height);
        }

        private void ClampWindowToScreen()
        {
            var width = Mathf.Min(_windowRect.width, Mathf.Max(320f, Screen.width - 12f));
            var height = Mathf.Min(_windowRect.height, Mathf.Max(240f, Screen.height - 12f));
            var maxX = Mathf.Max(6f, Screen.width - width - 6f);
            var maxY = Mathf.Max(6f, Screen.height - height - 6f);
            var x = Mathf.Clamp(_windowRect.x, 6f, maxX);
            var y = Mathf.Clamp(_windowRect.y, 6f, maxY);
            _windowRect = new Rect(x, y, width, height);
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
                var resource = GetResourceLabel(c.Resource);
                GUI.Label(new Rect(12f, lineY, width - 120f, 18f), $"{c.SellerPlayer} -> {c.BuyerPlayer} | {resource} {FormatResourceRate(c.Resource, c.UnitsPerTick)} | ${c.PricePerTick}/tick", _smallStyle);
                var local = _networkService.GetLocalState().Name;
                if (string.Equals(c.SellerPlayer, local, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.BuyerPlayer, local, StringComparison.OrdinalIgnoreCase))
                {
                    if (GUI.Button(new Rect(width - 100f, lineY, 84f, 20f), "Annuler", _buttonStyle))
                    {
                        var ok = _networkService.TryCancelContract(c.Id, out var error);
                        _proposalFeedback = ok ? "Contrat annule." : $"Erreur: {error}";
                        _proposalFeedbackUntilUtc = DateTime.UtcNow.AddSeconds(3);
                    }
                }
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

            GUI.Box(new Rect(4f, y, width - 8f, 126f), GUIContent.none, _cardStyle);
            GUI.Label(new Rect(12f, y + 8f, width - 24f, 18f), "Proposer un contrat", _nameStyle);
            if (targetNames.Count == 0)
            {
                GUI.Label(new Rect(12f, y + 30f, width - 24f, 16f), "Aucun joueur cible disponible.", _smallStyle);
                return;
            }

            if (_proposalTargetIndex >= targetNames.Count)
                _proposalTargetIndex = 0;

            var selectedTarget = targetNames[_proposalTargetIndex];
            MultiplayerResourceState sellerState = default;
            var sellerFound = false;
            for (var i = 0; i < states.Count; i++)
            {
                if (string.Equals(states[i].Name, selectedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    sellerState = states[i];
                    sellerFound = true;
                    break;
                }
            }

            var exportMax = sellerFound ? GetExportableUnits(sellerState, _proposalResource) : 0;
            if (GUI.Button(new Rect(12f, y + 30f, 210f, 22f), $"Acheter a: {selectedTarget}", _buttonStyle))
            {
                _proposalTargetIndex = (_proposalTargetIndex + 1) % targetNames.Count;
            }

            if (GUI.Button(new Rect(228f, y + 30f, 132f, 22f), $"Ressource: {GetResourceLabel(_proposalResource)}", _buttonStyle))
            {
                _proposalResource = (MultiplayerContractResource)(((int)_proposalResource + 1) % 3);
            }

            var sliderMax = Mathf.Max(1, exportMax);
            _proposalUnits = Mathf.Clamp(_proposalUnits, 1f, sliderMax);
            GUI.Label(new Rect(12f, y + 58f, 320f, 16f), $"Quantite/tick: {FormatResourceRate(_proposalResource, Mathf.RoundToInt(_proposalUnits))} (max {FormatResourceRate(_proposalResource, exportMax)})", _smallStyle);
            _proposalUnits = GUI.HorizontalSlider(new Rect(125f, y + 62f, 220f, 14f), _proposalUnits, 1f, sliderMax);

            GUI.Label(new Rect(12f, y + 80f, 170f, 16f), $"Prix/tick: ${(int)_proposalPrice}", _smallStyle);
            _proposalPrice = GUI.HorizontalSlider(new Rect(125f, y + 84f, 220f, 14f), _proposalPrice, 1f, 10000f);

            GUI.Label(new Rect(12f, y + 102f, width - 220f, 16f), "Contrat indefini (annulation manuelle).", _smallStyle);
            if (GUI.Button(new Rect(width - 156f, y + 98f, 140f, 22f), "Envoyer contrat", _buttonStyle))
            {
                if (exportMax <= 0)
                {
                    _proposalFeedback = "Export impossible: surplus nul.";
                    _proposalFeedbackUntilUtc = DateTime.UtcNow.AddSeconds(3);
                    return;
                }

                var ok = _networkService.TryProposeContract(
                    sellerPlayer: selectedTarget,
                    buyerPlayer: local.Name,
                    resource: _proposalResource,
                    unitsPerTick: Mathf.RoundToInt(_proposalUnits),
                    pricePerTick: Mathf.RoundToInt(_proposalPrice),
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
                GUI.Label(new Rect(12f, lineY, width - 200f, 18f), $"{p.BuyerPlayer} demande {resource} {FormatResourceRate(p.Resource, p.UnitsPerTick)} a {p.SellerPlayer} | ${p.PricePerTick}/tick | {expiresIn}s", _smallStyle);

                if (string.Equals(p.SellerPlayer, local.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (GUI.Button(new Rect(width - 184f, lineY, 84f, 20f), "Accepter", _buttonStyle))
                    {
                        var ok = _networkService.TryRespondToProposal(p.Id, true, out var error);
                        _proposalFeedback = ok ? "Demande acceptee." : $"Erreur: {error}";
                        _proposalFeedbackUntilUtc = DateTime.UtcNow.AddSeconds(3);
                    }

                    if (GUI.Button(new Rect(width - 94f, lineY, 78f, 20f), "Refuser", _buttonStyle))
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
            GUI.Box(new Rect(4f, y, width - 8f, 128f), GUIContent.none, _cardStyle);

            var pingText = s.PingMs >= 0 ? $"{s.PingMs} ms" : "n/a";
            GUI.Label(new Rect(14f, y + 8f, 240f, 20f), s.Name, _nameStyle);
            GUI.Label(new Rect(width - 120f, y + 10f, 100f, 18f), $"Ping {pingText}", _metaStyle);
            GUI.Label(new Rect(14f, y + 28f, width - 30f, 18f), $"Population {s.Population:N0}   -   Budget ${s.Money:N0}", _textStyle);
            var speedText = s.IsPaused ? "Pause" : (s.SimulationSpeed > 0 ? $"x{s.SimulationSpeed}" : "n/a");
            var dateText = string.IsNullOrWhiteSpace(s.SimulationDateText) ? "n/a" : s.SimulationDateText;
            GUI.Label(new Rect(14f, y + 44f, width - 30f, 16f), $"Simulation {speedText}   -   Date {dateText}", _smallStyle);
            GUI.Label(new Rect(14f, y + 58f, width - 30f, 16f),
                $"Frontieres distinctes -> Electricite: {(s.HasElectricityOutsideConnection ? "OK" : "KO")} | Eau: {(s.HasWaterOutsideConnection ? "OK" : "KO")} | Eaux usees: {(s.HasSewageOutsideConnection ? "OK" : "KO")}",
                _smallStyle);

            DrawMeter("Electricite", MultiplayerContractResource.Electricity, s.ElectricityFulfilledConsumption, s.ElectricityConsumption, s.ElectricityProduction, "prod", y + 76f, width);
            DrawMeter("Eau", MultiplayerContractResource.FreshWater, s.FreshWaterFulfilledConsumption, s.FreshWaterConsumption, s.FreshWaterCapacity, "cap", y + 92f, width);
            DrawMeter("Eaux usees", MultiplayerContractResource.Sewage, s.SewageFulfilledConsumption, s.SewageConsumption, s.SewageCapacity, "cap", y + 108f, width);
        }

        private static int GetExportableUnits(MultiplayerResourceState s, MultiplayerContractResource resource)
        {
            switch (resource)
            {
                case MultiplayerContractResource.Electricity:
                    if (!s.HasElectricityOutsideConnection)
                        return 0;
                    return Math.Max(0, s.ElectricityProduction - s.ElectricityConsumption);
                case MultiplayerContractResource.FreshWater:
                    if (!s.HasWaterOutsideConnection)
                        return 0;
                    return Math.Max(0, s.FreshWaterCapacity - s.FreshWaterConsumption);
                case MultiplayerContractResource.Sewage:
                    if (!s.HasSewageOutsideConnection)
                        return 0;
                    return Math.Max(0, s.SewageCapacity - s.SewageConsumption);
                default:
                    return 0;
            }
        }

        private void DrawMeter(string label, MultiplayerContractResource resource, int fulfilled, int demand, int aux, string auxLabel, float y, float width)
        {
            var left = 14f;
            var meterX = 102f;
            var meterWidth = width - 318f;
            var meterHeight = 10f;
            var supply = Math.Max(0, aux);
            var consumption = Math.Max(0, demand);
            var served = Math.Max(0, fulfilled);
            var sellable = Math.Max(0, supply - consumption);
            var load = supply <= 0 ? 1f : (float)consumption / supply;
            var ratio = Mathf.Clamp01(load);
            var fillTexture = load > 1f || supply <= 0
                ? _meterFillBad
                : (load > 0.80f ? _meterFillWarn : _meterFillGood);

            GUI.Label(new Rect(left, y - 2f, 84f, 16f), label, _smallStyle);
            GUI.DrawTexture(new Rect(meterX, y, meterWidth, meterHeight), _meterBg);
            GUI.DrawTexture(new Rect(meterX, y, meterWidth * ratio, meterHeight), fillTexture);
            GUI.Label(
                new Rect(meterX + meterWidth + 8f, y - 2f, 300f, 16f),
                $"{auxLabel} {FormatResourceAmount(resource, supply)} | conso {FormatResourceAmount(resource, consumption)} | vendable {FormatResourceAmount(resource, sellable)} | servi {FormatResourceAmount(resource, served)}",
                _smallStyle);
        }

        private static string FormatResourceRate(MultiplayerContractResource resource, int valuePerTick)
        {
            return $"{FormatResourceAmount(resource, valuePerTick)}/tick";
        }

        private static string FormatResourceAmount(MultiplayerContractResource resource, int value)
        {
            switch (resource)
            {
                case MultiplayerContractResource.Electricity:
                    return FormatElectricity(value);
                case MultiplayerContractResource.FreshWater:
                case MultiplayerContractResource.Sewage:
                    return $"{FormatCompact(value)} m3";
                default:
                    return FormatCompact(value);
            }
        }

        private static string FormatElectricity(int value)
        {
            // CS2 electricity stats are in deci-kW (x10). Convert to kW first, then to MW/GW.
            var kw = value / 10f;
            var mw = kw / 1000f;
            var absMw = Math.Abs(mw);
            if (absMw >= 1000f)
                return $"{mw / 1000f:0.##} GW";
            if (absMw >= 1f)
                return $"{mw:0.##} MW";
            return $"{kw:0.##} kW";
        }

        private static string FormatCompact(int value)
        {
            var abs = Math.Abs((long)value);
            if (abs >= 1_000_000_000L)
                return $"{value / 1_000_000_000f:0.##}G";
            if (abs >= 1_000_000L)
                return $"{value / 1_000_000f:0.##}M";
            if (abs >= 1_000L)
                return $"{value / 1_000f:0.##}k";
            return value.ToString();
        }

        private void OnDestroy()
        {
            ReleaseMouseControlBlock();
            if (_windowBg != null)
                Destroy(_windowBg);
            if (_cardBg != null)
                Destroy(_cardBg);
            if (_meterBg != null)
                Destroy(_meterBg);
            if (_meterFillGood != null)
                Destroy(_meterFillGood);
            if (_meterFillWarn != null)
                Destroy(_meterFillWarn);
            if (_meterFillBad != null)
                Destroy(_meterFillBad);
            if (_buttonBg != null)
                Destroy(_buttonBg);
            if (_buttonBgHover != null)
                Destroy(_buttonBgHover);
            if (_tabBg != null)
                Destroy(_tabBg);
            if (_tabBgActive != null)
                Destroy(_tabBgActive);
            if (_hudInputBarrier != null)
            {
                _hudInputBarrier.Dispose();
                _hudInputBarrier = null;
            }
        }
    }
}






