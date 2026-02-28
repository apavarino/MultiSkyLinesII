using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiSkyLineII
{
    public sealed class MultiplayerNetworkService : IDisposable
    {
        private const string FallbackPlayerName = "Unknown Player";
        private readonly Func<MultiplayerResourceState> _localStateProvider;

        private CancellationTokenSource _cts;
        private TcpListener _listener;
        private TcpClient _client;
        private readonly object _sync = new object();
        private readonly Dictionary<string, MultiplayerResourceState> _remoteStates = new Dictionary<string, MultiplayerResourceState>(StringComparer.OrdinalIgnoreCase);
        private readonly List<TcpClient> _hostClients = new List<TcpClient>();
        private readonly List<MultiplayerContract> _contracts = new List<MultiplayerContract>();
        private readonly List<MultiplayerContractProposal> _pendingProposals = new List<MultiplayerContractProposal>();
        private readonly Dictionary<string, int> _contractEffectiveUnits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _contractFailureCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _outboundMessages = new ConcurrentQueue<string>();
        private readonly List<string> _debugLog = new List<string>();
        private readonly List<SettlementEvent> _settlementHistory = new List<SettlementEvent>();
        private MultiplayerResourceState? _authoritativeLocalState;
        private MultiplayerResourceState _latestMainThreadLocalState;
        private bool _hasMainThreadLocalState;
        private int _pendingLocalMoneyDelta;
        private long _nextSettlementEventId = 1;
        private long _lastAppliedSettlementEventId;
        private DateTime _nextSettlementUtc = DateTime.UtcNow;
        private const int ProposalTimeoutSeconds = 120;
        private string _configuredBindAddress = "0.0.0.0";
        private string _configuredServerAddress = "127.0.0.1";
        private int _configuredPort = 25565;
        private const int MaxDebugLogEntries = 300;
        private const int MaxSettlementHistoryEntries = 300;
        private const int MaxSettlementSyncEntries = 40;
        private const int ContractCancelFailureThreshold = 3;

        private struct SettlementEvent
        {
            public long Id;
            public string SellerPlayer;
            public string BuyerPlayer;
            public int Payment;
        }

        private static string NormalizePlayerName(string playerName)
        {
            return string.IsNullOrWhiteSpace(playerName) ? FallbackPlayerName : playerName.Trim();
        }

        public bool IsRunning => _cts != null;
        public bool IsHost { get; private set; }
        public string DestinationEndpoint => IsHost
            ? $"{_configuredBindAddress}:{_configuredPort}"
            : $"{_configuredServerAddress}:{_configuredPort}";

        public IReadOnlyList<string> GetDebugLogLines()
        {
            lock (_sync)
            {
                return _debugLog.ToList();
            }
        }

        public void ClearDebugLog()
        {
            lock (_sync)
            {
                _debugLog.Clear();
            }
        }

        private void AddDebugLog(string message)
        {
            lock (_sync)
            {
                _debugLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
                if (_debugLog.Count > MaxDebugLogEntries)
                {
                    _debugLog.RemoveAt(0);
                }
            }
        }

        public MultiplayerNetworkService(Func<MultiplayerResourceState> localStateProvider)
        {
            _localStateProvider = localStateProvider ?? throw new ArgumentNullException(nameof(localStateProvider));
        }

        public void Restart(MultiplayerSettings settings)
        {
            Stop();
            Start(settings);
        }

        public void Start(MultiplayerSettings settings)
        {
            if (!settings.NetworkEnabled)
            {
                ModDiagnostics.Info("Multiplayer disabled in settings.");
                AddDebugLog("Network disabled by settings.");
                return;
            }

            if (settings.Port < 1 || settings.Port > 65535)
            {
                ModDiagnostics.Error($"Invalid port {settings.Port}. Expected 1..65535.");
                AddDebugLog($"Invalid port: {settings.Port}");
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            IsHost = settings.HostMode;
            _configuredBindAddress = string.IsNullOrWhiteSpace(settings.BindAddress) ? "0.0.0.0" : settings.BindAddress.Trim();
            _configuredServerAddress = string.IsNullOrWhiteSpace(settings.ServerAddress) ? "127.0.0.1" : settings.ServerAddress.Trim();
            _configuredPort = settings.Port;

            if (!settings.HostMode &&
                (string.Equals(settings.ServerAddress, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(settings.ServerAddress, "localhost", StringComparison.OrdinalIgnoreCase)))
            {
                ModDiagnostics.Warn("Client mode is targeting localhost. Use the host machine IP for remote multiplayer.");
                AddDebugLog("Warning: client targets localhost.");
            }

            if (settings.HostMode &&
                string.Equals(settings.BindAddress, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                ModDiagnostics.Warn("Host bind address is 127.0.0.1 (local only). Use 0.0.0.0 for LAN/WAN clients.");
                AddDebugLog("Warning: host bind is localhost only.");
            }

            if (settings.HostMode)
            {
                RunDetached("host-loop", () => RunHostLoop(_configuredBindAddress, _configuredPort, token), token);
                ModDiagnostics.Info($"Multiplayer host started on {_configuredBindAddress}:{_configuredPort}");
                AddDebugLog($"Host started on {_configuredBindAddress}:{_configuredPort}");
            }
            else
            {
                RunDetached("client-loop", () => RunClientLoop(_configuredServerAddress, _configuredPort, token), token);
                ModDiagnostics.Info($"Multiplayer client started for {_configuredServerAddress}:{_configuredPort}");
                AddDebugLog($"Client started for {_configuredServerAddress}:{_configuredPort}");
            }
        }

        private void RunDetached(string name, Func<Task> taskFactory, CancellationToken token)
        {
            _ = Task.Run(taskFactory, CancellationToken.None).ContinueWith(t =>
            {
                if (t.IsCanceled || token.IsCancellationRequested)
                    return;

                var ex = t.Exception?.GetBaseException();
                if (ex == null)
                    return;

                ModDiagnostics.Warn($"{name} faulted: {ex.Message}");
                AddDebugLog($"{name} faulted: {ex.Message}");
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        public void Stop()
        {
            lock (_sync)
            {
                try
                {
                    _cts?.Cancel();
                }
                catch (Exception e)
                {
                    ModDiagnostics.Warn($"Cancel failed: {e.Message}");
                }

                try
                {
                    _client?.Close();
                    _client?.Dispose();
                    foreach (var c in _hostClients)
                    {
                        try
                        {
                            c.Close();
                            c.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }
                catch (Exception e)
                {
                    ModDiagnostics.Warn($"Client shutdown failed: {e.Message}");
                }

                try
                {
                    _listener?.Stop();
                }
                catch (Exception e)
                {
                    ModDiagnostics.Warn($"Listener shutdown failed: {e.Message}");
                }

                _client = null;
                _listener = null;
                _cts?.Dispose();
                _cts = null;
                _remoteStates.Clear();
                _hostClients.Clear();
                _contracts.Clear();
                _pendingProposals.Clear();
                _contractEffectiveUnits.Clear();
                _contractFailureCounts.Clear();
                _settlementHistory.Clear();
                _authoritativeLocalState = null;
                _hasMainThreadLocalState = false;
                _pendingLocalMoneyDelta = 0;
                _nextSettlementEventId = 1;
                _lastAppliedSettlementEventId = 0;
                while (_outboundMessages.TryDequeue(out _)) { }
                IsHost = false;
                AddDebugLog("Network stopped.");
            }
        }

        private async Task RunHostLoop(string bindAddress, int port, CancellationToken token)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(bindAddress, out ip))
            {
                ModDiagnostics.Error($"Invalid host bind address: {bindAddress}");
                AddDebugLog($"Host loop aborted: invalid bind {bindAddress}");
                return;
            }

            _listener = new TcpListener(ip, port);
            _listener.Start();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var accepted = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    ModDiagnostics.Info("Client connected to host.");
                    AddDebugLog($"Peer connected: {accepted.Client.RemoteEndPoint}");
                    lock (_sync)
                    {
                        _hostClients.Add(accepted);
                    }
                    ReplaceClient(accepted);
                    _ = Task.Run(() => HandleConnectedClient(accepted, token), token);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                {
                    ModDiagnostics.Error($"Host loop crashed: {e}");
                    AddDebugLog($"Host loop error: {e.Message}");
                }
            }
        }

        private async Task RunClientLoop(string serverAddress, int port, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = new TcpClient();
                    AddDebugLog($"Client connecting to {serverAddress}:{port}...");
                    await client.ConnectAsync(serverAddress, port).ConfigureAwait(false);
                    ModDiagnostics.Info("Connected to host.");
                    AddDebugLog($"Client connected to {serverAddress}:{port}");
                    ReplaceClient(client);
                    await HandleConnectedClient(client, token).ConfigureAwait(false);
                }
                catch (SocketException e)
                {
                    if (!token.IsCancellationRequested)
                    {
                        ModDiagnostics.Warn($"Connection failed ({serverAddress}:{port}) [{e.SocketErrorCode}]: {e.Message}");
                        AddDebugLog($"Connect failed {serverAddress}:{port} [{e.SocketErrorCode}] - {e.Message}");
                    }
                }
                catch (Exception e)
                {
                    if (!token.IsCancellationRequested)
                    {
                        ModDiagnostics.Warn($"Connection failed ({serverAddress}:{port}): {e.Message}");
                        AddDebugLog($"Connect failed {serverAddress}:{port} - {e.Message}");
                    }
                }
                finally
                {
                    try
                    {
                        client?.Close();
                        client?.Dispose();
                    }
                    catch
                    {
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(5000, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task HandleConnectedClient(TcpClient client, CancellationToken token)
        {
            string remoteName = null;
            var pendingPings = new Dictionary<long, DateTime>();
            long pingSequence = 0;
            var currentRttMs = -1;
            try
            {
                AddDebugLog($"Session start with {client.Client.RemoteEndPoint}");
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true) { AutoFlush = true })
                {
                    await writer.WriteLineAsync(SerializeState(GetLocalState())).ConfigureAwait(false);
                    AddDebugLog("TX STATE (initial)");
                    Task<string> pendingReadTask = null;
                    var nextSyncUtc = DateTime.UtcNow;

                    while (!token.IsCancellationRequested && client.Connected)
                    {
                        while (_outboundMessages.TryDequeue(out var pending))
                        {
                            await writer.WriteLineAsync(pending).ConfigureAwait(false);
                        }

                        pendingReadTask ??= reader.ReadLineAsync();
                        var completed = await Task.WhenAny(pendingReadTask, Task.Delay(200, token)).ConfigureAwait(false);

                        if (completed == pendingReadTask)
                        {
                            var line = await pendingReadTask.ConfigureAwait(false);
                            pendingReadTask = null;
                            if (line == null)
                            {
                                ModDiagnostics.Info("Peer disconnected.");
                                AddDebugLog("Peer disconnected (EOF).");
                                break;
                            }

                            if (TryParsePingRequest(line, out var pingReqId))
                            {
                                await writer.WriteLineAsync(SerializePingResponse(pingReqId)).ConfigureAwait(false);
                            }
                            else if (TryParsePingResponse(line, out var pingRespId))
                            {
                                if (pendingPings.TryGetValue(pingRespId, out var sentAt))
                                {
                                    pendingPings.Remove(pingRespId);
                                    currentRttMs = Math.Max(0, (int)(DateTime.UtcNow - sentAt).TotalMilliseconds);
                                }
                            }
                            else if (TryParseState(line, out var remote))
                            {
                                remote.Name = NormalizePlayerName(remote.Name);
                                remoteName = remote.Name;
                                remote.PingMs = currentRttMs;
                                AddDebugLog($"RX STATE from {remote.Name} pop={remote.Population} money={remote.Money}");
                                lock (_sync)
                                {
                                    _remoteStates[remote.Name] = remote;
                                }
                            }
                            else if (TryParseSnapshot(line, out var snapshotStates))
                            {
                                AddDebugLog($"RX LIST snapshot entries={snapshotStates.Count}");
                                lock (_sync)
                                {
                                    _remoteStates.Clear();
                                    var localPlayerName = NormalizePlayerName(GetLocalState().Name);
                                    MultiplayerResourceState? authoritative = null;
                                    foreach (var state in snapshotStates)
                                    {
                                        var normalizedState = state;
                                        normalizedState.Name = NormalizePlayerName(normalizedState.Name);
                                        if (!IsHost && string.Equals(normalizedState.Name, localPlayerName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            authoritative = normalizedState;
                                            continue;
                                        }

                                        _remoteStates[normalizedState.Name] = normalizedState;
                                    }

                                    if (!IsHost)
                                    {
                                        _authoritativeLocalState = authoritative;
                                    }
                                }
                            }
                            else if (TryParseContracts(line, out var contracts))
                            {
                                AddDebugLog($"RX CONTRACTS count={contracts.Count}");
                                lock (_sync)
                                {
                                    _contracts.Clear();
                                    _contracts.AddRange(contracts);
                                }
                            }
                            else if (TryParseProposals(line, out var proposals))
                            {
                                AddDebugLog($"RX PROPOSALS count={proposals.Count}");
                                lock (_sync)
                                {
                                    _pendingProposals.Clear();
                                    _pendingProposals.AddRange(proposals);
                                }
                            }
                            else if (TryParseSettlements(line, out var settlements))
                            {
                                if (!IsHost && settlements.Count > 0)
                                {
                                    if (TryGetKnownLocalPlayerName(out var localPlayerName))
                                    {
                                        var totalDelta = 0;
                                        for (var i = 0; i < settlements.Count; i++)
                                        {
                                            var settlement = settlements[i];
                                            if (settlement.Id <= _lastAppliedSettlementEventId)
                                                continue;

                                            _lastAppliedSettlementEventId = settlement.Id;
                                            if (string.Equals(settlement.SellerPlayer, localPlayerName, StringComparison.OrdinalIgnoreCase))
                                            {
                                                totalDelta += settlement.Payment;
                                            }
                                            else if (string.Equals(settlement.BuyerPlayer, localPlayerName, StringComparison.OrdinalIgnoreCase))
                                            {
                                                totalDelta -= settlement.Payment;
                                            }
                                        }

                                        if (totalDelta != 0)
                                        {
                                            QueuePendingLocalMoneyDelta(totalDelta);
                                            AddDebugLog($"Queued real money delta from host settlements: {totalDelta}.");
                                        }
                                    }
                                }
                            }
                            else if (IsHost && TryParseContractRequest(line, out var proposal))
                            {
                                AddDebugLog($"RX CONTRACTREQ seller={proposal.SellerPlayer} buyer={(string.IsNullOrWhiteSpace(proposal.BuyerPlayer) ? "ANY" : proposal.BuyerPlayer)} units={proposal.UnitsPerTick}");
                                lock (_sync)
                                {
                                    if (TryGetStateByPlayer(proposal.SellerPlayer, out var sellerState) &&
                                        CanUseTransferInfrastructure(sellerState, proposal.Resource))
                                    {
                                        var canCreate = true;
                                        var normalizedBuyer = string.IsNullOrWhiteSpace(proposal.BuyerPlayer) ? string.Empty : proposal.BuyerPlayer;
                                        if (!string.IsNullOrWhiteSpace(normalizedBuyer))
                                        {
                                            canCreate = TryGetStateByPlayer(normalizedBuyer, out var buyerState) &&
                                                        CanUseTransferInfrastructure(buyerState, proposal.Resource);
                                        }

                                        if (canCreate)
                                        {
                                            _pendingProposals.Add(new MultiplayerContractProposal
                                            {
                                                Id = Guid.NewGuid().ToString("N"),
                                                SellerPlayer = sellerState.Name,
                                                BuyerPlayer = normalizedBuyer,
                                                Resource = proposal.Resource,
                                                UnitsPerTick = proposal.UnitsPerTick,
                                                PricePerTick = proposal.PricePerTick,
                                                CreatedUtc = DateTime.UtcNow
                                            });
                                        }
                                    }
                                }
                            }
                            else if (IsHost && TryParseContractDecision(line, out var decision))
                            {
                                AddDebugLog($"RX CONTRACTDECISION id={decision.ProposalId} actor={decision.ActorCity} accept={decision.Accept}");
                                lock (_sync)
                                {
                                    if (!TryApplyProposalDecision(decision.ProposalId, decision.ActorCity, decision.Accept, out var decisionError))
                                    {
                                        ModDiagnostics.Warn($"Contract decision ignored: {decisionError}");
                                    }
                                }
                            }
                            else if (IsHost && TryParseContractCancel(line, out var cancel))
                            {
                                AddDebugLog($"RX CONTRACTCANCEL id={cancel.ContractId} actor={cancel.ActorPlayer}");
                                lock (_sync)
                                {
                                    if (!TryCancelContractInternal(cancel.ContractId, cancel.ActorPlayer, out var cancelError))
                                    {
                                        AddDebugLog($"Contract cancel ignored: {cancelError}");
                                    }
                                }
                            }
                            else
                            {
                                ModDiagnostics.Info($"Net RX: {line}");
                            }
                        }
                        if (DateTime.UtcNow >= nextSyncUtc)
                        {
                            var pingId = ++pingSequence;
                            pendingPings[pingId] = DateTime.UtcNow;
                            await writer.WriteLineAsync(SerializePingRequest(pingId)).ConfigureAwait(false);
                            AddDebugLog($"TX PINGREQ id={pingId}");

                            if (IsHost)
                            {
                                await writer.WriteLineAsync(SerializeSnapshot()).ConfigureAwait(false);
                                await writer.WriteLineAsync(SerializeContracts()).ConfigureAwait(false);
                                await writer.WriteLineAsync(SerializeProposals()).ConfigureAwait(false);
                                await writer.WriteLineAsync(SerializeSettlements()).ConfigureAwait(false);
                                AddDebugLog("TX LIST/CONTRACTS/PROPOSALS/SETTLES");
                            }
                            else
                            {
                                await writer.WriteLineAsync(SerializeState(GetLocalState())).ConfigureAwait(false);
                                AddDebugLog("TX STATE (periodic)");
                            }

                            nextSyncUtc = DateTime.UtcNow.AddSeconds(2);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException e)
            {
                if (!token.IsCancellationRequested)
                {
                    ModDiagnostics.Info($"Peer socket closed: {e.Message}");
                    AddDebugLog($"Peer socket closed: {e.Message}");
                }
            }
            catch (SocketException e)
            {
                if (!token.IsCancellationRequested)
                {
                    ModDiagnostics.Info($"Peer socket error: {e.Message}");
                    AddDebugLog($"Peer socket error: {e.Message}");
                }
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                {
                    ModDiagnostics.Warn($"Client handler crashed: {e.Message}");
                    AddDebugLog($"Handler error: {e.Message}");
                }
            }
            finally
            {
                AddDebugLog("Session end.");
                lock (_sync)
                {
                    if (IsHost)
                    {
                        _hostClients.Remove(client);
                        if (!string.IsNullOrWhiteSpace(remoteName))
                        {
                            _remoteStates.Remove(remoteName);
                        }
                    }
                }
            }
        }

        private void ReplaceClient(TcpClient nextClient)
        {
            lock (_sync)
            {
                if (_client != null && !ReferenceEquals(_client, nextClient))
                {
                    try
                    {
                        _client.Close();
                        _client.Dispose();
                    }
                    catch
                    {
                    }
                }

                _client = nextClient;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        public MultiplayerResourceState GetLocalState()
        {
            lock (_sync)
            {
                if (_hasMainThreadLocalState)
                    return _latestMainThreadLocalState;
            }
            return _localStateProvider();
        }

        public void CaptureLocalStateOnMainThread()
        {
            var stateBeforeApply = _localStateProvider();
            var localPlayerName = NormalizePlayerName(stateBeforeApply.Name);
            var pendingDelta = 0;
            var targetElectricityCapacityDelta = 0;
            var targetWaterCapacityDelta = 0;
            var targetSewageCapacityDelta = 0;
            lock (_sync)
            {
                pendingDelta = _pendingLocalMoneyDelta;
                _pendingLocalMoneyDelta = 0;
                targetElectricityCapacityDelta = GetLocalTargetCapacityDelta(localPlayerName, MultiplayerContractResource.Electricity);
                targetWaterCapacityDelta = GetLocalTargetCapacityDelta(localPlayerName, MultiplayerContractResource.FreshWater);
                targetSewageCapacityDelta = GetLocalTargetCapacityDelta(localPlayerName, MultiplayerContractResource.Sewage);
            }

            if (pendingDelta != 0)
            {
                if (MultiplayerResourceReader.TryApplyMoneyDelta(pendingDelta, out var appliedDelta))
                {
                    AddDebugLog($"Applied real money delta {appliedDelta} (requested {pendingDelta}).");
                    if (appliedDelta != pendingDelta)
                    {
                        QueuePendingLocalMoneyDelta(pendingDelta - appliedDelta);
                    }
                }
                else
                {
                    QueuePendingLocalMoneyDelta(pendingDelta);
                }
            }

            var hasUtilityTargets = targetElectricityCapacityDelta != 0 || targetWaterCapacityDelta != 0 || targetSewageCapacityDelta != 0;
            if (hasUtilityTargets || MultiplayerResourceReader.HasUtilityOverrides())
            {
                if (!hasUtilityTargets)
                {
                    AddDebugLog("Utility target apply reconciliation to zero (clearing previous overrides).");
                }
                if (MultiplayerResourceReader.TrySetUtilityCapacityTarget(
                        targetElectricityCapacityDelta,
                        targetWaterCapacityDelta,
                        targetSewageCapacityDelta,
                        out var appliedElectricity,
                        out var appliedWater,
                        out var appliedSewage))
                {
                    AddDebugLog($"Utility target apply elec={targetElectricityCapacityDelta} water={targetWaterCapacityDelta} sewage={targetSewageCapacityDelta} -> delta elec={appliedElectricity} water={appliedWater} sewage={appliedSewage}");
                    if (appliedElectricity != 0 || appliedWater != 0 || appliedSewage != 0)
                    {
                        AddDebugLog($"Applied real utility delta elec={appliedElectricity} water={appliedWater} sewage={appliedSewage}");
                    }
                }
                else
                {
                    AddDebugLog($"Utility apply failed for target elec={targetElectricityCapacityDelta} water={targetWaterCapacityDelta} sewage={targetSewageCapacityDelta}");
                }
            }

            var state = _localStateProvider();
            lock (_sync)
            {
                _latestMainThreadLocalState = state;
                _hasMainThreadLocalState = true;
            }
        }

        public IReadOnlyList<MultiplayerContract> GetActiveContracts()
        {
            lock (_sync)
            {
                return _contracts
                    .OrderBy(c => c.CreatedUtc)
                    .ToList();
            }
        }

        public IReadOnlyList<MultiplayerContractProposal> GetPendingProposals()
        {
            lock (_sync)
            {
                CleanupExpiredProposals(DateTime.UtcNow);
                return _pendingProposals
                    .OrderBy(p => p.CreatedUtc)
                    .ToList();
            }
        }

        public bool TryRespondToProposal(string proposalId, bool accept, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(proposalId))
            {
                error = "Proposition invalide.";
                return false;
            }

            var actorCity = GetLocalState().Name;
            if (IsHost)
            {
                lock (_sync)
                {
                    AddDebugLog($"Local decision proposal={proposalId} accept={accept}");
                    return TryApplyProposalDecision(proposalId, actorCity, accept, out error);
                }
            }

            _outboundMessages.Enqueue(SerializeContractDecision(proposalId, actorCity, accept));
            AddDebugLog($"Queued CONTRACTDECISION proposal={proposalId} actor={actorCity} accept={accept}");
            return true;
        }

        public bool TryProposeContract(string sellerPlayer, string buyerPlayer, MultiplayerContractResource resource, int unitsPerTick, int pricePerTick, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(sellerPlayer))
            {
                error = "Joueur invalide.";
                return false;
            }

            sellerPlayer = NormalizePlayerName(sellerPlayer);
            var hasTargetBuyer = !string.IsNullOrWhiteSpace(buyerPlayer);
            if (hasTargetBuyer)
            {
                buyerPlayer = NormalizePlayerName(buyerPlayer);
                if (string.Equals(sellerPlayer, buyerPlayer, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Le vendeur et l'acheteur doivent etre differents.";
                    return false;
                }
            }
            else
            {
                buyerPlayer = string.Empty;
            }

            if (unitsPerTick <= 0 || pricePerTick <= 0)
            {
                error = "Parametres de contrat invalides.";
                return false;
            }

            if (IsHost)
            {
                lock (_sync)
                {
                    var now = DateTime.UtcNow;
                    if (!TryGetStateByPlayer(sellerPlayer, out var sellerState))
                    {
                        error = "Joueur vendeur introuvable.";
                        return false;
                    }

                    if (!CanUseTransferInfrastructure(sellerState, resource))
                    {
                        error = "Contrat impossible: infrastructure reseau requise pour le vendeur.";
                        return false;
                    }

                    if (hasTargetBuyer)
                    {
                        if (!TryGetStateByPlayer(buyerPlayer, out var buyerState))
                        {
                            error = "Joueur acheteur introuvable.";
                            return false;
                        }

                        if (!CanUseTransferInfrastructure(buyerState, resource))
                        {
                            error = "Contrat impossible: infrastructure reseau requise pour l'acheteur.";
                            return false;
                        }
                    }

                    var maxExportable = GetSellerAvailable(resource, sellerState);
                    if (maxExportable <= 0)
                    {
                        error = "Le vendeur n'a aucun surplus exportable.";
                        return false;
                    }

                    if (unitsPerTick > maxExportable)
                    {
                        error = $"Quantite trop elevee. Max exportable actuel: {maxExportable}.";
                        return false;
                    }

                    var proposal = new MultiplayerContractProposal
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        SellerPlayer = sellerState.Name,
                        BuyerPlayer = buyerPlayer,
                        Resource = resource,
                        UnitsPerTick = unitsPerTick,
                        PricePerTick = pricePerTick,
                        CreatedUtc = now
                    };
                    _pendingProposals.Add(proposal);
                    AddDebugLog($"Local proposal queued seller={proposal.SellerPlayer} buyer={(string.IsNullOrWhiteSpace(proposal.BuyerPlayer) ? "ANY" : proposal.BuyerPlayer)} units={proposal.UnitsPerTick}");
                }

                return true;
            }

            if (!TryGetStateByPlayer(sellerPlayer, out var clientSellerState))
            {
                error = "Joueur vendeur introuvable.";
                return false;
            }

            if (!CanUseTransferInfrastructure(clientSellerState, resource))
            {
                error = "Contrat impossible: infrastructure reseau requise pour le vendeur.";
                return false;
            }

            if (hasTargetBuyer)
            {
                if (!TryGetStateByPlayer(buyerPlayer, out var clientBuyerState))
                {
                    error = "Joueur acheteur introuvable.";
                    return false;
                }

                if (!CanUseTransferInfrastructure(clientBuyerState, resource))
                {
                    error = "Contrat impossible: infrastructure reseau requise pour l'acheteur.";
                    return false;
                }
            }

            var clientMaxExportable = GetSellerAvailable(resource, clientSellerState);
            if (clientMaxExportable <= 0)
            {
                error = "Le vendeur n'a aucun surplus exportable.";
                return false;
            }

            if (unitsPerTick > clientMaxExportable)
            {
                error = $"Quantite trop elevee. Max exportable actuel: {clientMaxExportable}.";
                return false;
            }

            var request = SerializeContractRequest(sellerPlayer, buyerPlayer, resource, unitsPerTick, pricePerTick);
            _outboundMessages.Enqueue(request);
            AddDebugLog($"Queued CONTRACTREQ seller={sellerPlayer} buyer={(string.IsNullOrWhiteSpace(buyerPlayer) ? "ANY" : buyerPlayer)} units={unitsPerTick}");
            return true;
        }

        public bool TryCancelContract(string contractId, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(contractId))
            {
                error = "Contrat invalide.";
                return false;
            }

            var actor = GetLocalState().Name;
            if (IsHost)
            {
                lock (_sync)
                {
                    return TryCancelContractInternal(contractId, actor, out error);
                }
            }

            _outboundMessages.Enqueue(SerializeContractCancel(contractId, actor));
            AddDebugLog($"Queued CONTRACTCANCEL id={contractId} actor={actor}");
            return true;
        }

        public IReadOnlyList<MultiplayerResourceState> GetConnectedStates()
        {
            lock (_sync)
            {
                if (IsHost)
                {
                    ApplyContractSettlementsIfDue(DateTime.UtcNow);
                }

                var local = IsHost
                    ? GetLocalState()
                    : (_authoritativeLocalState ?? GetLocalState());
                local.Name = NormalizePlayerName(local.Name);

                var result = new List<MultiplayerResourceState> { local };
                foreach (var kvp in _remoteStates)
                {
                    if (!string.Equals(kvp.Key, local.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(kvp.Value);
                    }
                }
                return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        private static string SerializeState(MultiplayerResourceState state)
        {
            return MultiplayerProtocolCodec.SerializeState(state);
        }

        private static bool TryParseState(string line, out MultiplayerResourceState state)
        {
            return MultiplayerProtocolCodec.TryParseState(line, out state);
        }

        private string SerializeSnapshot()
        {
            return MultiplayerProtocolCodec.SerializeSnapshot(GetConnectedStates());
        }

        private static bool TryParseSnapshot(string line, out List<MultiplayerResourceState> states)
        {
            return MultiplayerProtocolCodec.TryParseSnapshot(line, out states);
        }

        private string SerializeContracts()
        {
            return MultiplayerProtocolCodec.SerializeContracts(_contracts);
        }

        private static bool TryParseContracts(string line, out List<MultiplayerContract> contracts)
        {
            return MultiplayerProtocolCodec.TryParseContracts(line, out contracts);
        }

        private string SerializeProposals()
        {
            var now = DateTime.UtcNow;
            CleanupExpiredProposals(now);
            return MultiplayerProtocolCodec.SerializeProposals(_pendingProposals);
        }

        private static bool TryParseProposals(string line, out List<MultiplayerContractProposal> proposals)
        {
            return MultiplayerProtocolCodec.TryParseProposals(line, out proposals);
        }

        private string SerializeSettlements()
        {
            var settlements = _settlementHistory
                .Select(s => new MultiplayerProtocolCodec.SettlementSyncEvent
                {
                    Id = s.Id,
                    SellerPlayer = s.SellerPlayer,
                    BuyerPlayer = s.BuyerPlayer,
                    Payment = s.Payment
                })
                .ToList();
            return MultiplayerProtocolCodec.SerializeSettlements(settlements, MaxSettlementSyncEntries);
        }

        private static bool TryParseSettlements(string line, out List<SettlementEvent> settlements)
        {
            settlements = null;
            if (!MultiplayerProtocolCodec.TryParseSettlements(line, out var parsed))
                return false;

            settlements = parsed
                .Select(s => new SettlementEvent
                {
                    Id = s.Id,
                    SellerPlayer = s.SellerPlayer,
                    BuyerPlayer = s.BuyerPlayer,
                    Payment = s.Payment
                })
                .ToList();
            return true;
        }

        private struct ContractDecision
        {
            public string ProposalId;
            public string ActorCity;
            public bool Accept;
        }

        private struct ContractCancel
        {
            public string ContractId;
            public string ActorPlayer;
        }

        private static string SerializeContractDecision(string proposalId, string actorCity, bool accept)
        {
            return MultiplayerProtocolCodec.SerializeContractDecision(proposalId, actorCity, accept);
        }

        private static bool TryParseContractDecision(string line, out ContractDecision decision)
        {
            decision = default;
            if (!MultiplayerProtocolCodec.TryParseContractDecision(line, out var parsed))
                return false;

            decision = new ContractDecision
            {
                ProposalId = parsed.ProposalId,
                ActorCity = parsed.ActorCity,
                Accept = parsed.Accept
            };
            return true;
        }

        private static string SerializeContractCancel(string contractId, string actorPlayer)
        {
            return MultiplayerProtocolCodec.SerializeContractCancel(contractId, actorPlayer);
        }

        private static bool TryParseContractCancel(string line, out ContractCancel cancel)
        {
            cancel = default;
            if (!MultiplayerProtocolCodec.TryParseContractCancel(line, out var parsed))
                return false;

            cancel = new ContractCancel
            {
                ContractId = parsed.ContractId,
                ActorPlayer = parsed.ActorPlayer
            };
            return true;
        }

        private struct ContractProposal
        {
            public string SellerPlayer;
            public string BuyerPlayer;
            public MultiplayerContractResource Resource;
            public int UnitsPerTick;
            public int PricePerTick;
        }

        private static string SerializeContractRequest(string sellerPlayer, string buyerPlayer, MultiplayerContractResource resource, int unitsPerTick, int pricePerTick)
        {
            return MultiplayerProtocolCodec.SerializeContractRequest(sellerPlayer, buyerPlayer, resource, unitsPerTick, pricePerTick);
        }

        private static bool TryParseContractRequest(string line, out ContractProposal proposal)
        {
            proposal = default;
            if (!MultiplayerProtocolCodec.TryParseContractRequest(line, out var parsed))
                return false;

            proposal = new ContractProposal
            {
                SellerPlayer = parsed.SellerPlayer,
                BuyerPlayer = parsed.BuyerPlayer,
                Resource = parsed.Resource,
                UnitsPerTick = parsed.UnitsPerTick,
                PricePerTick = parsed.PricePerTick
            };
            return true;
        }

        private void CleanupExpiredProposals(DateTime nowUtc)
        {
            MultiplayerContractRules.CleanupExpiredProposals(_pendingProposals, nowUtc, ProposalTimeoutSeconds);
        }

        private bool TryApplyProposalDecision(string proposalId, string actorCity, bool accept, out string error)
        {
            return MultiplayerContractRules.TryApplyProposalDecision(
                _pendingProposals,
                _contracts,
                proposalId,
                actorCity,
                accept,
                NormalizePlayerName,
                playerName => TryGetStateByPlayer(playerName, out _),
                AddDebugLog,
                ProposalTimeoutSeconds,
                out error);
        }

        private bool TryCancelContractInternal(string contractId, string actorPlayer, out string error)
        {
            return MultiplayerContractRules.TryCancelContract(
                _contracts,
                _contractFailureCounts,
                contractId,
                actorPlayer,
                AddDebugLog,
                out error);
        }

        private bool TryGetStateByPlayer(string playerName, out MultiplayerResourceState state)
        {
            if (string.IsNullOrWhiteSpace(playerName))
            {
                state = default;
                return false;
            }

            playerName = NormalizePlayerName(playerName);
            var local = GetLocalState();
            local.Name = NormalizePlayerName(local.Name);
            if (string.Equals(local.Name, playerName, StringComparison.OrdinalIgnoreCase))
            {
                state = local;
                return true;
            }

            if (_remoteStates.TryGetValue(playerName, out var remote))
            {
                state = remote;
                return true;
            }

            state = default;
            return false;
        }

        private void ApplyContractSettlementsIfDue(DateTime nowUtc)
        {
            var context = new MultiplayerSettlementProcessor.Context
            {
                Contracts = _contracts,
                PendingProposals = _pendingProposals,
                RemoteStates = _remoteStates,
                ContractEffectiveUnits = _contractEffectiveUnits,
                ContractFailureCounts = _contractFailureCounts,
                NextSettlementUtc = _nextSettlementUtc,
                ContractCancelFailureThreshold = ContractCancelFailureThreshold,
                GetLocalState = GetLocalState,
                NormalizePlayerName = NormalizePlayerName,
                CanUseTransferInfrastructure = CanUseTransferInfrastructure,
                GetSellerAvailable = GetSellerAvailable,
                GetCommittedOutgoingUnits = GetCommittedOutgoingUnits,
                AddDebugLog = AddDebugLog,
                QueuePendingLocalMoneyDelta = QueuePendingLocalMoneyDelta,
                RecordSettlementEvent = RecordSettlementEvent,
                CleanupExpiredProposals = CleanupExpiredProposals
            };

            MultiplayerSettlementProcessor.ApplyIfDue(nowUtc, context);
            _nextSettlementUtc = context.NextSettlementUtc;
        }

        private static int GetSellerAvailable(MultiplayerContractResource resource, MultiplayerResourceState state)
        {
            return MultiplayerContractRules.GetSellerAvailable(resource, state);
        }

        private static bool CanUseTransferInfrastructure(MultiplayerResourceState state, MultiplayerContractResource resource)
        {
            return MultiplayerContractRules.CanUseTransferInfrastructure(state, resource);
        }

        private int GetCommittedOutgoingUnits(string sellerPlayer, MultiplayerContractResource resource)
        {
            return MultiplayerContractRules.GetCommittedOutgoingUnits(_contracts, sellerPlayer, resource, NormalizePlayerName);
        }

        private void QueuePendingLocalMoneyDelta(int delta)
        {
            if (delta == 0)
                return;

            lock (_sync)
            {
                _pendingLocalMoneyDelta += delta;
            }
        }

        private bool TryGetKnownLocalPlayerName(out string playerName)
        {
            lock (_sync)
            {
                if (_hasMainThreadLocalState)
                {
                    playerName = NormalizePlayerName(_latestMainThreadLocalState.Name);
                    return true;
                }

                if (_authoritativeLocalState.HasValue)
                {
                    playerName = NormalizePlayerName(_authoritativeLocalState.Value.Name);
                    return true;
                }
            }

            playerName = null;
            return false;
        }

        private void RecordSettlementEvent(string sellerPlayer, string buyerPlayer, int payment)
        {
            if (payment <= 0)
                return;

            _settlementHistory.Add(new SettlementEvent
            {
                Id = _nextSettlementEventId++,
                SellerPlayer = NormalizePlayerName(sellerPlayer),
                BuyerPlayer = NormalizePlayerName(buyerPlayer),
                Payment = payment
            });

            if (_settlementHistory.Count > MaxSettlementHistoryEntries)
            {
                _settlementHistory.RemoveRange(0, _settlementHistory.Count - MaxSettlementHistoryEntries);
            }
        }

        private int GetLocalTargetCapacityDelta(string localPlayerName, MultiplayerContractResource resource)
        {
            return MultiplayerContractRules.GetLocalTargetCapacityDelta(_contracts, localPlayerName, resource, NormalizePlayerName);
        }

        private static string SerializePingRequest(long id)
        {
            return MultiplayerProtocolCodec.SerializePingRequest(id);
        }

        private static string SerializePingResponse(long id)
        {
            return MultiplayerProtocolCodec.SerializePingResponse(id);
        }

        private static bool TryParsePingRequest(string line, out long id)
        {
            return MultiplayerProtocolCodec.TryParsePingRequest(line, out id);
        }

        private static bool TryParsePingResponse(string line, out long id)
        {
            return MultiplayerProtocolCodec.TryParsePingResponse(line, out id);
        }
    }
}




