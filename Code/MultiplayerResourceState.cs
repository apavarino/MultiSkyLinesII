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
        private static readonly object UtilitySync = new object();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedElecByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedWaterByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedSewageByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedElecEdgeByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedWaterEdgeByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedSewageEdgeByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedElecHubProducerByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedElecHubConsumerByEntity = new System.Collections.Generic.Dictionary<long, int>();

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

        public static bool TrySetUtilityCapacityTarget(int electricityTarget, int waterTarget, int sewageTarget, out int appliedElectricity, out int appliedWater, out int appliedSewage)
        {
            appliedElectricity = 0;
            appliedWater = 0;
            appliedSewage = 0;

            try
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (world == null)
                    return false;

                var entityManager = world.EntityManager;
                lock (UtilitySync)
                {
                    var elecImportTarget = Math.Max(0, electricityTarget);
                    var elecExportTarget = Math.Max(0, -electricityTarget);
                    // Apply contracts on dedicated in-city exchange hubs (transformer buildings connected to power grid).
                    var elecProducerDelta = SetElectricityHubProducerTarget(entityManager, elecImportTarget);
                    var elecConsumerDelta = SetElectricityHubConsumerTarget(entityManager, elecExportTarget);
                    appliedElectricity = elecProducerDelta - elecConsumerDelta;

                    var waterProducerDelta = SetWaterPumpTarget(entityManager, Math.Min(0, waterTarget));
                    var waterEdgeDelta = SetWaterOutsideEdgeTarget(entityManager, Math.Max(0, waterTarget), false);
                    appliedWater = waterProducerDelta + waterEdgeDelta;

                    var sewageProducerDelta = SetSewageOutletTarget(entityManager, Math.Min(0, sewageTarget));
                    var sewageEdgeDelta = SetWaterOutsideEdgeTarget(entityManager, Math.Max(0, sewageTarget), true);
                    appliedSewage = sewageProducerDelta + sewageEdgeDelta;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int SetElectricityProducerTarget(EntityManager entityManager, int target)
        {
            var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<ElectricityProducer>());
            return ReconcileProducerCapacityTarget(
                query.ToEntityArray(Unity.Collections.Allocator.Temp),
                target,
                AppliedElecByEntity,
                entity => entityManager.GetComponentData<ElectricityProducer>(entity).m_Capacity,
                (entity, value) =>
                {
                    var c = entityManager.GetComponentData<ElectricityProducer>(entity);
                    c.m_Capacity = value;
                    entityManager.SetComponentData(entity, c);
                });
        }

        private static int SetWaterPumpTarget(EntityManager entityManager, int target)
        {
            var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<WaterPumpingStation>());
            return ReconcileProducerCapacityTarget(
                query.ToEntityArray(Unity.Collections.Allocator.Temp),
                target,
                AppliedWaterByEntity,
                entity => entityManager.GetComponentData<WaterPumpingStation>(entity).m_Capacity,
                (entity, value) =>
                {
                    var c = entityManager.GetComponentData<WaterPumpingStation>(entity);
                    c.m_Capacity = value;
                    entityManager.SetComponentData(entity, c);
                });
        }

        private static int SetSewageOutletTarget(EntityManager entityManager, int target)
        {
            var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<SewageOutlet>());
            return ReconcileProducerCapacityTarget(
                query.ToEntityArray(Unity.Collections.Allocator.Temp),
                target,
                AppliedSewageByEntity,
                entity => entityManager.GetComponentData<SewageOutlet>(entity).m_Capacity,
                (entity, value) =>
                {
                    var c = entityManager.GetComponentData<SewageOutlet>(entity);
                    c.m_Capacity = value;
                    entityManager.SetComponentData(entity, c);
                });
        }

        private static int SetElectricityOutsideEdgeTarget(EntityManager entityManager, int target)
        {
            var edges = CollectElectricityOutsideEdges(entityManager);
            return ReconcileEdgeCapacityTarget(
                edges,
                target,
                AppliedElecEdgeByEntity,
                entity => entityManager.GetComponentData<ElectricityFlowEdge>(entity).m_Capacity,
                (entity, value) =>
                {
                    var edge = entityManager.GetComponentData<ElectricityFlowEdge>(entity);
                    edge.m_Capacity = value;
                    entityManager.SetComponentData(entity, edge);
                });
        }

        private static int SetElectricityHubProducerTarget(EntityManager entityManager, int target)
        {
            var hubs = CollectElectricityHubEntities(entityManager);
            if (hubs.Count == 0)
            {
                AppliedElecHubProducerByEntity.Clear();
                return 0;
            }

            EnsureElectricityHubComponents(entityManager, hubs, ensureProducer: true, ensureConsumer: false);
            return ReconcileProducerCapacityTarget(
                ToNativeArray(hubs),
                target,
                AppliedElecHubProducerByEntity,
                entity => entityManager.GetComponentData<ElectricityProducer>(entity).m_Capacity,
                (entity, value) =>
                {
                    var c = entityManager.GetComponentData<ElectricityProducer>(entity);
                    c.m_Capacity = value;
                    entityManager.SetComponentData(entity, c);
                });
        }

        private static int SetElectricityHubConsumerTarget(EntityManager entityManager, int target)
        {
            var hubs = CollectElectricityHubEntities(entityManager);
            if (hubs.Count == 0)
            {
                AppliedElecHubConsumerByEntity.Clear();
                return 0;
            }

            EnsureElectricityHubComponents(entityManager, hubs, ensureProducer: false, ensureConsumer: true);
            return ReconcileElectricityConsumerTarget(
                ToNativeArray(hubs),
                target,
                AppliedElecHubConsumerByEntity,
                entity => entityManager.GetComponentData<ElectricityConsumer>(entity),
                (entity, value) => entityManager.SetComponentData(entity, value));
        }

        private static int SetWaterOutsideEdgeTarget(EntityManager entityManager, int target, bool sewage)
        {
            var edges = CollectWaterOutsideEdges(entityManager);
            return ReconcileEdgeCapacityTarget(
                edges,
                target,
                sewage ? AppliedSewageEdgeByEntity : AppliedWaterEdgeByEntity,
                entity =>
                {
                    var edge = entityManager.GetComponentData<WaterPipeEdge>(entity);
                    return sewage ? edge.m_SewageCapacity : edge.m_FreshCapacity;
                },
                (entity, value) =>
                {
                    var edge = entityManager.GetComponentData<WaterPipeEdge>(entity);
                    if (sewage)
                        edge.m_SewageCapacity = value;
                    else
                        edge.m_FreshCapacity = value;
                    entityManager.SetComponentData(entity, edge);
                });
        }

        private static int ReconcileProducerCapacityTarget(
            Unity.Collections.NativeArray<Entity> entities,
            int target,
            System.Collections.Generic.Dictionary<long, int> appliedByEntity,
            Func<Entity, int> getCapacity,
            Action<Entity, int> setCapacity)
        {
            try
            {
                if (entities.Length == 0)
                {
                    appliedByEntity.Clear();
                    return 0;
                }

                var count = entities.Length;
                var keys = new long[count];
                var current = new int[count];
                var baseCapacity = new int[count];
                var desiredApplied = new int[count];
                var presentKeys = new System.Collections.Generic.HashSet<long>();

                for (var i = 0; i < count; i++)
                {
                    var entity = entities[i];
                    var key = GetEntityKey(entity);
                    keys[i] = key;
                    presentKeys.Add(key);
                    current[i] = Math.Max(0, getCapacity(entity));
                    var prevApplied = appliedByEntity.TryGetValue(key, out var prev) ? prev : 0;
                    baseCapacity[i] = Math.Max(0, current[i] - prevApplied);
                    desiredApplied[i] = 0;
                }

                if (target > 0)
                {
                    desiredApplied[0] = target;
                }
                else if (target < 0)
                {
                    var remaining = -target;
                    for (var i = 0; i < count && remaining > 0; i++)
                    {
                        var removable = Math.Min(baseCapacity[i], remaining);
                        if (removable <= 0)
                            continue;

                        desiredApplied[i] = -removable;
                        remaining -= removable;
                    }
                }

                var totalAppliedChange = 0;
                for (var i = 0; i < count; i++)
                {
                    var nextCapacity = Math.Max(0, baseCapacity[i] + desiredApplied[i]);
                    if (nextCapacity != current[i])
                    {
                        setCapacity(entities[i], nextCapacity);
                    }

                    totalAppliedChange += nextCapacity - current[i];
                    if (desiredApplied[i] == 0)
                        appliedByEntity.Remove(keys[i]);
                    else
                        appliedByEntity[keys[i]] = desiredApplied[i];
                }

                var staleKeys = new System.Collections.Generic.List<long>();
                foreach (var key in appliedByEntity.Keys)
                {
                    if (!presentKeys.Contains(key))
                        staleKeys.Add(key);
                }

                for (var i = 0; i < staleKeys.Count; i++)
                {
                    appliedByEntity.Remove(staleKeys[i]);
                }

                return totalAppliedChange;
            }
            finally
            {
                entities.Dispose();
            }
        }

        private static int ReconcileEdgeCapacityTarget(
            System.Collections.Generic.List<Entity> edges,
            int target,
            System.Collections.Generic.Dictionary<long, int> appliedByEntity,
            Func<Entity, int> getCapacity,
            Action<Entity, int> setCapacity)
        {
            if (edges == null || edges.Count == 0)
            {
                appliedByEntity.Clear();
                return 0;
            }

            var count = edges.Count;
            var keys = new long[count];
            var current = new int[count];
            var baseCapacity = new int[count];
            var desiredApplied = new int[count];
            var presentKeys = new System.Collections.Generic.HashSet<long>();

            for (var i = 0; i < count; i++)
            {
                var edge = edges[i];
                var key = GetEntityKey(edge);
                keys[i] = key;
                presentKeys.Add(key);
                current[i] = Math.Max(0, getCapacity(edge));
                var prevApplied = appliedByEntity.TryGetValue(key, out var prev) ? prev : 0;
                baseCapacity[i] = Math.Max(0, current[i] - prevApplied);
                desiredApplied[i] = 0;
            }

            if (target > 0)
            {
                desiredApplied[0] = target;
            }
            else if (target < 0)
            {
                var remaining = -target;
                for (var i = 0; i < count && remaining > 0; i++)
                {
                    var removable = Math.Min(baseCapacity[i], remaining);
                    if (removable <= 0)
                        continue;
                    desiredApplied[i] = -removable;
                    remaining -= removable;
                }
            }

            var totalAppliedChange = 0;
            for (var i = 0; i < count; i++)
            {
                var nextCapacity = Math.Max(0, baseCapacity[i] + desiredApplied[i]);
                if (nextCapacity != current[i])
                {
                    setCapacity(edges[i], nextCapacity);
                }

                totalAppliedChange += nextCapacity - current[i];
                if (desiredApplied[i] == 0)
                    appliedByEntity.Remove(keys[i]);
                else
                    appliedByEntity[keys[i]] = desiredApplied[i];
            }

            var staleKeys = new System.Collections.Generic.List<long>();
            foreach (var key in appliedByEntity.Keys)
            {
                if (!presentKeys.Contains(key))
                    staleKeys.Add(key);
            }
            for (var i = 0; i < staleKeys.Count; i++)
            {
                appliedByEntity.Remove(staleKeys[i]);
            }

            return totalAppliedChange;
        }

        private static int ReconcileElectricityConsumerTarget(
            Unity.Collections.NativeArray<Entity> entities,
            int target,
            System.Collections.Generic.Dictionary<long, int> appliedByEntity,
            Func<Entity, ElectricityConsumer> getConsumer,
            Action<Entity, ElectricityConsumer> setConsumer)
        {
            try
            {
                if (entities.Length == 0)
                {
                    appliedByEntity.Clear();
                    return 0;
                }

                var count = entities.Length;
                var keys = new long[count];
                var current = new int[count];
                var baseWanted = new int[count];
                var desiredApplied = new int[count];
                var presentKeys = new System.Collections.Generic.HashSet<long>();

                for (var i = 0; i < count; i++)
                {
                    var entity = entities[i];
                    var key = GetEntityKey(entity);
                    keys[i] = key;
                    presentKeys.Add(key);
                    var consumer = getConsumer(entity);
                    current[i] = Math.Max(0, consumer.m_WantedConsumption);
                    var prevApplied = appliedByEntity.TryGetValue(key, out var prev) ? prev : 0;
                    baseWanted[i] = Math.Max(0, current[i] - prevApplied);
                    desiredApplied[i] = 0;
                }

                if (target > 0)
                {
                    desiredApplied[0] = target;
                }
                else if (target < 0)
                {
                    var remaining = -target;
                    for (var i = 0; i < count && remaining > 0; i++)
                    {
                        var removable = Math.Min(baseWanted[i], remaining);
                        if (removable <= 0)
                            continue;

                        desiredApplied[i] = -removable;
                        remaining -= removable;
                    }
                }

                var totalAppliedChange = 0;
                for (var i = 0; i < count; i++)
                {
                    var nextWanted = Math.Max(0, baseWanted[i] + desiredApplied[i]);
                    if (nextWanted != current[i])
                    {
                        var consumer = getConsumer(entities[i]);
                        consumer.m_WantedConsumption = nextWanted;
                        if (consumer.m_FulfilledConsumption > nextWanted)
                            consumer.m_FulfilledConsumption = nextWanted;
                        setConsumer(entities[i], consumer);
                    }

                    totalAppliedChange += nextWanted - current[i];
                    if (desiredApplied[i] == 0)
                        appliedByEntity.Remove(keys[i]);
                    else
                        appliedByEntity[keys[i]] = desiredApplied[i];
                }

                var staleKeys = new System.Collections.Generic.List<long>();
                foreach (var key in appliedByEntity.Keys)
                {
                    if (!presentKeys.Contains(key))
                        staleKeys.Add(key);
                }

                for (var i = 0; i < staleKeys.Count; i++)
                {
                    appliedByEntity.Remove(staleKeys[i]);
                }

                return totalAppliedChange;
            }
            finally
            {
                entities.Dispose();
            }
        }

        private static System.Collections.Generic.List<Entity> CollectElectricityOutsideEdges(EntityManager entityManager)
        {
            var result = new System.Collections.Generic.List<Entity>();
            var tradeNodes = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<TradeNode>(),
                ComponentType.ReadOnly<ElectricityFlowNode>(),
                ComponentType.ReadOnly<ConnectedFlowEdge>());

            if (tradeNodes.IsEmptyIgnoreFilter)
                return result;

            var nodes = tradeNodes.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (var i = 0; i < nodes.Length; i++)
                {
                    var connected = entityManager.GetBuffer<ConnectedFlowEdge>(nodes[i]);
                    for (var j = 0; j < connected.Length; j++)
                    {
                        var edgeEntity = connected[j].m_Edge;
                        if (!entityManager.HasComponent<ElectricityFlowEdge>(edgeEntity))
                            continue;
                        result.Add(edgeEntity);
                    }
                }
            }
            finally
            {
                nodes.Dispose();
            }

            return result;
        }

        private static System.Collections.Generic.List<Entity> CollectElectricityHubEntities(EntityManager entityManager)
        {
            var result = new System.Collections.Generic.List<Entity>();
            var query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Transformer>(),
                ComponentType.ReadOnly<Game.Prefabs.PrefabRef>());
            if (query.IsEmptyIgnoreFilter)
                return result;

            var hubPrefab = ExchangeHubPrefabBootstrapSystem.ExchangeHubPrefabEntity;
            if (hubPrefab == Entity.Null)
                return result;

            var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var prefabRef = entityManager.GetComponentData<Game.Prefabs.PrefabRef>(entities[i]);
                    if (prefabRef.m_Prefab != hubPrefab)
                        continue;

                    result.Add(entities[i]);
                }
            }
            finally
            {
                entities.Dispose();
            }

            return result;
        }

        private static void EnsureElectricityHubComponents(EntityManager entityManager, System.Collections.Generic.List<Entity> hubs, bool ensureProducer, bool ensureConsumer)
        {
            for (var i = 0; i < hubs.Count; i++)
            {
                var hub = hubs[i];
                if (ensureProducer && !entityManager.HasComponent<ElectricityProducer>(hub))
                {
                    entityManager.AddComponentData(hub, new ElectricityProducer
                    {
                        m_Capacity = 0,
                        m_LastProduction = 0
                    });
                }

                if (ensureConsumer && !entityManager.HasComponent<ElectricityConsumer>(hub))
                {
                    entityManager.AddComponentData(hub, new ElectricityConsumer
                    {
                        m_WantedConsumption = 0,
                        m_FulfilledConsumption = 0,
                        m_CooldownCounter = 0,
                        m_Flags = default
                    });
                }
            }
        }

        private static Unity.Collections.NativeArray<Entity> ToNativeArray(System.Collections.Generic.List<Entity> entities)
        {
            var array = new Unity.Collections.NativeArray<Entity>(entities.Count, Unity.Collections.Allocator.Temp);
            for (var i = 0; i < entities.Count; i++)
            {
                array[i] = entities[i];
            }
            return array;
        }

        private static System.Collections.Generic.List<Entity> CollectWaterOutsideEdges(EntityManager entityManager)
        {
            var result = new System.Collections.Generic.List<Entity>();
            var tradeNodes = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<TradeNode>(),
                ComponentType.ReadOnly<WaterPipeNode>(),
                ComponentType.ReadOnly<ConnectedFlowEdge>());

            if (tradeNodes.IsEmptyIgnoreFilter)
                return result;

            var nodes = tradeNodes.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (var i = 0; i < nodes.Length; i++)
                {
                    var connected = entityManager.GetBuffer<ConnectedFlowEdge>(nodes[i]);
                    for (var j = 0; j < connected.Length; j++)
                    {
                        var edgeEntity = connected[j].m_Edge;
                        if (!entityManager.HasComponent<WaterPipeEdge>(edgeEntity))
                            continue;
                        result.Add(edgeEntity);
                    }
                }
            }
            finally
            {
                nodes.Dispose();
            }

            return result;
        }

        private static long GetEntityKey(Entity entity)
        {
            return ((long)entity.Index << 32) | (uint)entity.Version;
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
