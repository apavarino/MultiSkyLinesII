using System;

namespace MultiSkyLineII
{
    public enum MultiplayerContractResource
    {
        Electricity = 0,
        FreshWater = 1,
        Sewage = 2
    }

    public struct MultiplayerContract
    {
        public string Id;
        public string SellerPlayer;
        public string BuyerPlayer;
        public MultiplayerContractResource Resource;
        public int UnitsPerTick;
        public int EffectiveUnitsPerTick;
        public int PricePerTick;
        public DateTime CreatedUtc;
    }

    public struct MultiplayerContractProposal
    {
        public string Id;
        public string SellerPlayer;
        public string BuyerPlayer;
        public MultiplayerContractResource Resource;
        public int UnitsPerTick;
        public int PricePerTick;
        public DateTime CreatedUtc;
    }
}

