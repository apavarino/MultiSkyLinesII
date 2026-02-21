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
using Colossal.Logging;

namespace MultiSkyLineII
{
    public sealed class MultiplayerNetworkService : IDisposable
    {
        private const string FallbackPlayerName = "Unknown Player";
        private readonly ILog _log;
        private readonly Func<MultiplayerResourceState> _localStateProvider;

        private CancellationTokenSource _cts;
        private TcpListener _listener;
        private TcpClient _client;
        private readonly object _sync = new object();
        private readonly Dictionary<string, MultiplayerResourceState> _remoteStates = new Dictionary<string, MultiplayerResourceState>(StringComparer.OrdinalIgnoreCase);
        private readonly List<TcpClient> _hostClients = new List<TcpClient>();
        private readonly List<MultiplayerContract> _contracts = new List<MultiplayerContract>();
        private readonly List<MultiplayerContractProposal> _pendingProposals = new List<MultiplayerContractProposal>();
        private readonly Dictionary<string, int> _moneyAdjustments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ResourceDelta> _resourceAdjustments = new Dictionary<string, ResourceDelta>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _outboundMessages = new ConcurrentQueue<string>();
        private readonly List<string> _debugLog = new List<string>();
        private MultiplayerResourceState? _authoritativeLocalState;
        private MultiplayerResourceState _latestMainThreadLocalState;
        private bool _hasMainThreadLocalState;
        private DateTime _nextSettlementUtc = DateTime.UtcNow;
        private const int ProposalTimeoutSeconds = 120;
        private string _configuredBindAddress = "0.0.0.0";
        private string _configuredServerAddress = "127.0.0.1";
        private int _configuredPort = 25565;
        private const int MaxDebugLogEntries = 300;

        private struct ResourceDelta
        {
            public int ElectricityProduction;
            public int ElectricityFulfilled;
            public int WaterCapacity;
            public int WaterFulfilled;
            public int SewageCapacity;
            public int SewageFulfilled;
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

        public MultiplayerNetworkService(ILog log, Func<MultiplayerResourceState> localStateProvider)
        {
            _log = log;
            _localStateProvider = localStateProvider;
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
                _log.Info("Multiplayer disabled in settings.");
                AddDebugLog("Network disabled by settings.");
                return;
            }

            if (settings.Port < 1 || settings.Port > 65535)
            {
                _log.Error($"Invalid port {settings.Port}. Expected 1..65535.");
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
                _log.Warn("Client mode is targeting localhost. Use the host machine IP for remote multiplayer.");
                AddDebugLog("Warning: client targets localhost.");
            }

            if (settings.HostMode &&
                string.Equals(settings.BindAddress, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn("Host bind address is 127.0.0.1 (local only). Use 0.0.0.0 for LAN/WAN clients.");
                AddDebugLog("Warning: host bind is localhost only.");
            }

            if (settings.HostMode)
            {
                Task.Run(() => RunHostLoop(_configuredBindAddress, _configuredPort, token), token);
                _log.Info($"Multiplayer host started on {_configuredBindAddress}:{_configuredPort}");
                AddDebugLog($"Host started on {_configuredBindAddress}:{_configuredPort}");
            }
            else
            {
                Task.Run(() => RunClientLoop(_configuredServerAddress, _configuredPort, token), token);
                _log.Info($"Multiplayer client started for {_configuredServerAddress}:{_configuredPort}");
                AddDebugLog($"Client started for {_configuredServerAddress}:{_configuredPort}");
            }
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
                    _log.Warn($"Cancel failed: {e.Message}");
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
                    _log.Warn($"Client shutdown failed: {e.Message}");
                }

                try
                {
                    _listener?.Stop();
                }
                catch (Exception e)
                {
                    _log.Warn($"Listener shutdown failed: {e.Message}");
                }

                _client = null;
                _listener = null;
                _cts?.Dispose();
                _cts = null;
                _remoteStates.Clear();
                _hostClients.Clear();
                _contracts.Clear();
                _pendingProposals.Clear();
                _moneyAdjustments.Clear();
                _resourceAdjustments.Clear();
                _authoritativeLocalState = null;
                _hasMainThreadLocalState = false;
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
                _log.Error($"Invalid host bind address: {bindAddress}");
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
                    _log.Info("Client connected to host.");
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
                    _log.Error($"Host loop crashed: {e}");
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
                    _log.Info("Connected to host.");
                    AddDebugLog($"Client connected to {serverAddress}:{port}");
                    ReplaceClient(client);
                    await HandleConnectedClient(client, token).ConfigureAwait(false);
                }
                catch (SocketException e)
                {
                    if (!token.IsCancellationRequested)
                    {
                        _log.Warn($"Connection failed ({serverAddress}:{port}) [{e.SocketErrorCode}]: {e.Message}");
                        AddDebugLog($"Connect failed {serverAddress}:{port} [{e.SocketErrorCode}] - {e.Message}");
                    }
                }
                catch (Exception e)
                {
                    if (!token.IsCancellationRequested)
                    {
                        _log.Warn($"Connection failed ({serverAddress}:{port}): {e.Message}");
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
                    await Task.Delay(5000, token).ConfigureAwait(false);
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
                            var line = pendingReadTask.Result;
                            pendingReadTask = null;
                            if (line == null)
                            {
                                _log.Info("Peer disconnected.");
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
                            else if (IsHost && TryParseContractRequest(line, out var proposal))
                            {
                                AddDebugLog($"RX CONTRACTREQ seller={proposal.SellerPlayer} buyer={proposal.BuyerPlayer} units={proposal.UnitsPerTick}");
                                lock (_sync)
                                {
                                    if (TryGetStateByPlayer(proposal.SellerPlayer, out var sellerState) &&
                                        TryGetStateByPlayer(proposal.BuyerPlayer, out _))
                                    {
                                    _pendingProposals.Add(new MultiplayerContractProposal
                                    {
                                        Id = Guid.NewGuid().ToString("N"),
                                        SellerPlayer = sellerState.Name,
                                        BuyerPlayer = proposal.BuyerPlayer,
                                        Resource = proposal.Resource,
                                        UnitsPerTick = proposal.UnitsPerTick,
                                        PricePerUnit = proposal.PricePerUnit,
                                        CreatedUtc = DateTime.UtcNow
                                    });
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
                                        _log.Warn($"Contract decision ignored: {decisionError}");
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
                                _log.Info($"Net RX: {line}");
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
                                AddDebugLog("TX LIST/CONTRACTS/PROPOSALS");
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
                    _log.Info($"Peer socket closed: {e.Message}");
                    AddDebugLog($"Peer socket closed: {e.Message}");
                }
            }
            catch (SocketException e)
            {
                if (!token.IsCancellationRequested)
                {
                    _log.Info($"Peer socket error: {e.Message}");
                    AddDebugLog($"Peer socket error: {e.Message}");
                }
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                {
                    _log.Warn($"Client handler crashed: {e.Message}");
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

        public bool TryProposeContract(string sellerPlayer, string buyerPlayer, MultiplayerContractResource resource, int unitsPerTick, int pricePerUnit, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(sellerPlayer) || string.IsNullOrWhiteSpace(buyerPlayer))
            {
                error = "Joueur invalide.";
                return false;
            }

            sellerPlayer = NormalizePlayerName(sellerPlayer);
            buyerPlayer = NormalizePlayerName(buyerPlayer);

            if (string.Equals(sellerPlayer, buyerPlayer, StringComparison.OrdinalIgnoreCase))
            {
                error = "Le vendeur et l'acheteur doivent etre differents.";
                return false;
            }

            if (unitsPerTick <= 0 || pricePerUnit <= 0)
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

                    if (!TryGetStateByPlayer(buyerPlayer, out _))
                    {
                        error = "Joueur acheteur introuvable.";
                        return false;
                    }

                    var proposal = new MultiplayerContractProposal
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        SellerPlayer = sellerState.Name,
                        BuyerPlayer = buyerPlayer,
                        Resource = resource,
                        UnitsPerTick = unitsPerTick,
                        PricePerUnit = pricePerUnit,
                        CreatedUtc = now
                    };
                    _pendingProposals.Add(proposal);
                    AddDebugLog($"Local proposal queued seller={proposal.SellerPlayer} buyer={proposal.BuyerPlayer} units={proposal.UnitsPerTick}");
                }

                return true;
            }

            var request = SerializeContractRequest(sellerPlayer, buyerPlayer, resource, unitsPerTick, pricePerUnit);
            _outboundMessages.Enqueue(request);
            AddDebugLog($"Queued CONTRACTREQ seller={sellerPlayer} buyer={buyerPlayer} units={unitsPerTick}");
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
                    ? ApplyAdjustments(GetLocalState())
                    : (_authoritativeLocalState ?? GetLocalState());
                local.Name = NormalizePlayerName(local.Name);

                var result = new List<MultiplayerResourceState> { local };
                foreach (var kvp in _remoteStates)
                {
                    if (!string.Equals(kvp.Key, local.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(IsHost ? ApplyAdjustments(kvp.Value) : kvp.Value);
                    }
                }
                return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        private static string SerializeState(MultiplayerResourceState state)
        {
            var encoded = Uri.EscapeDataString(NormalizePlayerName(state.Name));
            return $"STATE|{encoded}|{state.Money}|{state.Population}|{state.ElectricityProduction}|{state.ElectricityConsumption}|{state.ElectricityFulfilledConsumption}|{state.FreshWaterCapacity}|{state.FreshWaterConsumption}|{state.FreshWaterFulfilledConsumption}|{state.SewageCapacity}|{state.SewageConsumption}|{state.SewageFulfilledConsumption}|{state.PingMs}";
        }

        private static bool TryParseState(string line, out MultiplayerResourceState state)
        {
            state = default;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length != 14 || !string.Equals(parts[0], "STATE", StringComparison.Ordinal))
                return false;

            if (!int.TryParse(parts[2], out var money) ||
                !int.TryParse(parts[3], out var population) ||
                !int.TryParse(parts[4], out var electricityProduction) ||
                !int.TryParse(parts[5], out var electricityConsumption) ||
                !int.TryParse(parts[6], out var electricityFulfilledConsumption) ||
                !int.TryParse(parts[7], out var freshWaterCapacity) ||
                !int.TryParse(parts[8], out var freshWaterConsumption) ||
                !int.TryParse(parts[9], out var freshWaterFulfilledConsumption) ||
                !int.TryParse(parts[10], out var sewageCapacity) ||
                !int.TryParse(parts[11], out var sewageConsumption) ||
                !int.TryParse(parts[12], out var sewageFulfilledConsumption) ||
                !int.TryParse(parts[13], out var pingMs))
                return false;

            state = new MultiplayerResourceState
            {
                Name = NormalizePlayerName(Uri.UnescapeDataString(parts[1])),
                Money = money,
                Population = population,
                ElectricityProduction = electricityProduction,
                ElectricityConsumption = electricityConsumption,
                ElectricityFulfilledConsumption = electricityFulfilledConsumption,
                FreshWaterCapacity = freshWaterCapacity,
                FreshWaterConsumption = freshWaterConsumption,
                FreshWaterFulfilledConsumption = freshWaterFulfilledConsumption,
                SewageCapacity = sewageCapacity,
                SewageConsumption = sewageConsumption,
                SewageFulfilledConsumption = sewageFulfilledConsumption,
                PingMs = pingMs,
                TimestampUtc = DateTime.UtcNow
            };
            return true;
        }

        private string SerializeSnapshot()
        {
            var states = GetConnectedStates();
            var entries = states
                .Select(s => $"{Uri.EscapeDataString(s.Name ?? "Unknown")},{s.Money},{s.Population},{s.ElectricityProduction},{s.ElectricityConsumption},{s.ElectricityFulfilledConsumption},{s.FreshWaterCapacity},{s.FreshWaterConsumption},{s.FreshWaterFulfilledConsumption},{s.SewageCapacity},{s.SewageConsumption},{s.SewageFulfilledConsumption},{s.PingMs}")
                .ToArray();
            return "LIST|" + string.Join("|", entries);
        }

        private static bool TryParseSnapshot(string line, out List<MultiplayerResourceState> states)
        {
            states = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length < 2 || !string.Equals(parts[0], "LIST", StringComparison.Ordinal))
                return false;

            var parsed = new List<MultiplayerResourceState>();
            for (var i = 1; i < parts.Length; i++)
            {
                var entry = parts[i].Split(',');
                if (entry.Length != 13)
                    continue;

                if (!int.TryParse(entry[1], out var money) ||
                    !int.TryParse(entry[2], out var population) ||
                    !int.TryParse(entry[3], out var electricityProduction) ||
                    !int.TryParse(entry[4], out var electricityConsumption) ||
                    !int.TryParse(entry[5], out var electricityFulfilledConsumption) ||
                    !int.TryParse(entry[6], out var freshWaterCapacity) ||
                    !int.TryParse(entry[7], out var freshWaterConsumption) ||
                    !int.TryParse(entry[8], out var freshWaterFulfilledConsumption) ||
                    !int.TryParse(entry[9], out var sewageCapacity) ||
                    !int.TryParse(entry[10], out var sewageConsumption) ||
                    !int.TryParse(entry[11], out var sewageFulfilledConsumption) ||
                    !int.TryParse(entry[12], out var pingMs))
                    continue;

                parsed.Add(new MultiplayerResourceState
                {
                    Name = NormalizePlayerName(Uri.UnescapeDataString(entry[0])),
                    Money = money,
                    Population = population,
                    ElectricityProduction = electricityProduction,
                    ElectricityConsumption = electricityConsumption,
                    ElectricityFulfilledConsumption = electricityFulfilledConsumption,
                    FreshWaterCapacity = freshWaterCapacity,
                    FreshWaterConsumption = freshWaterConsumption,
                    FreshWaterFulfilledConsumption = freshWaterFulfilledConsumption,
                    SewageCapacity = sewageCapacity,
                    SewageConsumption = sewageConsumption,
                    SewageFulfilledConsumption = sewageFulfilledConsumption,
                    PingMs = pingMs,
                    TimestampUtc = DateTime.UtcNow
                });
            }

            states = parsed;
            return true;
        }

        private string SerializeContracts()
        {
            if (_contracts.Count == 0)
                return "CONTRACTS";

            var entries = _contracts.Select(SerializeContract).ToArray();
            return "CONTRACTS|" + string.Join("|", entries);
        }

        private static string SerializeContract(MultiplayerContract c)
        {
            return string.Join(",",
                Uri.EscapeDataString(c.Id ?? string.Empty),
                Uri.EscapeDataString(c.SellerPlayer ?? string.Empty),
                Uri.EscapeDataString(c.BuyerPlayer ?? string.Empty),
                (int)c.Resource,
                c.UnitsPerTick,
                c.PricePerUnit,
                c.CreatedUtc.Ticks);
        }

        private static bool TryParseContracts(string line, out List<MultiplayerContract> contracts)
        {
            contracts = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length < 1 || !string.Equals(parts[0], "CONTRACTS", StringComparison.Ordinal))
                return false;

            var parsed = new List<MultiplayerContract>();
            for (var i = 1; i < parts.Length; i++)
            {
                var fields = parts[i].Split(',');
                if (fields.Length != 7)
                    continue;

                if (!int.TryParse(fields[3], out var resourceId) ||
                    !int.TryParse(fields[4], out var unitsPerTick) ||
                    !int.TryParse(fields[5], out var pricePerUnit) ||
                    !long.TryParse(fields[6], out var createdTicks))
                    continue;

                parsed.Add(new MultiplayerContract
                {
                    Id = Uri.UnescapeDataString(fields[0]),
                    SellerPlayer = NormalizePlayerName(Uri.UnescapeDataString(fields[1])),
                    BuyerPlayer = NormalizePlayerName(Uri.UnescapeDataString(fields[2])),
                    Resource = Enum.IsDefined(typeof(MultiplayerContractResource), resourceId)
                        ? (MultiplayerContractResource)resourceId
                        : MultiplayerContractResource.Electricity,
                    UnitsPerTick = unitsPerTick,
                    PricePerUnit = pricePerUnit,
                    CreatedUtc = new DateTime(createdTicks, DateTimeKind.Utc)
                });
            }

            contracts = parsed;
            return true;
        }

        private string SerializeProposals()
        {
            var now = DateTime.UtcNow;
            CleanupExpiredProposals(now);
            if (_pendingProposals.Count == 0)
                return "PROPOSALS";

            var entries = _pendingProposals.Select(SerializeProposal).ToArray();
            return "PROPOSALS|" + string.Join("|", entries);
        }

        private static string SerializeProposal(MultiplayerContractProposal p)
        {
            return string.Join(",",
                Uri.EscapeDataString(p.Id ?? string.Empty),
                Uri.EscapeDataString(p.SellerPlayer ?? string.Empty),
                Uri.EscapeDataString(p.BuyerPlayer ?? string.Empty),
                (int)p.Resource,
                p.UnitsPerTick,
                p.PricePerUnit,
                p.CreatedUtc.Ticks);
        }

        private static bool TryParseProposals(string line, out List<MultiplayerContractProposal> proposals)
        {
            proposals = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length < 1 || !string.Equals(parts[0], "PROPOSALS", StringComparison.Ordinal))
                return false;

            var parsed = new List<MultiplayerContractProposal>();
            for (var i = 1; i < parts.Length; i++)
            {
                var fields = parts[i].Split(',');
                if (fields.Length != 7)
                    continue;

                if (!int.TryParse(fields[3], out var resourceId) ||
                    !int.TryParse(fields[4], out var unitsPerTick) ||
                    !int.TryParse(fields[5], out var pricePerUnit) ||
                    !long.TryParse(fields[6], out var createdTicks))
                    continue;

                parsed.Add(new MultiplayerContractProposal
                {
                    Id = Uri.UnescapeDataString(fields[0]),
                    SellerPlayer = NormalizePlayerName(Uri.UnescapeDataString(fields[1])),
                    BuyerPlayer = NormalizePlayerName(Uri.UnescapeDataString(fields[2])),
                    Resource = Enum.IsDefined(typeof(MultiplayerContractResource), resourceId)
                        ? (MultiplayerContractResource)resourceId
                        : MultiplayerContractResource.Electricity,
                    UnitsPerTick = unitsPerTick,
                    PricePerUnit = pricePerUnit,
                    CreatedUtc = new DateTime(createdTicks, DateTimeKind.Utc)
                });
            }

            proposals = parsed;
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
            return $"CONTRACTDECISION|{Uri.EscapeDataString(proposalId)}|{Uri.EscapeDataString(actorCity)}|{(accept ? 1 : 0)}";
        }

        private static bool TryParseContractDecision(string line, out ContractDecision decision)
        {
            decision = default;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length != 4 || !string.Equals(parts[0], "CONTRACTDECISION", StringComparison.Ordinal))
                return false;

            if (!int.TryParse(parts[3], out var acceptFlag))
                return false;

            decision = new ContractDecision
            {
                ProposalId = Uri.UnescapeDataString(parts[1]),
                ActorCity = Uri.UnescapeDataString(parts[2]),
                Accept = acceptFlag == 1
            };
            return true;
        }

        private static string SerializeContractCancel(string contractId, string actorPlayer)
        {
            return $"CONTRACTCANCEL|{Uri.EscapeDataString(contractId)}|{Uri.EscapeDataString(actorPlayer)}";
        }

        private static bool TryParseContractCancel(string line, out ContractCancel cancel)
        {
            cancel = default;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length != 3 || !string.Equals(parts[0], "CONTRACTCANCEL", StringComparison.Ordinal))
                return false;

            cancel = new ContractCancel
            {
                ContractId = Uri.UnescapeDataString(parts[1]),
                ActorPlayer = NormalizePlayerName(Uri.UnescapeDataString(parts[2]))
            };
            return true;
        }

        private struct ContractProposal
        {
            public string SellerPlayer;
            public string BuyerPlayer;
            public MultiplayerContractResource Resource;
            public int UnitsPerTick;
            public int PricePerUnit;
        }

        private static string SerializeContractRequest(string sellerPlayer, string buyerPlayer, MultiplayerContractResource resource, int unitsPerTick, int pricePerUnit)
        {
            return $"CONTRACTREQ|{Uri.EscapeDataString(sellerPlayer)}|{Uri.EscapeDataString(buyerPlayer)}|{(int)resource}|{unitsPerTick}|{pricePerUnit}";
        }

        private static bool TryParseContractRequest(string line, out ContractProposal proposal)
        {
            proposal = default;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length != 6 || !string.Equals(parts[0], "CONTRACTREQ", StringComparison.Ordinal))
                return false;

            if (!int.TryParse(parts[3], out var resourceId) ||
                !int.TryParse(parts[4], out var unitsPerTick) ||
                !int.TryParse(parts[5], out var pricePerUnit))
                return false;

            proposal = new ContractProposal
            {
                SellerPlayer = NormalizePlayerName(Uri.UnescapeDataString(parts[1])),
                BuyerPlayer = NormalizePlayerName(Uri.UnescapeDataString(parts[2])),
                Resource = Enum.IsDefined(typeof(MultiplayerContractResource), resourceId)
                    ? (MultiplayerContractResource)resourceId
                    : MultiplayerContractResource.Electricity,
                UnitsPerTick = unitsPerTick,
                PricePerUnit = pricePerUnit
            };
            return true;
        }

        private static bool IsExpired(MultiplayerContractProposal proposal, DateTime nowUtc)
        {
            return proposal.CreatedUtc.AddSeconds(ProposalTimeoutSeconds) <= nowUtc;
        }

        private void CleanupExpiredProposals(DateTime nowUtc)
        {
            _pendingProposals.RemoveAll(p => IsExpired(p, nowUtc));
        }

        private bool TryApplyProposalDecision(string proposalId, string actorCity, bool accept, out string error)
        {
            error = null;
            CleanupExpiredProposals(DateTime.UtcNow);
            var index = _pendingProposals.FindIndex(p => string.Equals(p.Id, proposalId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                error = "Proposition introuvable ou expiree.";
                return false;
            }

            var proposal = _pendingProposals[index];
            if (!string.Equals(proposal.SellerPlayer, actorCity, StringComparison.OrdinalIgnoreCase))
            {
                error = "Seul le joueur vendeur peut repondre.";
                return false;
            }

            _pendingProposals.RemoveAt(index);
            if (!accept)
            {
                AddDebugLog($"Proposal {proposalId} refused by {actorCity}");
                return true;
            }

            if (!TryGetStateByPlayer(proposal.SellerPlayer, out _) || !TryGetStateByPlayer(proposal.BuyerPlayer, out _))
            {
                error = "Joueurs introuvables au moment de l'acceptation.";
                return false;
            }

            _contracts.Add(new MultiplayerContract
            {
                Id = Guid.NewGuid().ToString("N"),
                SellerPlayer = proposal.SellerPlayer,
                BuyerPlayer = proposal.BuyerPlayer,
                Resource = proposal.Resource,
                UnitsPerTick = proposal.UnitsPerTick,
                PricePerUnit = proposal.PricePerUnit,
                CreatedUtc = DateTime.UtcNow
            });
            AddDebugLog($"Proposal {proposalId} accepted by {actorCity}, contract active.");

            return true;
        }

        private bool TryCancelContractInternal(string contractId, string actorPlayer, out string error)
        {
            error = null;
            var index = _contracts.FindIndex(c => string.Equals(c.Id, contractId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                error = "Contrat introuvable.";
                return false;
            }

            var contract = _contracts[index];
            if (!string.Equals(contract.SellerPlayer, actorPlayer, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(contract.BuyerPlayer, actorPlayer, StringComparison.OrdinalIgnoreCase))
            {
                error = "Seuls le vendeur ou l'acheteur peuvent annuler ce contrat.";
                return false;
            }

            _contracts.RemoveAt(index);
            AddDebugLog($"Contract {contractId} cancelled by {actorPlayer}.");
            return true;
        }

        private bool TryGetStateByPlayer(string playerName, out MultiplayerResourceState state)
        {
            if (string.IsNullOrWhiteSpace(playerName))
            {
                state = default;
                return false;
            }

            playerName = NormalizePlayerName(playerName);
            var local = ApplyAdjustments(GetLocalState());
            local.Name = NormalizePlayerName(local.Name);
            if (string.Equals(local.Name, playerName, StringComparison.OrdinalIgnoreCase))
            {
                state = local;
                return true;
            }

            if (_remoteStates.TryGetValue(playerName, out var remote))
            {
                state = ApplyAdjustments(remote);
                return true;
            }

            state = default;
            return false;
        }

        private MultiplayerResourceState ApplyAdjustments(MultiplayerResourceState state)
        {
            state.Name = NormalizePlayerName(state.Name);
            if (_moneyAdjustments.TryGetValue(state.Name, out var moneyDelta))
            {
                state.Money += moneyDelta;
            }

            if (_resourceAdjustments.TryGetValue(state.Name, out var delta))
            {
                state.ElectricityProduction = Math.Max(0, state.ElectricityProduction + delta.ElectricityProduction);
                state.ElectricityFulfilledConsumption = Math.Max(0, state.ElectricityFulfilledConsumption + delta.ElectricityFulfilled);
                state.FreshWaterCapacity = Math.Max(0, state.FreshWaterCapacity + delta.WaterCapacity);
                state.FreshWaterFulfilledConsumption = Math.Max(0, state.FreshWaterFulfilledConsumption + delta.WaterFulfilled);
                state.SewageCapacity = Math.Max(0, state.SewageCapacity + delta.SewageCapacity);
                state.SewageFulfilledConsumption = Math.Max(0, state.SewageFulfilledConsumption + delta.SewageFulfilled);
            }

            return state;
        }

        private void ApplyContractSettlementsIfDue(DateTime nowUtc)
        {
            if (nowUtc < _nextSettlementUtc)
                return;

            _nextSettlementUtc = nowUtc.AddSeconds(2);
            CleanupExpiredProposals(nowUtc);
            _resourceAdjustments.Clear();

            if (_contracts.Count == 0)
                return;

            var effectiveStates = new Dictionary<string, MultiplayerResourceState>(StringComparer.OrdinalIgnoreCase);
            var local = ApplyAdjustments(GetLocalState());
            effectiveStates[local.Name] = local;
            foreach (var kvp in _remoteStates)
            {
                effectiveStates[kvp.Key] = ApplyAdjustments(kvp.Value);
            }

            for (var i = 0; i < _contracts.Count; i++)
            {
                var contract = _contracts[i];
                if (!effectiveStates.TryGetValue(contract.SellerPlayer, out var seller) ||
                    !effectiveStates.TryGetValue(contract.BuyerPlayer, out var buyer))
                    continue;

                var available = GetSellerAvailable(contract.Resource, seller);
                if (available <= 0)
                {
                    AddDebugLog($"Contract {contract.Id} cancelled (seller no surplus).");
                    _contracts.RemoveAt(i);
                    i--;
                    continue;
                }

                var affordableUnits = contract.PricePerUnit <= 0 ? 0 : Math.Max(0, buyer.Money / contract.PricePerUnit);
                var transferUnits = Math.Min(contract.UnitsPerTick, Math.Min(available, affordableUnits));
                if (transferUnits <= 0)
                    continue;

                var payment = transferUnits * contract.PricePerUnit;
                seller.Money += payment;
                buyer.Money -= payment;
                AddDebugLog($"Settlement {contract.SellerPlayer}->{contract.BuyerPlayer} res={contract.Resource} units={transferUnits} payment={payment}");

                ApplyResourceTransfer(contract.Resource, ref seller, ref buyer, transferUnits);
                AddMoneyAdjustment(contract.SellerPlayer, payment);
                AddMoneyAdjustment(contract.BuyerPlayer, -payment);
                AddResourceAdjustment(contract.SellerPlayer, contract.Resource, -transferUnits, false);
                AddResourceAdjustment(contract.BuyerPlayer, contract.Resource, transferUnits, true);

                effectiveStates[contract.SellerPlayer] = seller;
                effectiveStates[contract.BuyerPlayer] = buyer;
            }
        }

        private static int GetSellerAvailable(MultiplayerContractResource resource, MultiplayerResourceState state)
        {
            switch (resource)
            {
                case MultiplayerContractResource.Electricity:
                    return Math.Max(0, state.ElectricityProduction - state.ElectricityConsumption);
                case MultiplayerContractResource.FreshWater:
                    return Math.Max(0, state.FreshWaterCapacity - state.FreshWaterConsumption);
                case MultiplayerContractResource.Sewage:
                    return Math.Max(0, state.SewageCapacity - state.SewageConsumption);
                default:
                    return 0;
            }
        }

        private static void ApplyResourceTransfer(MultiplayerContractResource resource, ref MultiplayerResourceState seller, ref MultiplayerResourceState buyer, int units)
        {
            switch (resource)
            {
                case MultiplayerContractResource.Electricity:
                    seller.ElectricityProduction = Math.Max(0, seller.ElectricityProduction - units);
                    buyer.ElectricityFulfilledConsumption = Math.Min(buyer.ElectricityConsumption, buyer.ElectricityFulfilledConsumption + units);
                    break;
                case MultiplayerContractResource.FreshWater:
                    seller.FreshWaterCapacity = Math.Max(0, seller.FreshWaterCapacity - units);
                    buyer.FreshWaterFulfilledConsumption = Math.Min(buyer.FreshWaterConsumption, buyer.FreshWaterFulfilledConsumption + units);
                    break;
                case MultiplayerContractResource.Sewage:
                    seller.SewageCapacity = Math.Max(0, seller.SewageCapacity - units);
                    buyer.SewageFulfilledConsumption = Math.Min(buyer.SewageConsumption, buyer.SewageFulfilledConsumption + units);
                    break;
            }
        }

        private void AddMoneyAdjustment(string playerName, int value)
        {
            if (string.IsNullOrWhiteSpace(playerName) || value == 0)
                return;

            playerName = NormalizePlayerName(playerName);
            _moneyAdjustments.TryGetValue(playerName, out var current);
            _moneyAdjustments[playerName] = current + value;
        }

        private void AddResourceAdjustment(string playerName, MultiplayerContractResource resource, int value, bool isBuyer)
        {
            if (string.IsNullOrWhiteSpace(playerName) || value == 0)
                return;

            playerName = NormalizePlayerName(playerName);
            _resourceAdjustments.TryGetValue(playerName, out var delta);
            switch (resource)
            {
                case MultiplayerContractResource.Electricity:
                    if (isBuyer)
                        delta.ElectricityFulfilled += value;
                    else
                        delta.ElectricityProduction += value;
                    break;
                case MultiplayerContractResource.FreshWater:
                    if (isBuyer)
                        delta.WaterFulfilled += value;
                    else
                        delta.WaterCapacity += value;
                    break;
                case MultiplayerContractResource.Sewage:
                    if (isBuyer)
                        delta.SewageFulfilled += value;
                    else
                        delta.SewageCapacity += value;
                    break;
            }
            _resourceAdjustments[playerName] = delta;
        }

        private static string SerializePingRequest(long id)
        {
            return $"PINGREQ|{id}";
        }

        private static string SerializePingResponse(long id)
        {
            return $"PINGRSP|{id}";
        }

        private static bool TryParsePingRequest(string line, out long id)
        {
            id = 0;
            var parts = line.Split('|');
            return parts.Length == 2 &&
                   string.Equals(parts[0], "PINGREQ", StringComparison.Ordinal) &&
                   long.TryParse(parts[1], out id);
        }

        private static bool TryParsePingResponse(string line, out long id)
        {
            id = 0;
            var parts = line.Split('|');
            return parts.Length == 2 &&
                   string.Equals(parts[0], "PINGRSP", StringComparison.Ordinal) &&
                   long.TryParse(parts[1], out id);
        }
    }
}



