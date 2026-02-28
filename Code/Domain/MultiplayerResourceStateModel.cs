using System;

namespace MultiSkyLineII
{
    public struct MultiplayerResourceState
    {
        public string Name;
        public int Money;
        public int Population;
        public int ElectricityProduction;
        public int ElectricityConsumption;
        public int ElectricityFulfilledConsumption;
        public int FreshWaterCapacity;
        public int FreshWaterConsumption;
        public int FreshWaterFulfilledConsumption;
        public int SewageCapacity;
        public int SewageConsumption;
        public int SewageFulfilledConsumption;
        public int PingMs;
        public bool HasElectricityOutsideConnection;
        public bool HasWaterOutsideConnection;
        public bool HasSewageOutsideConnection;
        public bool IsPaused;
        public int SimulationSpeed;
        public string SimulationDateText;
        public DateTime TimestampUtc;
    }
}
