using System;
using Game.Buildings;
using Game.City;
using Game.Simulation;
using Unity.Entities;
using UnityEngine;
using System.Reflection;

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

    public static class MultiplayerResourceReader
    {
        private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static bool HasActiveCity()
        {
            try
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (world == null)
                    return false;

                var entityManager = world.EntityManager;
                var moneyQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PlayerMoney>());
                if (!moneyQuery.IsEmptyIgnoreFilter)
                    return true;

                var populationQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<Population>());
                if (!populationQuery.IsEmptyIgnoreFilter)
                    return true;

                if (world.GetExistingSystemManaged<ElectricityStatisticsSystem>() != null)
                    return true;

                return world.GetExistingSystemManaged<WaterStatisticsSystem>() != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryRead(ref MultiplayerResourceState state)
        {
            try
            {
                var timeScale = Time.timeScale;
                state.IsPaused = timeScale <= 0.001f;
                state.SimulationSpeed = EstimateSimulationSpeed(timeScale, state.IsPaused);
                state.SimulationDateText = TryResolveSimulationDateText(out var simulationDate) ? simulationDate : string.Empty;

                var world = World.DefaultGameObjectInjectionWorld;
                if (world == null)
                    return false;

                var entityManager = world.EntityManager;
                var hasData = false;
                state.HasElectricityOutsideConnection = false;
                state.HasWaterOutsideConnection = false;
                state.HasSewageOutsideConnection = false;

                var moneyQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PlayerMoney>());
                if (!moneyQuery.IsEmptyIgnoreFilter)
                {
                    state.Money = moneyQuery.GetSingleton<PlayerMoney>().money;
                    hasData = true;
                }

                var populationQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<Population>());
                if (!populationQuery.IsEmptyIgnoreFilter)
                {
                    state.Population = populationQuery.GetSingleton<Population>().m_Population;
                    hasData = true;
                }

                var electricitySystem = world.GetExistingSystemManaged<ElectricityStatisticsSystem>();
                if (electricitySystem != null)
                {
                    state.ElectricityProduction = electricitySystem.production;
                    state.ElectricityConsumption = electricitySystem.consumption;
                    state.ElectricityFulfilledConsumption = electricitySystem.fulfilledConsumption;
                    hasData = true;
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
                    hasData = true;
                }

                state.HasElectricityOutsideConnection = HasConnectedElectricityOutside(entityManager);
                state.HasWaterOutsideConnection = HasConnectedWaterOutside(entityManager);
                state.HasSewageOutsideConnection = HasConnectedSewageOutside(entityManager);

                return hasData;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryApplyMoneyDelta(int delta, out int appliedDelta)
        {
            appliedDelta = 0;
            if (delta == 0)
                return true;

            try
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (world == null)
                    return false;

                var entityManager = world.EntityManager;
                var moneyQuery = entityManager.CreateEntityQuery(ComponentType.ReadWrite<PlayerMoney>());
                if (moneyQuery.IsEmptyIgnoreFilter)
                    return false;

                var entity = moneyQuery.GetSingletonEntity();
                var money = entityManager.GetComponentData<PlayerMoney>(entity);
                var before = money.money;
                money.Add(delta);
                entityManager.SetComponentData(entity, money);
                var after = money.money;
                appliedDelta = after - before;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryApplyUtilityCapacityDelta(int electricityDelta, int waterDelta, int sewageDelta, out int appliedElectricity, out int appliedWater, out int appliedSewage)
        {
            appliedElectricity = 0;
            appliedWater = 0;
            appliedSewage = 0;
            if (electricityDelta == 0 && waterDelta == 0 && sewageDelta == 0)
                return true;

            try
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (world == null)
                    return false;

                var entityManager = world.EntityManager;
                // Keep border injection behavior, but also apply on utility producers so the effect is visible in simulation.
                _ = ApplyElectricityOutsideEdgeDelta(entityManager, electricityDelta);
                _ = ApplyWaterPipeOutsideEdgeDelta(entityManager, waterDelta, false);
                _ = ApplyWaterPipeOutsideEdgeDelta(entityManager, sewageDelta, true);

                appliedElectricity = ApplyElectricityProducerDelta(entityManager, electricityDelta);
                appliedWater = ApplyWaterPumpDelta(entityManager, waterDelta);
                appliedSewage = ApplySewageOutletDelta(entityManager, sewageDelta);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int ApplyElectricityProducerDelta(EntityManager entityManager, int delta)
        {
            var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<ElectricityProducer>());
            return ApplyProducerCapacityDelta(
                query.ToEntityArray(Unity.Collections.Allocator.Temp),
                delta,
                entity => entityManager.GetComponentData<ElectricityProducer>(entity).m_Capacity,
                (entity, value) =>
                {
                    var c = entityManager.GetComponentData<ElectricityProducer>(entity);
                    c.m_Capacity = value;
                    entityManager.SetComponentData(entity, c);
                });
        }

        private static int ApplyWaterPumpDelta(EntityManager entityManager, int delta)
        {
            var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<WaterPumpingStation>());
            return ApplyProducerCapacityDelta(
                query.ToEntityArray(Unity.Collections.Allocator.Temp),
                delta,
                entity => entityManager.GetComponentData<WaterPumpingStation>(entity).m_Capacity,
                (entity, value) =>
                {
                    var c = entityManager.GetComponentData<WaterPumpingStation>(entity);
                    c.m_Capacity = value;
                    entityManager.SetComponentData(entity, c);
                });
        }

        private static int ApplySewageOutletDelta(EntityManager entityManager, int delta)
        {
            var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<SewageOutlet>());
            return ApplyProducerCapacityDelta(
                query.ToEntityArray(Unity.Collections.Allocator.Temp),
                delta,
                entity => entityManager.GetComponentData<SewageOutlet>(entity).m_Capacity,
                (entity, value) =>
                {
                    var c = entityManager.GetComponentData<SewageOutlet>(entity);
                    c.m_Capacity = value;
                    entityManager.SetComponentData(entity, c);
                });
        }

        private static int ApplyProducerCapacityDelta(Unity.Collections.NativeArray<Entity> entities, int delta, Func<Entity, int> getCapacity, Action<Entity, int> setCapacity)
        {
            try
            {
                if (delta == 0 || entities.Length == 0)
                    return 0;

                if (delta > 0)
                {
                    var target = entities[0];
                    setCapacity(target, Math.Max(0, getCapacity(target) + delta));
                    return delta;
                }

                var remaining = -delta;
                var removed = 0;
                for (var i = 0; i < entities.Length && remaining > 0; i++)
                {
                    var entity = entities[i];
                    var current = Math.Max(0, getCapacity(entity));
                    if (current <= 0)
                        continue;

                    var remove = Math.Min(current, remaining);
                    setCapacity(entity, current - remove);
                    removed += remove;
                    remaining -= remove;
                }

                return -removed;
            }
            finally
            {
                entities.Dispose();
            }
        }

        private static int ApplyElectricityOutsideEdgeDelta(EntityManager entityManager, int delta)
        {
            if (delta == 0)
                return 0;

            var tradeNodes = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<TradeNode>(),
                ComponentType.ReadOnly<ElectricityFlowNode>(),
                ComponentType.ReadOnly<ConnectedFlowEdge>());
            if (tradeNodes.IsEmptyIgnoreFilter)
                return 0;

            var nodes = tradeNodes.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                if (delta > 0)
                {
                    for (var i = 0; i < nodes.Length; i++)
                    {
                        var edges = entityManager.GetBuffer<ConnectedFlowEdge>(nodes[i]);
                        for (var j = 0; j < edges.Length; j++)
                        {
                            var edgeEntity = edges[j].m_Edge;
                            if (!entityManager.HasComponent<ElectricityFlowEdge>(edgeEntity))
                                continue;

                            var edge = entityManager.GetComponentData<ElectricityFlowEdge>(edgeEntity);
                            edge.m_Capacity = Math.Max(0, edge.m_Capacity + delta);
                            entityManager.SetComponentData(edgeEntity, edge);
                            return delta;
                        }
                    }
                    return 0;
                }

                var remaining = -delta;
                var removed = 0;
                for (var i = 0; i < nodes.Length && remaining > 0; i++)
                {
                    var edges = entityManager.GetBuffer<ConnectedFlowEdge>(nodes[i]);
                    for (var j = 0; j < edges.Length && remaining > 0; j++)
                    {
                        var edgeEntity = edges[j].m_Edge;
                        if (!entityManager.HasComponent<ElectricityFlowEdge>(edgeEntity))
                            continue;

                        var edge = entityManager.GetComponentData<ElectricityFlowEdge>(edgeEntity);
                        var capacity = Math.Max(0, edge.m_Capacity);
                        if (capacity <= 0)
                            continue;

                        var remove = Math.Min(capacity, remaining);
                        edge.m_Capacity = capacity - remove;
                        entityManager.SetComponentData(edgeEntity, edge);
                        removed += remove;
                        remaining -= remove;
                    }
                }
                return -removed;
            }
            finally
            {
                nodes.Dispose();
            }
        }

        private static int ApplyWaterPipeOutsideEdgeDelta(EntityManager entityManager, int delta, bool sewage)
        {
            if (delta == 0)
                return 0;

            var tradeNodes = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<TradeNode>(),
                ComponentType.ReadOnly<WaterPipeNode>(),
                ComponentType.ReadOnly<ConnectedFlowEdge>());
            if (tradeNodes.IsEmptyIgnoreFilter)
                return 0;

            var nodes = tradeNodes.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                if (delta > 0)
                {
                    for (var i = 0; i < nodes.Length; i++)
                    {
                        var edges = entityManager.GetBuffer<ConnectedFlowEdge>(nodes[i]);
                        for (var j = 0; j < edges.Length; j++)
                        {
                            var edgeEntity = edges[j].m_Edge;
                            if (!entityManager.HasComponent<WaterPipeEdge>(edgeEntity))
                                continue;

                            var edge = entityManager.GetComponentData<WaterPipeEdge>(edgeEntity);
                            if (sewage)
                                edge.m_SewageCapacity = Math.Max(0, edge.m_SewageCapacity + delta);
                            else
                                edge.m_FreshCapacity = Math.Max(0, edge.m_FreshCapacity + delta);

                            entityManager.SetComponentData(edgeEntity, edge);
                            return delta;
                        }
                    }
                    return 0;
                }

                var remaining = -delta;
                var removed = 0;
                for (var i = 0; i < nodes.Length && remaining > 0; i++)
                {
                    var edges = entityManager.GetBuffer<ConnectedFlowEdge>(nodes[i]);
                    for (var j = 0; j < edges.Length && remaining > 0; j++)
                    {
                        var edgeEntity = edges[j].m_Edge;
                        if (!entityManager.HasComponent<WaterPipeEdge>(edgeEntity))
                            continue;

                        var edge = entityManager.GetComponentData<WaterPipeEdge>(edgeEntity);
                        var capacity = sewage ? Math.Max(0, edge.m_SewageCapacity) : Math.Max(0, edge.m_FreshCapacity);
                        if (capacity <= 0)
                            continue;

                        var remove = Math.Min(capacity, remaining);
                        if (sewage)
                            edge.m_SewageCapacity = capacity - remove;
                        else
                            edge.m_FreshCapacity = capacity - remove;

                        entityManager.SetComponentData(edgeEntity, edge);
                        removed += remove;
                        remaining -= remove;
                    }
                }
                return -removed;
            }
            finally
            {
                nodes.Dispose();
            }
        }

        private static bool HasConnectedElectricityOutside(EntityManager entityManager)
        {
            var tradeNodes = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<TradeNode>(),
                ComponentType.ReadOnly<ElectricityFlowNode>(),
                ComponentType.ReadOnly<ConnectedFlowEdge>());
            if (tradeNodes.IsEmptyIgnoreFilter)
                return false;

            var nodes = tradeNodes.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (var i = 0; i < nodes.Length; i++)
                {
                    var edges = entityManager.GetBuffer<ConnectedFlowEdge>(nodes[i]);
                    for (var j = 0; j < edges.Length; j++)
                    {
                        var edgeEntity = edges[j].m_Edge;
                        if (!entityManager.HasComponent<ElectricityFlowEdge>(edgeEntity))
                            continue;

                        var edge = entityManager.GetComponentData<ElectricityFlowEdge>(edgeEntity);
                        if ((edge.m_Flags & ElectricityFlowEdgeFlags.Disconnected) == 0)
                            return true;
                    }
                }
                return false;
            }
            finally
            {
                nodes.Dispose();
            }
        }

        private static bool HasConnectedWaterOutside(EntityManager entityManager)
        {
            return HasConnectedWaterPipeOutside(entityManager, false);
        }

        private static bool HasConnectedSewageOutside(EntityManager entityManager)
        {
            return HasConnectedWaterPipeOutside(entityManager, true);
        }

        private static bool HasConnectedWaterPipeOutside(EntityManager entityManager, bool sewage)
        {
            var tradeNodes = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<TradeNode>(),
                ComponentType.ReadOnly<WaterPipeNode>(),
                ComponentType.ReadOnly<ConnectedFlowEdge>());
            if (tradeNodes.IsEmptyIgnoreFilter)
                return false;

            var nodes = tradeNodes.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (var i = 0; i < nodes.Length; i++)
                {
                    var edges = entityManager.GetBuffer<ConnectedFlowEdge>(nodes[i]);
                    for (var j = 0; j < edges.Length; j++)
                    {
                        var edgeEntity = edges[j].m_Edge;
                        if (!entityManager.HasComponent<WaterPipeEdge>(edgeEntity))
                            continue;

                        var edge = entityManager.GetComponentData<WaterPipeEdge>(edgeEntity);
                        if (sewage)
                        {
                            if ((edge.m_Flags & WaterPipeEdgeFlags.SewageDisconnected) == 0)
                                return true;
                        }
                        else
                        {
                            if ((edge.m_Flags & WaterPipeEdgeFlags.WaterDisconnected) == 0)
                                return true;
                        }
                    }
                }
                return false;
            }
            finally
            {
                nodes.Dispose();
            }
        }

        private static int EstimateSimulationSpeed(float timeScale, bool isPaused)
        {
            if (isPaused)
                return 0;
            if (timeScale < 1.5f)
                return 1;
            if (timeScale < 3f)
                return 2;
            return 3;
        }

        private static bool TryResolveSimulationDateText(out string text)
        {
            text = null;
            try
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (world == null)
                    return false;

                var timeSystemType = ResolveTimeSystemType();
                if (timeSystemType == null)
                    return false;

                var getter = typeof(World).GetMethod("GetExistingSystemManaged", AnyInstance, null, new[] { typeof(Type) }, null);
                if (getter == null)
                    return false;

                var timeSystem = getter.Invoke(world, new object[] { timeSystemType });
                if (timeSystem == null)
                    return false;

                if (TryReadMember(timeSystem, out var rawDate,
                    "date",
                    "Date",
                    "currentDate",
                    "CurrentDate",
                    "currentDateTime",
                    "CurrentDateTime",
                    "m_Date",
                    "m_CurrentDate",
                    "formattedDate",
                    "dateString"))
                {
                    if (rawDate is DateTime dateTime)
                    {
                        text = dateTime.ToString("dd/MM/yyyy HH:mm");
                        return true;
                    }

                    var asString = rawDate?.ToString();
                    if (!string.IsNullOrWhiteSpace(asString))
                    {
                        text = asString;
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static Type ResolveTimeSystemType()
        {
            for (var i = 0; i < AppDomain.CurrentDomain.GetAssemblies().Length; i++)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()[i];
                try
                {
                    var type = assembly.GetType("Game.Simulation.TimeSystem");
                    if (type != null)
                        return type;
                }
                catch
                {
                }
            }

            return null;
        }

        private static bool TryReadMember(object target, out object value, params string[] memberNames)
        {
            value = null;
            if (target == null || memberNames == null)
                return false;

            var type = target.GetType();
            for (var i = 0; i < memberNames.Length; i++)
            {
                var name = memberNames[i];
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                try
                {
                    var property = type.GetProperty(name, AnyInstance);
                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        value = property.GetValue(target);
                        return true;
                    }
                }
                catch
                {
                }

                try
                {
                    var field = type.GetField(name, AnyInstance);
                    if (field != null)
                    {
                        value = field.GetValue(target);
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }
    }
}
