using System;
using System.Collections.Generic;
using Xunit;

namespace MultiSkyLineII.Tests;

public class MultiplayerProtocolCodecTests
{
    [Fact]
    public void State_RoundTrip_PreservesFields()
    {
        var state = new MultiplayerResourceState
        {
            Name = "City One",
            Money = 123,
            Population = 456,
            ElectricityProduction = 1000,
            ElectricityConsumption = 900,
            ElectricityFulfilledConsumption = 850,
            FreshWaterCapacity = 400,
            FreshWaterConsumption = 300,
            FreshWaterFulfilledConsumption = 280,
            SewageCapacity = 500,
            SewageConsumption = 250,
            SewageFulfilledConsumption = 240,
            PingMs = 42,
            HasElectricityOutsideConnection = true,
            HasWaterOutsideConnection = false,
            HasSewageOutsideConnection = true,
            IsPaused = true,
            SimulationSpeed = 2,
            SimulationDateText = "2030-01"
        };

        var line = MultiplayerProtocolCodec.SerializeState(state);
        var ok = MultiplayerProtocolCodec.TryParseState(line, out var parsed);

        Assert.True(ok);
        Assert.Equal(state.Name, parsed.Name);
        Assert.Equal(state.Money, parsed.Money);
        Assert.Equal(state.Population, parsed.Population);
        Assert.Equal(state.SimulationDateText, parsed.SimulationDateText);
        Assert.Equal(state.HasSewageOutsideConnection, parsed.HasSewageOutsideConnection);
    }

    [Fact]
    public void Contracts_RoundTrip_PreservesCountAndFields()
    {
        var contracts = new List<MultiplayerContract>
        {
            new MultiplayerContract
            {
                Id = "c1",
                SellerPlayer = "Seller",
                BuyerPlayer = "Buyer",
                Resource = MultiplayerContractResource.FreshWater,
                UnitsPerTick = 77,
                EffectiveUnitsPerTick = 70,
                PricePerTick = 99,
                CreatedUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        };

        var line = MultiplayerProtocolCodec.SerializeContracts(contracts);
        var ok = MultiplayerProtocolCodec.TryParseContracts(line, out var parsed);

        Assert.True(ok);
        Assert.Single(parsed);
        Assert.Equal("c1", parsed[0].Id);
        Assert.Equal(MultiplayerContractResource.FreshWater, parsed[0].Resource);
        Assert.Equal(70, parsed[0].EffectiveUnitsPerTick);
    }

    [Fact]
    public void Ping_RoundTrip_Works()
    {
        var req = MultiplayerProtocolCodec.SerializePingRequest(12345);
        var reqOk = MultiplayerProtocolCodec.TryParsePingRequest(req, out var reqId);

        var rsp = MultiplayerProtocolCodec.SerializePingResponse(67890);
        var rspOk = MultiplayerProtocolCodec.TryParsePingResponse(rsp, out var rspId);

        Assert.True(reqOk);
        Assert.Equal(12345, reqId);
        Assert.True(rspOk);
        Assert.Equal(67890, rspId);
    }

    [Fact]
    public void Parse_InvalidOrTruncatedPayloads_ReturnFalse()
    {
        var badState = MultiplayerProtocolCodec.TryParseState("STATE|only_name", out _);
        var badPingReq = MultiplayerProtocolCodec.TryParsePingRequest("PINGREQ|NaN", out _);
        var badPingRsp = MultiplayerProtocolCodec.TryParsePingResponse("PINGRSP", out _);
        var badDecision = MultiplayerProtocolCodec.TryParseContractDecision("CONTRACTDECISION|id|actor", out _);
        var badCancel = MultiplayerProtocolCodec.TryParseContractCancel("CONTRACTCANCEL|id_only", out _);
        var badRequest = MultiplayerProtocolCodec.TryParseContractRequest("CONTRACTREQ|s|b|x|y|z", out _);

        Assert.False(badState);
        Assert.False(badPingReq);
        Assert.False(badPingRsp);
        Assert.False(badDecision);
        Assert.False(badCancel);
        Assert.False(badRequest);
    }

    [Fact]
    public void Parse_WrongMessageType_ReturnFalse()
    {
        var wrongState = MultiplayerProtocolCodec.TryParseState("LIST|abc", out _);
        var wrongContracts = MultiplayerProtocolCodec.TryParseContracts("STATE|abc", out _);
        var wrongProposals = MultiplayerProtocolCodec.TryParseProposals("PINGREQ|1", out _);
        var wrongSettles = MultiplayerProtocolCodec.TryParseSettlements("CONTRACTS|x", out _);

        Assert.False(wrongState);
        Assert.False(wrongContracts);
        Assert.False(wrongProposals);
        Assert.False(wrongSettles);
    }
}
