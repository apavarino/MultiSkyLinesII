using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsedPort) ? parsedPort : 25565;
var playerName = args.Length > 2 ? args[2] : $"Mock-{Guid.NewGuid().ToString("N")[..6]}";

var cts = new CancellationTokenSource();
var outbound = new ConcurrentQueue<string>();
const int AutoOfferElectricityUnits = 2000000;
const int AutoOfferPricePerTick = 1;
var autoOfferEnabled = true;
var autoOfferSent = false;
var state = new LocalState
{
    Name = playerName,
    Money = 1_000_000,
    Population = 15_000,
    ElectricityProduction = 9000000,
    ElectricityConsumption = 60000,
    ElectricityFulfilled = 60000,
    WaterCapacity = 7000000,
    WaterConsumption = 4500000,
    WaterFulfilled = 4500000,
    SewageCapacity = 6500000,
    SewageConsumption = 4300000,
    SewageFulfilled = 4300000,
    PingMs = 0,
    IsPaused = false,
    SimulationSpeed = 1,
    SimulationDateText = DateTime.Now.ToString("dd/MM/yyyy HH:mm"),
    HasElectricityOutsideConnection = true,
    HasWaterOutsideConnection = true,
    HasSewageOutsideConnection = true
};

List<Proposal> lastProposals = new();
var autoAcceptProposals = false;
var autoAcceptedProposalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

Console.WriteLine($"Connecting to {host}:{port} as '{state.Name}'...");
using var client = new TcpClient();
await client.ConnectAsync(host, port);
Console.WriteLine("Connected.");
Console.WriteLine("Type 'help' for commands.");

using var stream = client.GetStream();
using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
using var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true) { AutoFlush = true };

await writer.WriteLineAsync(SerializeState(state));

var readTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        string? line;
        try
        {
            line = await reader.ReadLineAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RX-ERR] {ex.GetType().Name}: {ex.Message}");
            cts.Cancel();
            break;
        }

        if (line == null)
        {
            Console.WriteLine("Disconnected by server.");
            cts.Cancel();
            break;
        }

        if (TryParsePingReq(line, out var pingId))
        {
            outbound.Enqueue($"PINGRSP|{pingId}");
            continue;
        }

        if (line.StartsWith("PROPOSALS", StringComparison.Ordinal))
        {
            try
            {
                lastProposals = ParseProposals(line);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PARSE-ERR] proposals: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            Console.WriteLine($"[RX] proposals={lastProposals.Count}");
            if (autoAcceptProposals)
            {
                for (var i = 0; i < lastProposals.Count; i++)
                {
                    var p = lastProposals[i];
                    var seller = (p.SellerPlayer ?? string.Empty).Trim();
                    var buyer = (p.BuyerPlayer ?? string.Empty).Trim();
                    var me = (state.Name ?? string.Empty).Trim();

                    // Never auto-accept our own offers as seller.
                    if (string.Equals(seller, me, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Accept only proposals addressed to us or public proposals.
                    if (!string.IsNullOrWhiteSpace(buyer) &&
                        !string.Equals(buyer, me, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!autoAcceptedProposalIds.Add(p.Id))
                        continue;

                    var msg = $"CONTRACTDECISION|{Uri.EscapeDataString(p.Id)}|{Uri.EscapeDataString(state.Name ?? "Mock")}|1";
                    outbound.Enqueue(msg);
                    Console.WriteLine($"[AUTO] accepted proposal {p.Id}");
                }
            }
            continue;
        }

        if (line.StartsWith("CONTRACTS", StringComparison.Ordinal))
        {
            var contracts = ParseContracts(line);
            Console.WriteLine($"[RX] contracts={contracts.Count}");
            continue;
        }

        if (line.StartsWith("LIST|", StringComparison.Ordinal))
        {
            Console.WriteLine("[RX] snapshot");
            continue;
        }

        if (line.StartsWith("STATE|", StringComparison.Ordinal))
        {
            Console.WriteLine("[RX] state");
            continue;
        }

        Console.WriteLine($"[RX] {line}");
    }
}, cts.Token);

var writeTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        if (autoOfferEnabled && !autoOfferSent)
        {
            var autoOffer = BuildPublicElectricityOffer(state.Name, AutoOfferElectricityUnits, AutoOfferPricePerTick);
            outbound.Enqueue(autoOffer);
            autoOfferSent = true;
            Console.WriteLine($"[AUTO] queued public electricity offer: {AutoOfferElectricityUnits} MW @ ${AutoOfferPricePerTick}/tick");
        }

        while (outbound.TryDequeue(out var msg))
        {
            try
            {
                await writer.WriteLineAsync(msg);
                Console.WriteLine($"[TX] {msg}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TX-ERR] {ex.GetType().Name}: {ex.Message}");
                cts.Cancel();
                break;
            }
        }

        try
        {
            await writer.WriteLineAsync(SerializeState(state));
            await Task.Delay(2000, cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WRITE-LOOP-ERR] {ex.GetType().Name}: {ex.Message}");
            cts.Cancel();
            break;
        }
    }
}, cts.Token);

while (!cts.Token.IsCancellationRequested)
{
    var input = Console.ReadLine();
    if (input == null)
    {
        cts.Cancel();
        break;
    }

    var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
        continue;

    var cmd = parts[0].ToLowerInvariant();
    switch (cmd)
    {
        case "help":
            Console.WriteLine("Commands:");
            Console.WriteLine("  name <playerName>");
            Console.WriteLine("  propose <sellerPlayer> <buyerPlayer> <resource:0|1|2> <unitsPerTick> <pricePerTick>");
            Console.WriteLine("  offer200 (send public electricity offer 200 MW @ $1/tick)");
            Console.WriteLine("  decide <proposalId> <accept|refuse>");
            Console.WriteLine("  proposals");
            Console.WriteLine("  state <money> <population>");
            Console.WriteLine("  border <e:0|1> <w:0|1> <s:0|1>");
            Console.WriteLine($"  autoaccept <0|1> (current: {(autoAcceptProposals ? 1 : 0)})");
            Console.WriteLine($"  autooffer <0|1> (current: {(autoOfferEnabled ? 1 : 0)})");
            Console.WriteLine("  quit");
            break;

        case "name":
            if (parts.Length >= 2)
            {
                state.Name = parts[1];
                Console.WriteLine($"Local player name set to {state.Name}");
            }
            break;

        case "state":
            if (parts.Length >= 3 && int.TryParse(parts[1], out var money) && int.TryParse(parts[2], out var pop))
            {
                state.Money = money;
                state.Population = pop;
                Console.WriteLine($"State updated: money={money}, pop={pop}");
            }
            else
            {
                Console.WriteLine("Usage: state <money> <population>");
            }
            break;

        case "propose":
            if (parts.Length >= 6 &&
                int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var resource) &&
                int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var units) &&
                int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var price))
            {
                var msg = $"CONTRACTREQ|{Uri.EscapeDataString(parts[1])}|{Uri.EscapeDataString(parts[2])}|{resource}|{units}|{price}";
                outbound.Enqueue(msg);
            }
            else
            {
                Console.WriteLine("Usage: propose <sellerPlayer> <buyerPlayer> <resource:0|1|2> <unitsPerTick> <pricePerTick>");
            }
            break;

        case "offer200":
            outbound.Enqueue(BuildPublicElectricityOffer(state.Name, AutoOfferElectricityUnits, AutoOfferPricePerTick));
            Console.WriteLine($"Queued public electricity offer: {AutoOfferElectricityUnits} MW @ ${AutoOfferPricePerTick}/tick");
            break;

        case "decide":
            if (parts.Length >= 3)
            {
                var accept = parts[2].Equals("accept", StringComparison.OrdinalIgnoreCase);
                var msg = $"CONTRACTDECISION|{Uri.EscapeDataString(parts[1])}|{Uri.EscapeDataString(state.Name)}|{(accept ? 1 : 0)}";
                outbound.Enqueue(msg);
            }
            else
            {
                Console.WriteLine("Usage: decide <proposalId> <accept|refuse>");
            }
            break;

        case "proposals":
            if (lastProposals.Count == 0)
            {
                Console.WriteLine("No pending proposals.");
            }
            else
            {
                foreach (var p in lastProposals)
                {
                    Console.WriteLine($"{p.Id} | {p.SellerPlayer} <- {p.BuyerPlayer} | res={p.Resource} units={p.Units} price/tick={p.PricePerTick}");
                }
            }
            break;

        case "border":
            if (parts.Length >= 4)
            {
                state.HasElectricityOutsideConnection = parts[1] == "1";
                state.HasWaterOutsideConnection = parts[2] == "1";
                state.HasSewageOutsideConnection = parts[3] == "1";
                Console.WriteLine($"Borders set: E={(state.HasElectricityOutsideConnection ? 1 : 0)} W={(state.HasWaterOutsideConnection ? 1 : 0)} S={(state.HasSewageOutsideConnection ? 1 : 0)}");
            }
            else
            {
                Console.WriteLine("Usage: border <e:0|1> <w:0|1> <s:0|1>");
            }
            break;

        case "autoaccept":
            if (parts.Length >= 2)
            {
                autoAcceptProposals = parts[1] == "1";
                Console.WriteLine($"Auto-accept proposals: {(autoAcceptProposals ? "ON" : "OFF")}");
            }
            else
            {
                Console.WriteLine("Usage: autoaccept <0|1>");
            }
            break;

        case "autooffer":
            if (parts.Length >= 2)
            {
                autoOfferEnabled = parts[1] == "1";
                if (autoOfferEnabled)
                {
                    autoOfferSent = false;
                }
                Console.WriteLine($"Auto-offer 200 MW: {(autoOfferEnabled ? "ON" : "OFF")}");
            }
            else
            {
                Console.WriteLine("Usage: autooffer <0|1>");
            }
            break;

        case "quit":
        case "exit":
            cts.Cancel();
            break;

        default:
            Console.WriteLine("Unknown command. Type 'help'.");
            break;
    }
}

await Task.WhenAll(readTask.ContinueWith(_ => { }), writeTask.ContinueWith(_ => { }));

static string SerializeState(LocalState s)
{
    var name = Uri.EscapeDataString(string.IsNullOrWhiteSpace(s.Name) ? "Mock" : s.Name);
    var date = Uri.EscapeDataString(s.SimulationDateText ?? string.Empty);
    return string.Join("|",
        "STATE", name, s.Money, s.Population,
        s.ElectricityProduction, s.ElectricityConsumption, s.ElectricityFulfilled,
        s.WaterCapacity, s.WaterConsumption, s.WaterFulfilled,
        s.SewageCapacity, s.SewageConsumption, s.SewageFulfilled,
        s.PingMs,
        s.IsPaused ? 1 : 0, s.SimulationSpeed, date,
        s.HasElectricityOutsideConnection ? 1 : 0,
        s.HasWaterOutsideConnection ? 1 : 0,
        s.HasSewageOutsideConnection ? 1 : 0);
}

static bool TryParsePingReq(string line, out long id)
{
    id = 0;
    var parts = line.Split('|');
    return parts.Length == 2 && parts[0] == "PINGREQ" && long.TryParse(parts[1], out id);
}

static List<Proposal> ParseProposals(string line)
{
    var result = new List<Proposal>();
    var parts = line.Split('|');
    for (var i = 1; i < parts.Length; i++)
    {
        var fields = parts[i].Split(',');
        if (fields.Length != 7)
            continue;

        if (!int.TryParse(fields[3], out var resource) ||
            !int.TryParse(fields[4], out var units) ||
            !int.TryParse(fields[5], out var pricePerTick))
            continue;

        result.Add(new Proposal(
            Uri.UnescapeDataString(fields[0]),
            Uri.UnescapeDataString(fields[1]),
            Uri.UnescapeDataString(fields[2]),
            resource,
            units,
            pricePerTick));
    }

    return result;
}

static List<string> ParseContracts(string line)
{
    var result = new List<string>();
    var parts = line.Split('|');
    for (var i = 1; i < parts.Length; i++)
    {
        result.Add(parts[i]);
    }
    return result;
}

static string BuildPublicElectricityOffer(string sellerPlayer, int unitsPerTick, int pricePerTick)
{
    var seller = Uri.EscapeDataString(string.IsNullOrWhiteSpace(sellerPlayer) ? "Mock" : sellerPlayer.Trim());
    // Empty buyer => public offer (any player can accept)
    return $"CONTRACTREQ|{seller}||0|{unitsPerTick}|{pricePerTick}";
}

file sealed class LocalState
{
    public string Name { get; set; } = "Mock";
    public int Money { get; set; }
    public int Population { get; set; }
    public int ElectricityProduction { get; set; }
    public int ElectricityConsumption { get; set; }
    public int ElectricityFulfilled { get; set; }
    public int WaterCapacity { get; set; }
    public int WaterConsumption { get; set; }
    public int WaterFulfilled { get; set; }
    public int SewageCapacity { get; set; }
    public int SewageConsumption { get; set; }
    public int SewageFulfilled { get; set; }
    public int PingMs { get; set; }
    public bool IsPaused { get; set; }
    public int SimulationSpeed { get; set; }
    public string SimulationDateText { get; set; } = string.Empty;
    public bool HasElectricityOutsideConnection { get; set; }
    public bool HasWaterOutsideConnection { get; set; }
    public bool HasSewageOutsideConnection { get; set; }
}

file sealed record Proposal(string Id, string SellerPlayer, string BuyerPlayer, int Resource, int Units, int PricePerTick);
