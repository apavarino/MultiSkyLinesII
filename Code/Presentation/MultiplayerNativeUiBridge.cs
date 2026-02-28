using System;
using System.Collections.Generic;
using System.Text;
using Colossal.UI.Binding;
using Game;
using Game.SceneFlow;
using UnityEngine;

namespace MultiSkyLineII
{
    public sealed class MultiplayerNativeUiBridge : MonoBehaviour
    {
        private const string Group = "multisky";

        private MultiplayerNetworkService _networkService;
        private readonly List<IBinding> _bindings = new List<IBinding>();
        private ValueBinding<bool> _visibleBinding;
        private ValueBinding<string> _payloadBinding;
        private bool _visible;
        private bool _bindingsRegistered;
        private string _lastPayload = "{}";
        private string _uiMessage;
        private DateTime _uiMessageUntilUtc;
        private DateTime _nextLocalStateCaptureUtc;
        private DateTime _nextPayloadRefreshUtc;
        private DateTime _uiHandshakeDeadlineUtc;
        private bool _uiHandshakeReceived;
        private bool _uiHandshakeTimeoutLogged;
        private DateTime _nextUiPulseLogUtc;
        private int _uiReadySinceLastLog;
        private int _uiPingSinceLastLog;
        private string _lastUiPingText = "<none>";

        public void Initialize(MultiplayerNetworkService networkService)
        {
            _networkService = networkService;
            ModDiagnostics.Write("NativeUiBridge.Initialize called.");
            _uiHandshakeDeadlineUtc = DateTime.UtcNow.AddSeconds(25);
            RegisterBindings();
            PublishSnapshot(force: true);
        }

        private void Update()
        {
            if (_networkService == null)
                return;

            if (!_bindingsRegistered)
            {
                RegisterBindings();
            }

            if (DateTime.UtcNow >= _nextLocalStateCaptureUtc)
            {
                _networkService.CaptureLocalStateOnMainThread();
                _nextLocalStateCaptureUtc = DateTime.UtcNow.AddMilliseconds(400);
            }

            var inGameplay = MultiplayerResourceReader.HasActiveCity();
            if (!inGameplay && _visible)
            {
                SetVisible(false);
            }
            else if (inGameplay && Input.GetKeyDown(KeyCode.F8))
            {
                ModDiagnostics.Write($"NativeUiBridge F8 pressed. Visible before toggle={_visible}");
                SetVisible(!_visible);
            }

            if (DateTime.UtcNow >= _nextPayloadRefreshUtc)
            {
                PublishSnapshot(force: false);
                _nextPayloadRefreshUtc = DateTime.UtcNow.AddMilliseconds(300);
            }

            if (!_uiHandshakeReceived && !_uiHandshakeTimeoutLogged && DateTime.UtcNow >= _uiHandshakeDeadlineUtc)
            {
                _uiHandshakeTimeoutLogged = true;
                ModDiagnostics.Write("NativeUiBridge warning: UI handshake was not received within timeout. JS UI module likely not mounted by game.");
            }
        }

        private void RegisterBindings()
        {
            if (_bindingsRegistered)
                return;

            try
            {
                var bindingStore = GameManager.instance?.userInterface?.bindings;
                if (bindingStore == null)
                    return;

                _visibleBinding = new ValueBinding<bool>(Group, "visible", initialValue: false);
                _payloadBinding = new ValueBinding<string>(Group, "payload", "{}", ValueWriters.Nullable(new StringWriter()));

                AddBinding(_visibleBinding);
                AddBinding(_payloadBinding);
                AddBinding(new TriggerBinding<bool>(Group, "setVisible", SetVisible));
                AddBinding(new TriggerBinding(Group, "toggleVisible", ToggleVisible));
                AddBinding(new TriggerBinding<string>(Group, "propose", ProposeContractPacked));
                AddBinding(new TriggerBinding<string>(Group, "respond", RespondProposalPacked));
                AddBinding(new TriggerBinding<string>(Group, "cancel", CancelContract));
                AddBinding(new TriggerBinding(Group, "clearLogs", ClearLogs));
                AddBinding(new TriggerBinding(Group, "uiReady", UiReady));
                AddBinding(new TriggerBinding<string>(Group, "uiPing", UiPing));
                _bindingsRegistered = true;
                ModDiagnostics.Write("NativeUiBridge bindings registered.");
                _visibleBinding?.Update(_visible);
                PublishSnapshot(force: true);
            }
            catch (Exception e)
            {
                ModDiagnostics.Warn($"Failed to register native UI bindings: {e.Message}");
                ModDiagnostics.Write($"NativeUiBridge bindings registration failed: {e}");
            }
        }

        private void AddBinding(IBinding binding)
        {
            var bindingStore = GameManager.instance?.userInterface?.bindings;
            if (bindingStore == null)
                return;
            bindingStore.AddBinding(binding);
            _bindings.Add(binding);
        }

        private void ToggleVisible()
        {
            SetVisible(!_visible);
        }

        private void SetVisible(bool visible)
        {
            var inGameplay = MultiplayerResourceReader.HasActiveCity();
            var next = visible && inGameplay;
            if (_visible == next)
                return;

            _visible = next;
            ModDiagnostics.Info($"[NativeHUD] SetVisible -> {_visible} (inGameplay={inGameplay})");
            ModDiagnostics.Write($"NativeUiBridge.SetVisible({_visible}) inGameplay={inGameplay}");
            _visibleBinding?.Update(_visible);
            PublishSnapshot(force: true);
        }

        private void ProposeContractPacked(string packed)
        {
            if (string.IsNullOrWhiteSpace(packed))
                return;

            var parts = packed.Split('|');
            if (parts.Length < 5)
                return;

            if (!int.TryParse(parts[2], out var resource))
                resource = 0;
            if (!int.TryParse(parts[3], out var unitsPerTick))
                unitsPerTick = 1;
            if (!int.TryParse(parts[4], out var pricePerTick))
                pricePerTick = 1;

            ProposeContract(parts[0], parts[1], resource, unitsPerTick, pricePerTick);
        }

        private void ProposeContract(string sellerPlayer, string buyerPlayer, int resource, int unitsPerTick, int pricePerTick)
        {
            if (_networkService == null)
                return;

            var ok = _networkService.TryProposeContract(
                sellerPlayer,
                buyerPlayer,
                (MultiplayerContractResource)Mathf.Clamp(resource, 0, 2),
                Mathf.Max(1, unitsPerTick),
                Mathf.Max(1, pricePerTick),
                out var error);

            SetUiMessage(ok ? "Demande envoyee." : $"Erreur: {error}");
            PublishSnapshot(force: true);
        }

        private void RespondProposalPacked(string packed)
        {
            if (string.IsNullOrWhiteSpace(packed))
                return;

            var parts = packed.Split('|');
            if (parts.Length < 2)
                return;

            var id = parts[0];
            var accept = string.Equals(parts[1], "1", StringComparison.Ordinal) ||
                         string.Equals(parts[1], "true", StringComparison.OrdinalIgnoreCase);
            RespondProposal(id, accept);
        }

        private void RespondProposal(string id, bool accept)
        {
            if (_networkService == null)
                return;

            var ok = _networkService.TryRespondToProposal(id, accept, out var error);
            SetUiMessage(ok ? (accept ? "Demande acceptee." : "Demande refusee.") : $"Erreur: {error}");
            PublishSnapshot(force: true);
        }

        private void CancelContract(string id)
        {
            if (_networkService == null)
                return;

            var ok = _networkService.TryCancelContract(id, out var error);
            SetUiMessage(ok ? "Contrat annule." : $"Erreur: {error}");
            PublishSnapshot(force: true);
        }

        private void ClearLogs()
        {
            _networkService?.ClearDebugLog();
            PublishSnapshot(force: true);
        }

        private void UiReady()
        {
            _uiHandshakeReceived = true;
            _uiReadySinceLastLog++;
            LogUiPulse(force: false);
        }

        private void UiPing(string message)
        {
            _lastUiPingText = string.IsNullOrWhiteSpace(message) ? "<empty>" : message.Trim();
            _uiPingSinceLastLog++;
            var force = !_lastUiPingText.Equals("module heartbeat", StringComparison.OrdinalIgnoreCase);
            LogUiPulse(force);
        }

        private void LogUiPulse(bool force)
        {
            var now = DateTime.UtcNow;
            if (!force && now < _nextUiPulseLogUtc)
                return;

            _nextUiPulseLogUtc = now.AddSeconds(20);
            ModDiagnostics.Write(
                $"NativeUiBridge UI pulse: pingCount={_uiPingSinceLastLog}, readyCount={_uiReadySinceLastLog}, lastPing={_lastUiPingText}, handshake={_uiHandshakeReceived}");
            _uiPingSinceLastLog = 0;
            _uiReadySinceLastLog = 0;
        }

        private void SetUiMessage(string message)
        {
            _uiMessage = message;
            _uiMessageUntilUtc = DateTime.UtcNow.AddSeconds(4);
        }

        private void PublishSnapshot(bool force)
        {
            if (_networkService == null)
                return;

            var payload = BuildSnapshotJson();
            if (!force && string.Equals(payload, _lastPayload, StringComparison.Ordinal))
                return;

            _lastPayload = payload;
            _payloadBinding?.Update(payload);
        }

        private string BuildSnapshotJson()
        {
            var sb = new StringBuilder(8192);
            var states = _networkService.GetConnectedStates();
            var contracts = _networkService.GetActiveContracts();
            var proposals = _networkService.GetPendingProposals();
            var logs = _networkService.GetDebugLogLines();
            var local = _networkService.GetLocalState();
            var message = DateTime.UtcNow < _uiMessageUntilUtc ? _uiMessage : string.Empty;
            var locale = LocalizationCatalog.NormalizeLocale(Mod.CurrentLocale);

            sb.Append('{');
            AppendProp(sb, "version", Mod.DisplayVersion); sb.Append(',');
            AppendProp(sb, "mode", _networkService.IsHost ? "Host" : "Client"); sb.Append(',');
            AppendProp(sb, "destination", _networkService.DestinationEndpoint); sb.Append(',');
            AppendProp(sb, "localName", local.Name); sb.Append(',');
            AppendProp(sb, "message", message); sb.Append(',');
            AppendProp(sb, "uiLocale", locale); sb.Append(',');
            sb.Append("\"contractsEnabled\":true,");
            AppendProp(sb, "contractsEnabledDebug", "true"); sb.Append(',');
            AppendUiStrings(sb, locale); sb.Append(',');
            sb.Append("\"visible\":").Append(_visible ? "true" : "false").Append(',');
            sb.Append("\"states\":[");
            for (var i = 0; i < states.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendState(sb, states[i]);
            }
            sb.Append("],");
            sb.Append("\"contracts\":[");
            for (var i = 0; i < contracts.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var c = contracts[i];
                sb.Append('{');
                AppendProp(sb, "id", c.Id); sb.Append(',');
                AppendProp(sb, "seller", c.SellerPlayer); sb.Append(',');
                AppendProp(sb, "buyer", c.BuyerPlayer); sb.Append(',');
                AppendProp(sb, "resource", (int)c.Resource); sb.Append(',');
                AppendProp(sb, "unitsPerTick", c.UnitsPerTick); sb.Append(',');
                AppendProp(sb, "pricePerTick", c.PricePerTick);
                sb.Append('}');
            }
            sb.Append("],");
            sb.Append("\"proposals\":[");
            for (var i = 0; i < proposals.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = proposals[i];
                var ageSeconds = Math.Max(0, (int)(DateTime.UtcNow - p.CreatedUtc).TotalSeconds);
                sb.Append('{');
                AppendProp(sb, "id", p.Id); sb.Append(',');
                AppendProp(sb, "seller", p.SellerPlayer); sb.Append(',');
                AppendProp(sb, "buyer", p.BuyerPlayer); sb.Append(',');
                AppendProp(sb, "resource", (int)p.Resource); sb.Append(',');
                AppendProp(sb, "unitsPerTick", p.UnitsPerTick); sb.Append(',');
                AppendProp(sb, "pricePerTick", p.PricePerTick); sb.Append(',');
                AppendProp(sb, "ageSeconds", ageSeconds);
                sb.Append('}');
            }
            sb.Append("],");
            sb.Append("\"logs\":[");
            var start = Math.Max(0, logs.Count - 120);
            for (var i = start; i < logs.Count; i++)
            {
                if (i > start) sb.Append(',');
                AppendString(sb, logs[i]);
            }
            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendUiStrings(StringBuilder sb, string locale)
        {
            sb.Append("\"ui\":{");
            AppendProp(sb, "title", L(locale, "ui.title", "MultiSkyLines II")); sb.Append(',');
            AppendProp(sb, "close", L(locale, "ui.close", "Close")); sb.Append(',');
            AppendProp(sb, "tab_overview", L(locale, "ui.tab_overview", "Overview")); sb.Append(',');
            AppendProp(sb, "tab_contracts", L(locale, "ui.tab_contracts", "Contracts")); sb.Append(',');
            AppendProp(sb, "tab_debug", L(locale, "ui.tab_debug", "Debug")); sb.Append(',');
            AppendProp(sb, "session", L(locale, "ui.session", "Session")); sb.Append(',');
            AppendProp(sb, "local", L(locale, "ui.local", "Local")); sb.Append(',');
            AppendProp(sb, "active_contracts", L(locale, "ui.active_contracts", "Active contracts")); sb.Append(',');
            AppendProp(sb, "pending_proposals", L(locale, "ui.pending_proposals", "Pending proposals")); sb.Append(',');
            AppendProp(sb, "none_active_contracts", L(locale, "ui.none_active_contracts", "No active contracts.")); sb.Append(',');
            AppendProp(sb, "none_pending_proposals", L(locale, "ui.none_pending_proposals", "No pending proposals.")); sb.Append(',');
            AppendProp(sb, "cancel", L(locale, "ui.cancel", "Cancel")); sb.Append(',');
            AppendProp(sb, "accept", L(locale, "ui.accept", "Accept")); sb.Append(',');
            AppendProp(sb, "reject", L(locale, "ui.reject", "Reject")); sb.Append(',');
            AppendProp(sb, "public_offer_title", L(locale, "ui.public_offer_title", "Propose a public service offer")); sb.Append(',');
            AppendProp(sb, "public_offer_desc", L(locale, "ui.public_offer_desc", "Any interested player can accept.")); sb.Append(',');
            AppendProp(sb, "change_resource", L(locale, "ui.change_resource", "Change resource")); sb.Append(',');
            AppendProp(sb, "send_offer", L(locale, "ui.send_offer", "Send offer")); sb.Append(',');
            AppendProp(sb, "clear_logs", L(locale, "ui.clear_logs", "Clear logs")); sb.Append(',');
            AppendProp(sb, "ping_bridge", L(locale, "ui.ping_bridge", "Ping bridge")); sb.Append(',');
            AppendProp(sb, "no_network_logs", L(locale, "ui.no_network_logs", "No network logs.")); sb.Append(',');
            AppendProp(sb, "resource_electricity", L(locale, "ui.resource_electricity", "Electricity")); sb.Append(',');
            AppendProp(sb, "resource_water", L(locale, "ui.resource_water", "Water")); sb.Append(',');
            AppendProp(sb, "resource_sewage", L(locale, "ui.resource_sewage", "Sewage")); sb.Append(',');
            AppendProp(sb, "resource_unknown", L(locale, "ui.resource_unknown", "Unknown")); sb.Append(',');
            AppendProp(sb, "launcher_open", L(locale, "ui.launcher_open", "MS2 OPEN")); sb.Append(',');
            AppendProp(sb, "launcher_closed", L(locale, "ui.launcher_closed", "MS2"));
            sb.Append('}');
        }

        private static string L(string locale, string key, string fallback)
        {
            return LocalizationCatalog.GetText(locale, key, fallback);
        }

        private static void AppendState(StringBuilder sb, MultiplayerResourceState s)
        {
            sb.Append('{');
            AppendProp(sb, "name", s.Name); sb.Append(',');
            AppendProp(sb, "population", s.Population); sb.Append(',');
            AppendProp(sb, "money", s.Money); sb.Append(',');
            AppendProp(sb, "pingMs", s.PingMs); sb.Append(',');
            AppendProp(sb, "simSpeed", s.SimulationSpeed); sb.Append(',');
            sb.Append("\"isPaused\":").Append(s.IsPaused ? "true" : "false").Append(',');
            AppendProp(sb, "simDate", s.SimulationDateText); sb.Append(',');
            AppendProp(sb, "elecProd", s.ElectricityProduction); sb.Append(',');
            AppendProp(sb, "elecCons", s.ElectricityConsumption); sb.Append(',');
            AppendProp(sb, "elecServed", s.ElectricityFulfilledConsumption); sb.Append(',');
            AppendProp(sb, "waterCap", s.FreshWaterCapacity); sb.Append(',');
            AppendProp(sb, "waterCons", s.FreshWaterConsumption); sb.Append(',');
            AppendProp(sb, "waterServed", s.FreshWaterFulfilledConsumption); sb.Append(',');
            AppendProp(sb, "sewCap", s.SewageCapacity); sb.Append(',');
            AppendProp(sb, "sewCons", s.SewageConsumption); sb.Append(',');
            AppendProp(sb, "sewServed", s.SewageFulfilledConsumption); sb.Append(',');
            sb.Append("\"borderElec\":").Append(s.HasElectricityOutsideConnection ? "true" : "false").Append(',');
            sb.Append("\"borderWater\":").Append(s.HasWaterOutsideConnection ? "true" : "false").Append(',');
            sb.Append("\"borderSew\":").Append(s.HasSewageOutsideConnection ? "true" : "false");
            sb.Append('}');
        }

        private static void AppendProp(StringBuilder sb, string name, string value)
        {
            AppendString(sb, name);
            sb.Append(':');
            AppendString(sb, value ?? string.Empty);
        }

        private static void AppendProp(StringBuilder sb, string name, int value)
        {
            AppendString(sb, name);
            sb.Append(':').Append(value);
        }

        private static void AppendProp(StringBuilder sb, string name, long value)
        {
            AppendString(sb, name);
            sb.Append(':').Append(value);
        }

        private static void AppendString(StringBuilder sb, string value)
        {
            sb.Append('"');
            if (!string.IsNullOrEmpty(value))
            {
                for (var i = 0; i < value.Length; i++)
                {
                    var c = value[i];
                    switch (c)
                    {
                        case '\\': sb.Append("\\\\"); break;
                        case '"': sb.Append("\\\""); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 32)
                            {
                                sb.Append("\\u").Append(((int)c).ToString("x4"));
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        private void OnDestroy()
        {
            try
            {
                var bindingStore = GameManager.instance?.userInterface?.bindings;
                if (bindingStore != null)
                {
                    for (var i = 0; i < _bindings.Count; i++)
                    {
                        bindingStore.RemoveBinding(_bindings[i]);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _bindingsRegistered = false;
                _bindings.Clear();
                ModDiagnostics.Write("NativeUiBridge destroyed and bindings cleared.");
            }
        }
    }
}
