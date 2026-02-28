using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiSkyLineII
{
    internal static class MultiplayerProtocolCodec
    {
        private const string FallbackPlayerName = "Unknown Player";

        internal struct SettlementSyncEvent
        {
            public long Id;
            public string SellerPlayer;
            public string BuyerPlayer;
            public int Payment;
        }

        internal struct ContractDecisionMessage
        {
            public string ProposalId;
            public string ActorCity;
            public bool Accept;
        }

        internal struct ContractCancelMessage
        {
            public string ContractId;
            public string ActorPlayer;
        }

        internal struct ContractRequestMessage
        {
            public string SellerPlayer;
            public string BuyerPlayer;
            public MultiplayerContractResource Resource;
            public int UnitsPerTick;
            public int PricePerTick;
        }

        private static string NormalizePlayerName(string playerName)
        {
            return string.IsNullOrWhiteSpace(playerName) ? FallbackPlayerName : playerName.Trim();
        }

        public static string SerializeState(MultiplayerResourceState state)
        {
            var encoded = Uri.EscapeDataString(NormalizePlayerName(state.Name));
            var encodedDate = Uri.EscapeDataString(state.SimulationDateText ?? string.Empty);
            return $"STATE|{encoded}|{state.Money}|{state.Population}|{state.ElectricityProduction}|{state.ElectricityConsumption}|{state.ElectricityFulfilledConsumption}|{state.FreshWaterCapacity}|{state.FreshWaterConsumption}|{state.FreshWaterFulfilledConsumption}|{state.SewageCapacity}|{state.SewageConsumption}|{state.SewageFulfilledConsumption}|{state.PingMs}|{(state.IsPaused ? 1 : 0)}|{state.SimulationSpeed}|{encodedDate}|{(state.HasElectricityOutsideConnection ? 1 : 0)}|{(state.HasWaterOutsideConnection ? 1 : 0)}|{(state.HasSewageOutsideConnection ? 1 : 0)}";
        }

        public static bool TryParseState(string line, out MultiplayerResourceState state)
        {
            state = default;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length < 14 || !string.Equals(parts[0], "STATE", StringComparison.Ordinal))
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

            var isPaused = false;
            var simulationSpeed = 0;
            var simulationDateText = string.Empty;
            var hasElectricityOutside = false;
            var hasWaterOutside = false;
            var hasSewageOutside = false;
            if (parts.Length >= 17)
            {
                if (int.TryParse(parts[14], out var pausedFlag))
                {
                    isPaused = pausedFlag != 0;
                }

                if (int.TryParse(parts[15], out var parsedSpeed))
                {
                    simulationSpeed = Math.Max(0, parsedSpeed);
                }

                simulationDateText = Uri.UnescapeDataString(parts[16] ?? string.Empty);
            }

            if (parts.Length >= 20)
            {
                hasElectricityOutside = string.Equals(parts[17], "1", StringComparison.Ordinal);
                hasWaterOutside = string.Equals(parts[18], "1", StringComparison.Ordinal);
                hasSewageOutside = string.Equals(parts[19], "1", StringComparison.Ordinal);
            }

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
                HasElectricityOutsideConnection = hasElectricityOutside,
                HasWaterOutsideConnection = hasWaterOutside,
                HasSewageOutsideConnection = hasSewageOutside,
                IsPaused = isPaused,
                SimulationSpeed = simulationSpeed,
                SimulationDateText = simulationDateText,
                TimestampUtc = DateTime.UtcNow
            };
            return true;
        }

        public static string SerializeSnapshot(IReadOnlyList<MultiplayerResourceState> states)
        {
            var entries = states
                .Select(s => $"{Uri.EscapeDataString(s.Name ?? "Unknown")},{s.Money},{s.Population},{s.ElectricityProduction},{s.ElectricityConsumption},{s.ElectricityFulfilledConsumption},{s.FreshWaterCapacity},{s.FreshWaterConsumption},{s.FreshWaterFulfilledConsumption},{s.SewageCapacity},{s.SewageConsumption},{s.SewageFulfilledConsumption},{s.PingMs},{(s.IsPaused ? 1 : 0)},{s.SimulationSpeed},{Uri.EscapeDataString(s.SimulationDateText ?? string.Empty)},{(s.HasElectricityOutsideConnection ? 1 : 0)},{(s.HasWaterOutsideConnection ? 1 : 0)},{(s.HasSewageOutsideConnection ? 1 : 0)}")
                .ToArray();
            return "LIST|" + string.Join("|", entries);
        }

        public static bool TryParseSnapshot(string line, out List<MultiplayerResourceState> states)
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
                if (entry.Length < 13)
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

                var isPaused = false;
                var simulationSpeed = 0;
                var simulationDateText = string.Empty;
                var hasElectricityOutside = false;
                var hasWaterOutside = false;
                var hasSewageOutside = false;
                if (entry.Length >= 16)
                {
                    if (int.TryParse(entry[13], out var pausedFlag))
                    {
                        isPaused = pausedFlag != 0;
                    }

                    if (int.TryParse(entry[14], out var parsedSpeed))
                    {
                        simulationSpeed = Math.Max(0, parsedSpeed);
                    }

                    simulationDateText = Uri.UnescapeDataString(entry[15] ?? string.Empty);
                }

                if (entry.Length >= 19)
                {
                    hasElectricityOutside = string.Equals(entry[16], "1", StringComparison.Ordinal);
                    hasWaterOutside = string.Equals(entry[17], "1", StringComparison.Ordinal);
                    hasSewageOutside = string.Equals(entry[18], "1", StringComparison.Ordinal);
                }

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
                    HasElectricityOutsideConnection = hasElectricityOutside,
                    HasWaterOutsideConnection = hasWaterOutside,
                    HasSewageOutsideConnection = hasSewageOutside,
                    IsPaused = isPaused,
                    SimulationSpeed = simulationSpeed,
                    SimulationDateText = simulationDateText,
                    TimestampUtc = DateTime.UtcNow
                });
            }

            states = parsed;
            return true;
        }

        public static string SerializeContracts(IReadOnlyList<MultiplayerContract> contracts)
        {
            if (contracts.Count == 0)
                return "CONTRACTS";

            var entries = contracts
                .Select(c => string.Join(",",
                    Uri.EscapeDataString(c.Id ?? string.Empty),
                    Uri.EscapeDataString(c.SellerPlayer ?? string.Empty),
                    Uri.EscapeDataString(c.BuyerPlayer ?? string.Empty),
                    (int)c.Resource,
                    c.UnitsPerTick,
                    c.EffectiveUnitsPerTick,
                    c.PricePerTick,
                    c.CreatedUtc.Ticks))
                .ToArray();
            return "CONTRACTS|" + string.Join("|", entries);
        }

        public static bool TryParseContracts(string line, out List<MultiplayerContract> contracts)
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
                if (fields.Length < 7)
                    continue;
                if (!int.TryParse(fields[3], out var resourceId) ||
                    !int.TryParse(fields[4], out var unitsPerTick))
                    continue;

                var effectiveUnitsPerTick = unitsPerTick;
                var pricePerTick = 0;
                var createdTicks = 0L;
                if (fields.Length >= 8)
                {
                    if (!int.TryParse(fields[5], out effectiveUnitsPerTick) ||
                        !int.TryParse(fields[6], out pricePerTick) ||
                        !long.TryParse(fields[7], out createdTicks))
                        continue;
                }
                else
                {
                    if (!int.TryParse(fields[5], out pricePerTick) ||
                        !long.TryParse(fields[6], out createdTicks))
                        continue;
                    effectiveUnitsPerTick = unitsPerTick;
                }

                parsed.Add(new MultiplayerContract
                {
                    Id = Uri.UnescapeDataString(fields[0]),
                    SellerPlayer = NormalizePlayerName(Uri.UnescapeDataString(fields[1])),
                    BuyerPlayer = NormalizePlayerName(Uri.UnescapeDataString(fields[2])),
                    Resource = Enum.IsDefined(typeof(MultiplayerContractResource), resourceId)
                        ? (MultiplayerContractResource)resourceId
                        : MultiplayerContractResource.Electricity,
                    UnitsPerTick = unitsPerTick,
                    EffectiveUnitsPerTick = Math.Max(0, effectiveUnitsPerTick),
                    PricePerTick = pricePerTick,
                    CreatedUtc = new DateTime(createdTicks, DateTimeKind.Utc)
                });
            }

            contracts = parsed;
            return true;
        }

        public static string SerializeProposals(IReadOnlyList<MultiplayerContractProposal> proposals)
        {
            if (proposals.Count == 0)
                return "PROPOSALS";

            var entries = proposals
                .Select(p => string.Join(",",
                    Uri.EscapeDataString(p.Id ?? string.Empty),
                    Uri.EscapeDataString(p.SellerPlayer ?? string.Empty),
                    Uri.EscapeDataString(p.BuyerPlayer ?? string.Empty),
                    (int)p.Resource,
                    p.UnitsPerTick,
                    p.PricePerTick,
                    p.CreatedUtc.Ticks))
                .ToArray();
            return "PROPOSALS|" + string.Join("|", entries);
        }

        public static bool TryParseProposals(string line, out List<MultiplayerContractProposal> proposals)
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
                    !int.TryParse(fields[5], out var pricePerTick) ||
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
                    PricePerTick = pricePerTick,
                    CreatedUtc = new DateTime(createdTicks, DateTimeKind.Utc)
                });
            }

            proposals = parsed;
            return true;
        }

        public static string SerializeSettlements(IReadOnlyList<SettlementSyncEvent> settlements, int maxEntries)
        {
            if (settlements.Count == 0)
                return "SETTLES";

            var entries = settlements
                .Skip(Math.Max(0, settlements.Count - maxEntries))
                .Select(s => $"{s.Id},{Uri.EscapeDataString(s.SellerPlayer ?? string.Empty)},{Uri.EscapeDataString(s.BuyerPlayer ?? string.Empty)},{s.Payment}")
                .ToArray();
            return "SETTLES|" + string.Join("|", entries);
        }

        public static bool TryParseSettlements(string line, out List<SettlementSyncEvent> settlements)
        {
            settlements = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length < 1 || !string.Equals(parts[0], "SETTLES", StringComparison.Ordinal))
                return false;

            var parsed = new List<SettlementSyncEvent>();
            for (var i = 1; i < parts.Length; i++)
            {
                var fields = parts[i].Split(',');
                if (fields.Length != 4)
                    continue;

                if (!long.TryParse(fields[0], out var id) ||
                    !int.TryParse(fields[3], out var payment))
                    continue;

                parsed.Add(new SettlementSyncEvent
                {
                    Id = id,
                    SellerPlayer = NormalizePlayerName(Uri.UnescapeDataString(fields[1])),
                    BuyerPlayer = NormalizePlayerName(Uri.UnescapeDataString(fields[2])),
                    Payment = payment
                });
            }

            settlements = parsed;
            return true;
        }

        public static string SerializeContractDecision(string proposalId, string actorCity, bool accept)
        {
            return $"CONTRACTDECISION|{Uri.EscapeDataString(proposalId)}|{Uri.EscapeDataString(actorCity)}|{(accept ? 1 : 0)}";
        }

        public static bool TryParseContractDecision(string line, out ContractDecisionMessage decision)
        {
            decision = default;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length != 4 || !string.Equals(parts[0], "CONTRACTDECISION", StringComparison.Ordinal))
                return false;

            if (!int.TryParse(parts[3], out var acceptFlag))
                return false;

            decision = new ContractDecisionMessage
            {
                ProposalId = Uri.UnescapeDataString(parts[1]),
                ActorCity = Uri.UnescapeDataString(parts[2]),
                Accept = acceptFlag == 1
            };
            return true;
        }

        public static string SerializeContractCancel(string contractId, string actorPlayer)
        {
            return $"CONTRACTCANCEL|{Uri.EscapeDataString(contractId)}|{Uri.EscapeDataString(actorPlayer)}";
        }

        public static bool TryParseContractCancel(string line, out ContractCancelMessage cancel)
        {
            cancel = default;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length != 3 || !string.Equals(parts[0], "CONTRACTCANCEL", StringComparison.Ordinal))
                return false;

            cancel = new ContractCancelMessage
            {
                ContractId = Uri.UnescapeDataString(parts[1]),
                ActorPlayer = NormalizePlayerName(Uri.UnescapeDataString(parts[2]))
            };
            return true;
        }

        public static string SerializeContractRequest(string sellerPlayer, string buyerPlayer, MultiplayerContractResource resource, int unitsPerTick, int pricePerTick)
        {
            return $"CONTRACTREQ|{Uri.EscapeDataString(sellerPlayer)}|{Uri.EscapeDataString(buyerPlayer)}|{(int)resource}|{unitsPerTick}|{pricePerTick}";
        }

        public static bool TryParseContractRequest(string line, out ContractRequestMessage proposal)
        {
            proposal = default;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split('|');
            if (parts.Length != 6 || !string.Equals(parts[0], "CONTRACTREQ", StringComparison.Ordinal))
                return false;

            if (!int.TryParse(parts[3], out var resourceId) ||
                !int.TryParse(parts[4], out var unitsPerTick) ||
                !int.TryParse(parts[5], out var pricePerTick))
                return false;

            proposal = new ContractRequestMessage
            {
                SellerPlayer = NormalizePlayerName(Uri.UnescapeDataString(parts[1])),
                BuyerPlayer = string.IsNullOrWhiteSpace(parts[2]) ? string.Empty : NormalizePlayerName(Uri.UnescapeDataString(parts[2])),
                Resource = Enum.IsDefined(typeof(MultiplayerContractResource), resourceId)
                    ? (MultiplayerContractResource)resourceId
                    : MultiplayerContractResource.Electricity,
                UnitsPerTick = unitsPerTick,
                PricePerTick = pricePerTick
            };
            return true;
        }

        public static string SerializePingRequest(long id)
        {
            return $"PINGREQ|{id}";
        }

        public static string SerializePingResponse(long id)
        {
            return $"PINGRSP|{id}";
        }

        public static bool TryParsePingRequest(string line, out long id)
        {
            id = 0;
            var parts = line.Split('|');
            return parts.Length == 2 &&
                   string.Equals(parts[0], "PINGREQ", StringComparison.Ordinal) &&
                   long.TryParse(parts[1], out id);
        }

        public static bool TryParsePingResponse(string line, out long id)
        {
            id = 0;
            var parts = line.Split('|');
            return parts.Length == 2 &&
                   string.Equals(parts[0], "PINGRSP", StringComparison.Ordinal) &&
                   long.TryParse(parts[1], out id);
        }
    }
}
