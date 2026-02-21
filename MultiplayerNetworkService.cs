using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Logging;

namespace MultiSkyLineII
{
    public sealed class MultiplayerNetworkService : IDisposable
    {
        private readonly ILog _log;

        private CancellationTokenSource _cts;
        private Task _networkTask;
        private TcpListener _listener;
        private TcpClient _client;
        private readonly object _sync = new object();

        public MultiplayerNetworkService(ILog log)
        {
            _log = log;
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

            if (settings.HostMode)
            {
                _networkTask = Task.Run(() => RunHostLoop(settings.BindAddress, settings.Port, token), token);
                _log.Info($"Multiplayer host started on {settings.BindAddress}:{settings.Port}");
            }
            else
            {
                _networkTask = Task.Run(() => RunClientLoop(settings.ServerAddress, settings.Port, token), token);
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
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
            using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true) { AutoFlush = true })
            {
                await writer.WriteLineAsync("HELLO FROM MULTISKYLINEII").ConfigureAwait(false);

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

                        _log.Info($"Net RX: {line}");
                    }
                    else
                    {
                        await writer.WriteLineAsync("PING").ConfigureAwait(false);
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
    }
}
