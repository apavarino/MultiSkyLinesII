using System;
using System.Collections.Generic;
using Xunit;

namespace MultiSkyLineII.Tests;

public class MultiplayerSettlementProcessorTests
{
    [Fact]
    public void ApplyIfDue_NotDue_DoesNothing()
    {
        var contracts = new List<MultiplayerContract>
        {
            new MultiplayerContract
            {
                Id = "c1",
                SellerPlayer = "Seller",
                BuyerPlayer = "Buyer",
                Resource = MultiplayerContractResource.Electricity,
                UnitsPerTick = 10,
                PricePerTick = 5
            }
        };
        var pending = new List<MultiplayerContractProposal>();
        var remoteStates = new Dictionary<string, MultiplayerResourceState>(StringComparer.OrdinalIgnoreCase);
        var effective = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var next = DateTime.UtcNow.AddMinutes(1);

        var ctx = CreateContext(contracts, pending, remoteStates, effective, failures, next, out _, out _, out _);

        MultiplayerSettlementProcessor.ApplyIfDue(DateTime.UtcNow, ctx);

        Assert.Equal(next, ctx.NextSettlementUtc);
        Assert.Empty(effective);
        Assert.Empty(failures);
        Assert.Single(contracts);
    }

    [Fact]
    public void ApplyIfDue_DueAndValid_SettlesContractAndQueuesMoneyDelta()
    {
        var contracts = new List<MultiplayerContract>
        {
            new MultiplayerContract
            {
                Id = "c1",
                SellerPlayer = "Local",
                BuyerPlayer = "Remote",
                Resource = MultiplayerContractResource.Electricity,
                UnitsPerTick = 20,
                PricePerTick = 50
            }
        };
        var pending = new List<MultiplayerContractProposal>();
        var remoteStates = new Dictionary<string, MultiplayerResourceState>(StringComparer.OrdinalIgnoreCase)
        {
            ["Remote"] = new MultiplayerResourceState
            {
                Name = "Remote",
                Money = 10_000,
                ElectricityProduction = 100,
                ElectricityConsumption = 50,
                HasWaterOutsideConnection = true,
                HasSewageOutsideConnection = true
            }
        };
        var effective = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var next = DateTime.UtcNow.AddSeconds(-1);

        var ctx = CreateContext(contracts, pending, remoteStates, effective, failures, next, out var deltas, out var events, out _);

        MultiplayerSettlementProcessor.ApplyIfDue(DateTime.UtcNow, ctx);

        Assert.True(ctx.NextSettlementUtc > DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(20, contracts[0].EffectiveUnitsPerTick);
        Assert.Equal(20, effective["c1"]);
        Assert.Single(deltas);
        Assert.Equal(50, deltas[0]);
        Assert.Single(events);
        Assert.Equal(("Local", "Remote", 50), events[0]);
    }

    [Fact]
    public void ApplyIfDue_DueAndInsufficientCapacity_CancelsContractAtThreshold()
    {
        var contracts = new List<MultiplayerContract>
        {
            new MultiplayerContract
            {
                Id = "c1",
                SellerPlayer = "Seller",
                BuyerPlayer = "Buyer",
                Resource = MultiplayerContractResource.FreshWater,
                UnitsPerTick = 999,
                PricePerTick = 5
            }
        };
        var pending = new List<MultiplayerContractProposal>();
        var remoteStates = new Dictionary<string, MultiplayerResourceState>(StringComparer.OrdinalIgnoreCase)
        {
            ["Seller"] = new MultiplayerResourceState
            {
                Name = "Seller",
                Money = 1000,
                ElectricityProduction = 100,
                ElectricityConsumption = 100,
                HasWaterOutsideConnection = false,
                HasSewageOutsideConnection = true
            },
            ["Buyer"] = new MultiplayerResourceState
            {
                Name = "Buyer",
                Money = 1000,
                ElectricityProduction = 100,
                ElectricityConsumption = 50,
                HasWaterOutsideConnection = false,
                HasSewageOutsideConnection = true
            }
        };
        var effective = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var next = DateTime.UtcNow.AddSeconds(-1);

        var ctx = CreateContext(contracts, pending, remoteStates, effective, failures, next, out _, out _, out _);
        ctx.ContractCancelFailureThreshold = 1;

        MultiplayerSettlementProcessor.ApplyIfDue(DateTime.UtcNow, ctx);

        Assert.Empty(contracts);
        Assert.DoesNotContain("c1", failures.Keys);
    }

    private static MultiplayerSettlementProcessor.Context CreateContext(
        List<MultiplayerContract> contracts,
        List<MultiplayerContractProposal> proposals,
        Dictionary<string, MultiplayerResourceState> remoteStates,
        Dictionary<string, int> effective,
        Dictionary<string, int> failures,
        DateTime next,
        out List<int> deltas,
        out List<(string seller, string buyer, int payment)> events,
        out bool cleanupCalled)
    {
        var capturedDeltas = new List<int>();
        var capturedEvents = new List<(string seller, string buyer, int payment)>();
        var capturedCleanupCalled = false;
        deltas = capturedDeltas;
        events = capturedEvents;
        cleanupCalled = capturedCleanupCalled;

        return new MultiplayerSettlementProcessor.Context
        {
            Contracts = contracts,
            PendingProposals = proposals,
            RemoteStates = remoteStates,
            ContractEffectiveUnits = effective,
            ContractFailureCounts = failures,
            NextSettlementUtc = next,
            ContractCancelFailureThreshold = 3,
            GetLocalState = () => new MultiplayerResourceState
            {
                Name = "Local",
                Money = 10_000,
                ElectricityProduction = 500,
                ElectricityConsumption = 100,
                HasWaterOutsideConnection = true,
                HasSewageOutsideConnection = true
            },
            NormalizePlayerName = s => string.IsNullOrWhiteSpace(s) ? "Unknown Player" : s.Trim(),
            CanUseTransferInfrastructure = MultiplayerContractRules.CanUseTransferInfrastructure,
            GetSellerAvailable = (resource, state) => MultiplayerContractRules.GetSellerAvailable(resource, state),
            GetCommittedOutgoingUnits = (seller, resource) => MultiplayerContractRules.GetCommittedOutgoingUnits(contracts, seller, resource, s => string.IsNullOrWhiteSpace(s) ? "Unknown Player" : s.Trim()),
            AddDebugLog = _ => { },
            QueuePendingLocalMoneyDelta = value => capturedDeltas.Add(value),
            RecordSettlementEvent = (seller, buyer, payment) => capturedEvents.Add((seller, buyer, payment)),
            CleanupExpiredProposals = _ => { capturedCleanupCalled = true; }
        };
    }
}
