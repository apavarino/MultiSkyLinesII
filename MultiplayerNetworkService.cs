using System;
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
        private readonly ILog _log;
        private readonly Func<MultiplayerResourceState> _localStateProvider;

        private CancellationTokenSource _cts;
        private TcpListener _listener;
        private TcpClient _client;
        private readonly object _sync = new object();
        private readonly Dictionary<string, MultiplayerResourceState> _remoteStates = new Dictionary<string, MultiplayerResourceState>(StringComparer.OrdinalIgnoreCase);
        private readonly List<TcpClient> _hostClients = new List<TcpClient>();

        public bool IsRunning => _cts != null;
        public bool IsHost { get; private set; }

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
                return;
            }

            if (settings.Port < 1 || settings.Port > 65535)
            {
                _log.Error($"Invalid port {settings.Port}. Expected 1..65535.");
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            IsHost = settings.HostMode;

            if (settings.HostMode)
            {
                Task.Run(() => RunHostLoop(settings.BindAddress, settings.Port, token), token);
                _log.Info($"Multiplayer host started on {settings.BindAddress}:{settings.Port}");
            }
            else
            {
                Task.Run(() => RunClientLoop(settings.ServerAddress, settings.Port, token), token);
                _log.Info($"Multiplayer client started for {settings.ServerAddress}:{settings.Port}");
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
                IsHost = false;
            }
        }

        private async Task RunHostLoop(string bindAddress, int port, CancellationToken token)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(bindAddress, out ip))
            {
                _log.Error($"Invalid host bind address: {bindAddress}");
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
                    await client.ConnectAsync(serverAddress, port).ConfigureAwait(false);
                    _log.Info("Connected to host.");
                    ReplaceClient(client);
                    await HandleConnectedClient(client, token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    if (!token.IsCancellationRequested)
                    {
                        _log.Warn($"Connection failed ({serverAddress}:{port}): {e.Message}");
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

            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
            using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true) { AutoFlush = true })
            {
                await writer.WriteLineAsync(SerializeState(_localStateProvider())).ConfigureAwait(false);

                while (!token.IsCancellationRequested && client.Connected)
                {
                    var readTask = reader.ReadLineAsync();
                    var completed = await Task.WhenAny(readTask, Task.Delay(2000, token)).ConfigureAwait(false);

                    if (completed == readTask)
                    {
                        var line = readTask.Result;
                        if (line == null)
                        {
                            _log.Info("Peer disconnected.");
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
                            remoteName = remote.Name;
                            remote.PingMs = currentRttMs;
                            lock (_sync)
                            {
                                _remoteStates[remote.Name] = remote;
                            }
                        }
                        else if (TryParseSnapshot(line, out var snapshotStates))
                        {
                            lock (_sync)
                            {
                                _remoteStates.Clear();
                                foreach (var state in snapshotStates)
                                {
                                    _remoteStates[state.Name] = state;
                                }
                            }
                        }
                        else
                        {
                            _log.Info($"Net RX: {line}");
                        }
                    }
                    else
                    {
                        var pingId = ++pingSequence;
                        pendingPings[pingId] = DateTime.UtcNow;
                        await writer.WriteLineAsync(SerializePingRequest(pingId)).ConfigureAwait(false);

                        if (IsHost)
                        {
                            await writer.WriteLineAsync(SerializeSnapshot()).ConfigureAwait(false);
                        }
                        else
                        {
                            await writer.WriteLineAsync(SerializeState(_localStateProvider())).ConfigureAwait(false);
                        }
                    }
                }
            }

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
            return _localStateProvider();
        }

        public IReadOnlyList<MultiplayerResourceState> GetConnectedStates()
        {
            lock (_sync)
            {
                var local = _localStateProvider();
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
            var encoded = Uri.EscapeDataString(state.Name ?? "Unknown");
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
                Name = Uri.UnescapeDataString(parts[1]),
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
                    Name = Uri.UnescapeDataString(entry[0]),
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
