using System;
using System.Collections.Generic;
using Xunit;

namespace MultiSkyLineII.Tests;

public class MultiplayerContractRulesTests
{
    [Fact]
    public void TryApplyProposalDecision_AcceptPublicOffer_CreatesContract()
    {
        var proposals = new List<MultiplayerContractProposal>
        {
            new MultiplayerContractProposal
            {
                Id = "p1",
                SellerPlayer = "Seller",
                BuyerPlayer = string.Empty,
                Resource = MultiplayerContractResource.Electricity,
                UnitsPerTick = 10,
                PricePerTick = 100,
                CreatedUtc = DateTime.UtcNow
            }
        };
        var contracts = new List<MultiplayerContract>();

        var ok = MultiplayerContractRules.TryApplyProposalDecision(
            proposals,
            contracts,
            "p1",
            "BuyerA",
            true,
            s => s?.Trim() ?? string.Empty,
            _ => true,
            _ => { },
            proposalTimeoutSeconds: 120,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Empty(proposals);
        Assert.Single(contracts);
        Assert.Equal("Seller", contracts[0].SellerPlayer);
        Assert.Equal("BuyerA", contracts[0].BuyerPlayer);
    }

    [Fact]
    public void TryCancelContract_NonParticipant_Fails()
    {
        var contracts = new List<MultiplayerContract>
        {
            new MultiplayerContract { Id = "c1", SellerPlayer = "S", BuyerPlayer = "B" }
        };
        var failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var ok = MultiplayerContractRules.TryCancelContract(
            contracts,
            failures,
            "c1",
            "X",
            _ => { },
            out var error);

        Assert.False(ok);
        Assert.Equal("Seuls le vendeur ou l'acheteur peuvent annuler ce contrat.", error);
        Assert.Single(contracts);
    }

    [Fact]
    public void GetLocalTargetCapacityDelta_ComputesSignedDelta()
    {
        var contracts = new List<MultiplayerContract>
        {
            new MultiplayerContract { SellerPlayer = "Alice", BuyerPlayer = "Bob", Resource = MultiplayerContractResource.Electricity, UnitsPerTick = 100, EffectiveUnitsPerTick = 80 },
            new MultiplayerContract { SellerPlayer = "Bob", BuyerPlayer = "Alice", Resource = MultiplayerContractResource.Electricity, UnitsPerTick = 60, EffectiveUnitsPerTick = 60 }
        };

        var delta = MultiplayerContractRules.GetLocalTargetCapacityDelta(
            contracts,
            "Alice",
            MultiplayerContractResource.Electricity,
            s => s);

        Assert.Equal(-20, delta);
    }

    [Fact]
    public void CleanupExpiredProposals_RemovesExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var proposals = new List<MultiplayerContractProposal>
        {
            new MultiplayerContractProposal
            {
                Id = "expired",
                CreatedUtc = now.AddSeconds(-121)
            },
            new MultiplayerContractProposal
            {
                Id = "fresh",
                CreatedUtc = now.AddSeconds(-30)
            }
        };

        MultiplayerContractRules.CleanupExpiredProposals(proposals, now, timeoutSeconds: 120);

        Assert.Single(proposals);
        Assert.Equal("fresh", proposals[0].Id);
    }

    [Fact]
    public void TryApplyProposalDecision_ExpiredProposal_ReturnsNotFound()
    {
        var proposals = new List<MultiplayerContractProposal>
        {
            new MultiplayerContractProposal
            {
                Id = "p1",
                SellerPlayer = "Seller",
                BuyerPlayer = "Buyer",
                Resource = MultiplayerContractResource.Electricity,
                UnitsPerTick = 10,
                PricePerTick = 10,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-10)
            }
        };
        var contracts = new List<MultiplayerContract>();

        var ok = MultiplayerContractRules.TryApplyProposalDecision(
            proposals,
            contracts,
            "p1",
            "Buyer",
            true,
            s => s,
            _ => true,
            _ => { },
            proposalTimeoutSeconds: 120,
            out var error);

        Assert.False(ok);
        Assert.Equal("Proposition introuvable ou expiree.", error);
        Assert.Empty(proposals);
        Assert.Empty(contracts);
    }
}
