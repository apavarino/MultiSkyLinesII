using System;
using Game.City;
using Game.Simulation;
using Unity.Entities;

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
        public DateTime TimestampUtc;
    }

    public static class MultiplayerResourceReader
    {
        public static bool TryRead(ref MultiplayerResourceState state)
        {
            try
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (world == null)
                    return false;

                var entityManager = world.EntityManager;

                var moneyQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PlayerMoney>());
                if (!moneyQuery.IsEmptyIgnoreFilter)
                {
                    state.Money = moneyQuery.GetSingleton<PlayerMoney>().money;
                }

                var populationQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<Population>());
                if (!populationQuery.IsEmptyIgnoreFilter)
                {
                    state.Population = populationQuery.GetSingleton<Population>().m_Population;
                }

                var electricitySystem = world.GetExistingSystemManaged<ElectricityStatisticsSystem>();
                if (electricitySystem != null)
                {
                    state.ElectricityProduction = electricitySystem.production;
                    state.ElectricityConsumption = electricitySystem.consumption;
                    state.ElectricityFulfilledConsumption = electricitySystem.fulfilledConsumption;
                }

                var waterSystem = world.GetExistingSystemManaged<WaterStatisticsSystem>();
                if (waterSystem != null)
                {
                    state.FreshWaterCapacity = waterSystem.freshCapacity;
                    state.FreshWaterConsumption = waterSystem.freshConsumption;
                    state.FreshWaterFulfilledConsumption = waterSystem.fulfilledFreshConsumption;
                    state.SewageCapacity = waterSystem.sewageCapacity;
                    state.SewageConsumption = waterSystem.sewageConsumption;
                    state.SewageFulfilledConsumption = waterSystem.fulfilledSewageConsumption;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
