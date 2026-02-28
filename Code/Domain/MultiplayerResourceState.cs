using System;
using Game.Buildings;
using Game.City;
using Game.Simulation;
using Unity.Entities;
using UnityEngine;
using System.Reflection;
using System.Linq;
using System.Text;

namespace MultiSkyLineII
{
    public static class MultiplayerResourceReader
    {
        private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly object UtilitySync = new object();
        private static DateTime LastNoDeltaWarnUtc = DateTime.MinValue;
        private static DateTime LastElectricityDiagUtc = DateTime.MinValue;
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedElecByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedWaterByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedSewageByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedElecEdgeByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedWaterEdgeByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedSewageEdgeByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedElecHubProducerByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedElecHubConsumerByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedElecHubProducerEdgeByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static readonly System.Collections.Generic.Dictionary<long, int> AppliedElecHubConsumerEdgeByEntity = new System.Collections.Generic.Dictionary<long, int>();
        private static Type CachedGarbageSystemType;
        private static string CachedGarbageProductionMember;
        private static string CachedGarbageCapacityMember;
        private static string CachedGarbageProcessedMember;
        private static DateTime LastGarbageDiscoveryLogUtc = DateTime.MinValue;
        private static DateTime NextGarbageScanUtc = DateTime.MinValue;
        private static DateTime NextGarbageDeepDumpUtc = DateTime.MinValue;
        private static int CachedGarbageProductionValue;
        private static int CachedGarbageProcessingCapacityValue;
        private static int CachedGarbageProcessedValue;
        private static bool HasCachedGarbageValues;
        private static DateTime NextGarbageRefreshUtc = DateTime.MinValue;
        private static bool CachedHasElectricityOutsideConnection;
        private static bool CachedHasWaterOutsideConnection;
        private static bool CachedHasSewageOutsideConnection;
        private static DateTime NextOutsideConnectionRefreshUtc = DateTime.MinValue;

        public static bool HasUtilityOverrides()
        {
            lock (UtilitySync)
            {
                return AppliedElecByEntity.Count > 0 ||
                       AppliedWaterByEntity.Count > 0 ||
                       AppliedSewageByEntity.Count > 0 ||
                       AppliedElecEdgeByEntity.Count > 0 ||
                       AppliedWaterEdgeByEntity.Count > 0 ||
                       AppliedSewageEdgeByEntity.Count > 0 ||
                       AppliedElecHubProducerByEntity.Count > 0 ||
                       AppliedElecHubConsumerByEntity.Count > 0 ||
                       AppliedElecHubProducerEdgeByEntity.Count > 0 ||
                       AppliedElecHubConsumerEdgeByEntity.Count > 0;
            }
        }

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
                state.GarbageProduction = HasCachedGarbageValues ? CachedGarbageProductionValue : 0;
                state.GarbageProcessingCapacity = HasCachedGarbageValues ? CachedGarbageProcessingCapacityValue : 0;
                state.GarbageProcessed = HasCachedGarbageValues ? CachedGarbageProcessedValue : 0;

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

                var nowUtc = DateTime.UtcNow;
                if (NextGarbageRefreshUtc == DateTime.MinValue)
                {
                    // Defer first garbage sampling a bit to avoid hitching on the first UI open.
                    NextGarbageRefreshUtc = nowUtc.AddSeconds(4);
                }
                else if (nowUtc >= NextGarbageRefreshUtc)
                {
                    if (TryReadGarbageStats(world, out var garbageProduction, out var garbageProcessingCapacity, out var garbageProcessed))
                    {
                        CachedGarbageProductionValue = garbageProduction;
                        CachedGarbageProcessingCapacityValue = garbageProcessingCapacity;
                        CachedGarbageProcessedValue = garbageProcessed;
                        HasCachedGarbageValues = true;
                        state.GarbageProduction = garbageProduction;
                        state.GarbageProcessingCapacity = garbageProcessingCapacity;
                        state.GarbageProcessed = garbageProcessed;
                        hasData = true;
                    }

                    // Always throttle retries, even when values are zero or unavailable.
                    NextGarbageRefreshUtc = nowUtc.AddSeconds(10);
                }

                if (HasCachedGarbageValues)
                {
                    // Keep the panel responsive: reuse last known values while background refresh waits.
                    hasData = true;
                }

                if (DateTime.UtcNow >= NextOutsideConnectionRefreshUtc)
                {
                    CachedHasElectricityOutsideConnection = HasConnectedElectricityOutside(entityManager);
                    CachedHasWaterOutsideConnection = HasConnectedWaterOutside(entityManager);
                    CachedHasSewageOutsideConnection = HasConnectedSewageOutside(entityManager);
                    NextOutsideConnectionRefreshUtc = DateTime.UtcNow.AddSeconds(3);
                }

                state.HasElectricityOutsideConnection = CachedHasElectricityOutsideConnection;
                state.HasWaterOutsideConnection = CachedHasWaterOutsideConnection;
                state.HasSewageOutsideConnection = CachedHasSewageOutsideConnection;

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
                    var electricityHubCount = CollectElectricityHubEntities(entityManager).Count;
                    var elecImportTarget = Math.Max(0, electricityTarget);
                    var elecExportTarget = Math.Max(0, -electricityTarget);
                    // Apply contracts on dedicated in-city exchange hubs (transformer buildings connected to power grid).
                    var elecProducerDelta = SetElectricityHubProducerTarget(entityManager, elecImportTarget);
                    var elecConsumerDelta = SetElectricityHubConsumerTarget(entityManager, elecExportTarget);
                    appliedElectricity = elecProducerDelta - elecConsumerDelta;
                    var appliedProducerTarget = SumAppliedValues(AppliedElecHubProducerByEntity);
                    var appliedConsumerTarget = SumAppliedValues(AppliedElecHubConsumerByEntity);
                    var appliedProducerEdgeTarget = SumAppliedValues(AppliedElecHubProducerEdgeByEntity);
                    var appliedConsumerEdgeTarget = SumAppliedValues(AppliedElecHubConsumerEdgeByEntity);
                    var producerTargetReached = elecImportTarget == appliedProducerTarget || elecImportTarget == appliedProducerEdgeTarget;
                    var consumerTargetReached = elecExportTarget == appliedConsumerTarget || elecExportTarget == appliedConsumerEdgeTarget;
                    var targetAlreadyApplied = producerTargetReached && consumerTargetReached;
                    if (electricityTarget != 0)
                    {
                        var nowDiag = DateTime.UtcNow;
                        if ((nowDiag - LastElectricityDiagUtc).TotalSeconds >= 5)
                        {
                            LastElectricityDiagUtc = nowDiag;
                            ModDiagnostics.Info(
                                $"Electricity diag target={electricityTarget} hubs={electricityHubCount} appliedDelta={appliedElectricity} producer={appliedProducerTarget} producerEdge={appliedProducerEdgeTarget} consumer={appliedConsumerTarget} consumerEdge={appliedConsumerEdgeTarget}");
                        }
                    }
                    if (electricityTarget != 0 && appliedElectricity == 0 && !targetAlreadyApplied)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - LastNoDeltaWarnUtc).TotalSeconds >= 5)
                        {
                            LastNoDeltaWarnUtc = now;
                            ModDiagnostics.Warn($"Electricity target {electricityTarget} produced no applied delta on hubs. Detected hubs={electricityHubCount}. producer={appliedProducerTarget}, producerEdge={appliedProducerEdgeTarget}, consumer={appliedConsumerTarget}, consumerEdge={appliedConsumerEdgeTarget}");
                        }
                    }

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

        private static int SetElectricityHubProducerTarget(EntityManager entityManager, int target)
        {
            var hubs = CollectElectricityHubEntities(entityManager);
            var injections = CollectElectricityHubInjectionEntities(entityManager, hubs);
            var directEdges = CollectElectricityHubConnectionEdges(entityManager, hubs, producerDirection: true);
            if (injections.Count == 0)
            {
                AppliedElecHubProducerByEntity.Clear();
                return 0;
            }

            EnsureElectricityHubComponents(entityManager, injections, ensureProducer: true, ensureConsumer: false);
            var applied = ApplyHubProducerTargetAbsolute(entityManager, injections, target, AppliedElecHubProducerByEntity);
            var edgeApplied = SetElectricityHubEdgeTarget(entityManager, injections, directEdges, target, AppliedElecHubProducerEdgeByEntity);
            // Force effective production on hub-side entities even when base capacity already equals target.
            ForceHubProducerLastProduction(entityManager, injections, target);
            return applied + edgeApplied;
        }

        private static int SetElectricityHubConsumerTarget(EntityManager entityManager, int target)
        {
            var hubs = CollectElectricityHubEntities(entityManager);
            var injections = CollectElectricityHubInjectionEntities(entityManager, hubs);
            var directEdges = CollectElectricityHubConnectionEdges(entityManager, hubs, producerDirection: false);
            if (injections.Count == 0)
            {
                AppliedElecHubConsumerByEntity.Clear();
                return 0;
            }

            EnsureElectricityHubComponents(entityManager, injections, ensureProducer: false, ensureConsumer: true);
            var applied = ApplyHubConsumerTargetAbsolute(entityManager, injections, target, AppliedElecHubConsumerByEntity);
            var edgeApplied = SetElectricityHubEdgeTarget(entityManager, injections, directEdges, target, AppliedElecHubConsumerEdgeByEntity);
            return applied + edgeApplied;
        }

        private static int ApplyHubProducerTargetAbsolute(
            EntityManager entityManager,
            System.Collections.Generic.List<Entity> injections,
            int target,
            System.Collections.Generic.Dictionary<long, int> appliedByEntity)
        {
            var appliedDelta = 0;
            var positiveTarget = Math.Max(0, target);
            var count = injections.Count;
            var perEntity = count > 0 ? positiveTarget / count : 0;
            var remainder = count > 0 ? positiveTarget - perEntity * count : 0;

            for (var i = 0; i < count; i++)
            {
                var entity = injections[i];
                if (!entityManager.HasComponent<ElectricityProducer>(entity))
                    continue;

                var desired = perEntity + (i == 0 ? remainder : 0);
                var current = entityManager.GetComponentData<ElectricityProducer>(entity);
                var before = Math.Max(0, current.m_Capacity);
                if (before != desired)
                {
                    current.m_Capacity = desired;
                    entityManager.SetComponentData(entity, current);
                }

                appliedDelta += desired - before;
                var key = GetEntityKey(entity);
                if (desired == 0)
                    appliedByEntity.Remove(key);
                else
                    appliedByEntity[key] = desired;
            }

            return appliedDelta;
        }

        private static int ApplyHubConsumerTargetAbsolute(
            EntityManager entityManager,
            System.Collections.Generic.List<Entity> injections,
            int target,
            System.Collections.Generic.Dictionary<long, int> appliedByEntity)
        {
            var appliedDelta = 0;
            var positiveTarget = Math.Max(0, target);
            var count = injections.Count;
            var perEntity = count > 0 ? positiveTarget / count : 0;
            var remainder = count > 0 ? positiveTarget - perEntity * count : 0;

            for (var i = 0; i < count; i++)
            {
                var entity = injections[i];
                if (!entityManager.HasComponent<ElectricityConsumer>(entity))
                    continue;

                var desired = perEntity + (i == 0 ? remainder : 0);
                var consumer = entityManager.GetComponentData<ElectricityConsumer>(entity);
                var before = Math.Max(0, consumer.m_WantedConsumption);
                if (before != desired || consumer.m_FulfilledConsumption > desired)
                {
                    consumer.m_WantedConsumption = desired;
                    if (consumer.m_FulfilledConsumption > desired)
                        consumer.m_FulfilledConsumption = desired;
                    entityManager.SetComponentData(entity, consumer);
                }

                appliedDelta += desired - before;
                var key = GetEntityKey(entity);
                if (desired == 0)
                    appliedByEntity.Remove(key);
                else
                    appliedByEntity[key] = desired;
            }

            return appliedDelta;
        }

        private static int SetElectricityHubEdgeTarget(
            EntityManager entityManager,
            System.Collections.Generic.List<Entity> injections,
            System.Collections.Generic.List<Entity> directEdges,
            int target,
            System.Collections.Generic.Dictionary<long, int> appliedByEntity)
        {
            var edges = CollectConnectedElectricityEdges(entityManager, injections);
            if (directEdges.Count > 0)
            {
                var seen = new System.Collections.Generic.HashSet<long>(edges.Select(GetEntityKey));
                for (var i = 0; i < directEdges.Count; i++)
                {
                    var edge = directEdges[i];
                    if (edge == Entity.Null || !entityManager.Exists(edge) || !entityManager.HasComponent<ElectricityFlowEdge>(edge))
                        continue;

                    var key = GetEntityKey(edge);
                    if (seen.Add(key))
                        edges.Add(edge);
                }
            }
            if (edges.Count == 0)
            {
                appliedByEntity.Clear();
                return 0;
            }

            return ReconcileEdgeCapacityTarget(
                edges,
                target,
                appliedByEntity,
                entity => entityManager.GetComponentData<ElectricityFlowEdge>(entity).m_Capacity,
                (entity, value) =>
                {
                    var edge = entityManager.GetComponentData<ElectricityFlowEdge>(entity);
                    edge.m_Capacity = value;
                    entityManager.SetComponentData(entity, edge);
                });
        }

        private static void ForceHubProducerLastProduction(EntityManager entityManager, System.Collections.Generic.List<Entity> injections, int target)
        {
            if (target <= 0 || injections.Count == 0)
                return;

            var perEntity = Math.Max(1, target / injections.Count);
            var remainder = Math.Max(0, target - perEntity * injections.Count);
            for (var i = 0; i < injections.Count; i++)
            {
                var entity = injections[i];
                if (!entityManager.HasComponent<ElectricityProducer>(entity))
                    continue;

                var producer = entityManager.GetComponentData<ElectricityProducer>(entity);
                var forced = perEntity + (i == 0 ? remainder : 0);
                if (producer.m_Capacity < forced)
                    producer.m_Capacity = forced;
                producer.m_LastProduction = forced;
                entityManager.SetComponentData(entity, producer);
            }
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

        private static System.Collections.Generic.List<Entity> CollectElectricityHubEntities(EntityManager entityManager)
        {
            var result = new System.Collections.Generic.List<Entity>();
            var query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<Game.Prefabs.PrefabRef>());
            if (query.IsEmptyIgnoreFilter)
                return result;

            var hubPrefab = ExchangeHubPrefabBootstrapSystem.ExchangeHubPrefabEntity;
            var prefabSystem = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<Game.Prefabs.PrefabSystem>();

            var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var prefabRef = entityManager.GetComponentData<Game.Prefabs.PrefabRef>(entities[i]);
                    var isHub = hubPrefab != Entity.Null && prefabRef.m_Prefab == hubPrefab;
                    if (!isHub && prefabSystem != null && prefabSystem.TryGetPrefab(prefabRef.m_Prefab, out Game.Prefabs.PrefabBase prefab) && prefab != null)
                    {
                        isHub = string.Equals(prefab.name, "MS2 Exchange Hub", StringComparison.OrdinalIgnoreCase);
                    }
                    if (!isHub)
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

        private static System.Collections.Generic.List<Entity> CollectElectricityHubInjectionEntities(EntityManager entityManager, System.Collections.Generic.List<Entity> hubs)
        {
            var result = new System.Collections.Generic.List<Entity>(hubs.Count * 6);
            var seen = new System.Collections.Generic.HashSet<long>();
            for (var i = 0; i < hubs.Count; i++)
            {
                var hub = hubs[i];
                AddInjectionCandidate(entityManager, hub, seen, result);
                AddElectricityConnectionCandidates(entityManager, hub, seen, result);
            }

            return result;
        }

        private static void AddElectricityConnectionCandidates(
            EntityManager entityManager,
            Entity hub,
            System.Collections.Generic.HashSet<long> seen,
            System.Collections.Generic.List<Entity> result)
        {
            if (!entityManager.HasComponent<ElectricityBuildingConnection>(hub))
                return;

            var c = entityManager.GetComponentData<ElectricityBuildingConnection>(hub);
            AddInjectionCandidate(entityManager, c.m_TransformerNode, seen, result);
            AddEdgeEndpointNodes(entityManager, c.m_ProducerEdge, seen, result);
            AddEdgeEndpointNodes(entityManager, c.m_ConsumerEdge, seen, result);
            AddEdgeEndpointNodes(entityManager, c.m_ChargeEdge, seen, result);
            AddEdgeEndpointNodes(entityManager, c.m_DischargeEdge, seen, result);
        }

        private static void AddEdgeEndpointNodes(
            EntityManager entityManager,
            Entity edgeEntity,
            System.Collections.Generic.HashSet<long> seen,
            System.Collections.Generic.List<Entity> result)
        {
            if (edgeEntity == Entity.Null || !entityManager.Exists(edgeEntity) || !entityManager.HasComponent<ElectricityFlowEdge>(edgeEntity))
                return;

            var edge = entityManager.GetComponentData<ElectricityFlowEdge>(edgeEntity);
            AddInjectionCandidate(entityManager, edge.m_Start, seen, result);
            AddInjectionCandidate(entityManager, edge.m_End, seen, result);
        }

        private static System.Collections.Generic.List<Entity> CollectElectricityHubConnectionEdges(
            EntityManager entityManager,
            System.Collections.Generic.List<Entity> hubs,
            bool producerDirection)
        {
            var result = new System.Collections.Generic.List<Entity>(hubs.Count * 2);
            var seen = new System.Collections.Generic.HashSet<long>();

            for (var i = 0; i < hubs.Count; i++)
            {
                var hub = hubs[i];
                if (!entityManager.HasComponent<ElectricityBuildingConnection>(hub))
                    continue;

                var c = entityManager.GetComponentData<ElectricityBuildingConnection>(hub);
                if (producerDirection)
                {
                    AddConnectionEdge(entityManager, c.m_ProducerEdge, seen, result);
                    AddConnectionEdge(entityManager, c.m_DischargeEdge, seen, result);
                }
                else
                {
                    AddConnectionEdge(entityManager, c.m_ConsumerEdge, seen, result);
                    AddConnectionEdge(entityManager, c.m_ChargeEdge, seen, result);
                }
            }

            return result;
        }

        private static void AddConnectionEdge(
            EntityManager entityManager,
            Entity edgeEntity,
            System.Collections.Generic.HashSet<long> seen,
            System.Collections.Generic.List<Entity> result)
        {
            if (edgeEntity == Entity.Null || !entityManager.Exists(edgeEntity) || !entityManager.HasComponent<ElectricityFlowEdge>(edgeEntity))
                return;

            var key = GetEntityKey(edgeEntity);
            if (!seen.Add(key))
                return;
            result.Add(edgeEntity);
        }

        private static int SumAppliedValues(System.Collections.Generic.Dictionary<long, int> appliedByEntity)
        {
            var sum = 0;
            foreach (var value in appliedByEntity.Values)
            {
                sum += value;
            }

            return sum;
        }

        private static void AddInjectionCandidate(EntityManager entityManager, Entity entity, System.Collections.Generic.HashSet<long> seen, System.Collections.Generic.List<Entity> result)
        {
            if (!entityManager.Exists(entity))
                return;

            var key = GetEntityKey(entity);
            if (!seen.Add(key))
                return;

            result.Add(entity);
        }

        private static System.Collections.Generic.List<Entity> CollectConnectedElectricityEdges(EntityManager entityManager, System.Collections.Generic.List<Entity> nodes)
        {
            var result = new System.Collections.Generic.List<Entity>();
            var seen = new System.Collections.Generic.HashSet<long>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (!entityManager.Exists(node) || !entityManager.HasBuffer<ConnectedFlowEdge>(node))
                    continue;

                var connected = entityManager.GetBuffer<ConnectedFlowEdge>(node);
                for (var j = 0; j < connected.Length; j++)
                {
                    var edgeEntity = connected[j].m_Edge;
                    if (edgeEntity == Entity.Null || !entityManager.Exists(edgeEntity))
                        continue;
                    if (!entityManager.HasComponent<ElectricityFlowEdge>(edgeEntity))
                        continue;

                    var key = GetEntityKey(edgeEntity);
                    if (!seen.Add(key))
                        continue;

                    result.Add(edgeEntity);
                }
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

        private static bool TryReadGarbageStats(World world, out int garbageProduction, out int garbageProcessingCapacity, out int garbageProcessed)
        {
            garbageProduction = 0;
            garbageProcessingCapacity = 0;
            garbageProcessed = 0;

            if (world == null)
                return false;

            // Fast path first to keep UI responsive.
            var hasFast = TryReadGarbageStatsFromEcs(world, out garbageProduction, out garbageProcessingCapacity, out garbageProcessed);
            if (hasFast && garbageProcessed > 0)
                return true;

            // Slow fallback only occasionally when processed value stays unavailable.
            var now = DateTime.UtcNow;
            if (now < NextGarbageScanUtc)
                return hasFast;
            NextGarbageScanUtc = now.AddSeconds(10);

            var hasUiFallback = TryReadGarbageStatsFromUiBindings(world, out var uiProduction, out var uiCapacity, out var uiProcessed);
            if (hasUiFallback)
            {
                if (garbageProduction <= 0 && uiProduction > 0)
                    garbageProduction = uiProduction;
                if (garbageProcessingCapacity <= 0 && uiCapacity > 0)
                    garbageProcessingCapacity = uiCapacity;
                if (garbageProcessed <= 0 && uiProcessed > 0)
                    garbageProcessed = uiProcessed;
            }

            if (garbageProcessed > 0)
                return true;

            var hasScanFallback = TryReadGarbageStatsFromSimulationScan(world, out var scanProduction, out var scanCapacity, out var scanProcessed);
            if (hasScanFallback)
            {
                if (garbageProduction <= 0 && scanProduction > 0)
                    garbageProduction = scanProduction;
                if (garbageProcessingCapacity <= 0 && scanCapacity > 0)
                    garbageProcessingCapacity = scanCapacity;
                if (garbageProcessed <= 0 && scanProcessed > 0)
                    garbageProcessed = scanProcessed;
            }

            if (garbageProcessed <= 0)
            {
                MaybeLogGarbageDeepScan(world);
            }

            return hasFast || hasUiFallback || hasScanFallback;
        }

        private static bool TryReadGarbageStatsFromUiBindings(World world, out int garbageProduction, out int garbageProcessingCapacity, out int garbageProcessed)
        {
            garbageProduction = 0;
            garbageProcessingCapacity = 0;
            garbageProcessed = 0;
            if (world == null)
                return false;

            var uiType = ResolveFirstType("Game.UI.InGame.GarbageInfoviewUISystem");
            if (uiType == null || !TryGetExistingSystemManaged(world, uiType, out var uiSystem) || uiSystem == null)
                return false;

            var hasGarbageRate = TryReadGetterBindingNumeric(uiSystem, "m_GarbageRate", out var garbageRate);
            var hasProcessingRate = TryReadGetterBindingNumeric(uiSystem, "m_ProcessingRate", out var processingRate);
            var hasCapacity = TryReadGetterBindingNumeric(uiSystem, "m_Capacity", out var capacity);
            var hasStored = TryReadGetterBindingNumeric(uiSystem, "m_StoredGarbage", out var stored);

            if (!(hasGarbageRate || hasProcessingRate || hasCapacity || hasStored))
                return false;

            garbageProduction = Mathf.Max(0, Mathf.RoundToInt((float)Math.Max(0d, garbageRate)));
            // "Processed" has no direct integer in this UI system; processing rate is the closest live metric.
            garbageProcessed = Mathf.Max(0, Mathf.RoundToInt((float)Math.Max(0d, processingRate)));
            // Prefer capacity binding; if unavailable, fallback to stored garbage so UI does not stay at zero.
            garbageProcessingCapacity = Mathf.Max(0, Mathf.RoundToInt((float)Math.Max(0d, hasCapacity ? capacity : stored)));

            MaybeLogGarbageDiscovery(
                $"UI garbage fallback: rate={garbageProduction} processing={garbageProcessed} capacity={garbageProcessingCapacity} stored={Mathf.Max(0, Mathf.RoundToInt((float)Math.Max(0d, stored)))}");
            // If UI bindings only report zeros, keep searching through deeper fallbacks.
            return garbageProduction != 0 || garbageProcessingCapacity != 0 || garbageProcessed != 0;
        }

        private static bool TryReadGarbageStatsFromEcs(World world, out int garbageProduction, out int garbageProcessingCapacity, out int garbageProcessed)
        {
            garbageProduction = 0;
            garbageProcessingCapacity = 0;
            garbageProcessed = 0;
            if (world == null)
                return false;

            var entityManager = world.EntityManager;
            var foundAny = false;
            long accumulatedGarbage = 0;
            long facilityProcessingRate = 0;

            var accumulationSystem = world.GetExistingSystemManaged<GarbageAccumulationSystem>();
            if (accumulationSystem != null)
            {
                accumulatedGarbage = Math.Max(0, accumulationSystem.garbageAccumulation);
                foundAny = true;
            }

            // Reliable fallback for "treated/incinerated" amount from facility components.
            var facilityQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<GarbageFacility>());
            if (!facilityQuery.IsEmptyIgnoreFilter)
            {
                var entities = facilityQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
                try
                {
                    for (var i = 0; i < entities.Length; i++)
                    {
                        var facility = entityManager.GetComponentData<GarbageFacility>(entities[i]);
                        facilityProcessingRate += Math.Max(0, facility.m_ProcessingRate);
                    }
                }
                finally
                {
                    entities.Dispose();
                }

                foundAny = true;
            }

            var garbageAiType = ResolveFirstType("Game.Simulation.GarbageFacilityAISystem");
            if (garbageAiType != null && TryGetExistingSystemManaged(world, garbageAiType, out var garbageAiSystem) && garbageAiSystem != null)
            {
                foundAny = true;
                _ = TryReadIntMember(garbageAiSystem, out garbageProduction,
                    "m_GarbageRate", "garbageRate", "m_GarbageProduction", "garbageProduction");
                _ = TryReadIntMember(garbageAiSystem, out garbageProcessed,
                    "m_ProcessingRate", "processingRate",
                    "m_IncinerationRate", "incinerationRate",
                    "m_IncineratedGarbage", "incineratedGarbage", "incinerated",
                    "m_GarbageProcessed", "garbageProcessed");
                _ = TryReadIntMember(garbageAiSystem, out garbageProcessingCapacity,
                    "m_Capacity", "capacity", "m_ProcessingCapacity", "processingCapacity", "m_StoredGarbage", "storedGarbage");
            }

            if (!foundAny)
                return false;

            if (garbageProduction <= 0)
                garbageProduction = (int)Math.Min(int.MaxValue, Math.Max(0, accumulatedGarbage));

            if (garbageProcessed <= 0 && facilityProcessingRate > 0)
                garbageProcessed = (int)Math.Min(int.MaxValue, facilityProcessingRate);

            if (garbageProcessingCapacity <= 0 && facilityProcessingRate > 0)
                garbageProcessingCapacity = (int)Math.Min(int.MaxValue, facilityProcessingRate);

            if (garbageProcessed <= 0 && garbageProduction > 0 && garbageProcessingCapacity > 0)
                garbageProcessed = Math.Min(garbageProduction, garbageProcessingCapacity);

            MaybeLogGarbageDiscovery(
                $"ECS garbage fallback: production={garbageProduction} capacity={garbageProcessingCapacity} processed={garbageProcessed} accum={Math.Min(int.MaxValue, Math.Max(0, accumulatedGarbage))} facilityRate={Math.Min(int.MaxValue, Math.Max(0, facilityProcessingRate))}");

            // Keep cache alive even when values are currently zero.
            return true;
        }

        private static bool TryReadGetterBindingNumeric(object owner, string fieldName, out double value)
        {
            value = 0d;
            if (owner == null || string.IsNullOrWhiteSpace(fieldName))
                return false;

            var flags = AnyInstance;
            object binding = null;
            try
            {
                var field = owner.GetType().GetField(fieldName, flags);
                if (field == null)
                    return false;
                binding = field.GetValue(owner);
            }
            catch
            {
                return false;
            }

            if (binding == null)
                return false;

            if (TryReadNumericFromObject(binding, out value))
                return true;

            if (TryReadMember(binding, out var rawValue, "m_Value", "value") &&
                TryConvertToDouble(rawValue, out value))
                return true;

            if (TryReadMember(binding, out var getter, "m_Getter") && getter is Delegate del)
            {
                try
                {
                    var invoked = del.DynamicInvoke();
                    if (TryConvertToDouble(invoked, out value))
                        return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TryReadNumericFromObject(object target, out double value)
        {
            value = 0d;
            if (target == null)
                return false;

            if (TryConvertToDouble(target, out value))
                return true;

            if (TryReadMember(target, out var v, "value", "Value", "m_Value", "m_value", "amount", "Amount") &&
                TryConvertToDouble(v, out value))
                return true;

            return false;
        }

        private static bool TryConvertToDouble(object raw, out double value)
        {
            value = 0d;
            switch (raw)
            {
                case null:
                    return false;
                case int i:
                    value = i;
                    return true;
                case long l:
                    value = l;
                    return true;
                case short s:
                    value = s;
                    return true;
                case byte b:
                    value = b;
                    return true;
                case float f:
                    value = f;
                    return true;
                case double d:
                    value = d;
                    return true;
                case decimal m:
                    value = (double)m;
                    return true;
            }

            var text = raw.ToString();
            return !string.IsNullOrWhiteSpace(text) &&
                   double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadGarbageValuesFromSystem(object system, out int garbageProduction, out int garbageProcessingCapacity, out int garbageProcessed)
        {
            garbageProduction = 0;
            garbageProcessingCapacity = 0;
            garbageProcessed = 0;
            if (system == null)
                return false;

            var foundAny = false;
            foundAny |= TryReadIntMember(system, out garbageProduction,
                "garbageProduction",
                "wasteProduction",
                "totalGarbageProduction",
                "totalWasteProduction",
                "collectedGarbage",
                "production",
                "m_GarbageProduction",
                "m_WasteProduction",
                "m_TotalGarbageProduction",
                "m_TotalWasteProduction",
                "m_Production");
            foundAny |= TryReadIntMember(system, out garbageProcessingCapacity,
                "garbageProcessingCapacity",
                "wasteProcessingCapacity",
                "totalGarbageProcessingCapacity",
                "totalWasteProcessingCapacity",
                "incinerationCapacity",
                "landfillCapacity",
                "processingCapacity",
                "capacity",
                "m_GarbageProcessingCapacity",
                "m_WasteProcessingCapacity",
                "m_TotalGarbageProcessingCapacity",
                "m_TotalWasteProcessingCapacity",
                "m_IncinerationCapacity",
                "m_LandfillCapacity",
                "m_ProcessingCapacity",
                "m_Capacity");
            foundAny |= TryReadIntMember(system, out garbageProcessed,
                "garbageProcessed",
                "wasteProcessed",
                "totalGarbageProcessed",
                "totalWasteProcessed",
                "incineratedGarbage",
                "incineratedWaste",
                "totalIncineratedGarbage",
                "incinerationRate",
                "incinerated",
                "collected",
                "handled",
                "processed",
                "fulfilledConsumption",
                "m_GarbageProcessed",
                "m_WasteProcessed",
                "m_TotalGarbageProcessed",
                "m_TotalWasteProcessed",
                "m_IncineratedGarbage",
                "m_IncineratedWaste",
                "m_TotalIncineratedGarbage",
                "m_IncinerationRate",
                "m_Incinerated",
                "m_Collected",
                "m_Handled",
                "m_Processed",
                "m_FulfilledConsumption");

            return foundAny;
        }

        private static bool TryReadGarbageStatsFromSimulationScan(World world, out int garbageProduction, out int garbageProcessingCapacity, out int garbageProcessed)
        {
            garbageProduction = 0;
            garbageProcessingCapacity = 0;
            garbageProcessed = 0;
            if (world == null)
                return false;

            var bestScore = int.MinValue;
            var foundAny = false;
            Type bestType = null;
            string bestProductionMember = null;
            string bestCapacityMember = null;
            string bestProcessedMember = null;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                Type[] types;
                try
                {
                    types = assemblies[i].GetTypes();
                }
                catch
                {
                    continue;
                }

                for (var j = 0; j < types.Length; j++)
                {
                    var type = types[j];
                    if (type == null)
                        continue;

                    var ns = type.Namespace ?? string.Empty;
                    if (!ns.StartsWith("Game.Simulation", StringComparison.Ordinal))
                        continue;

                    var lowerName = (type.FullName ?? string.Empty).ToLowerInvariant();
                    if (!(lowerName.Contains("garbage") || lowerName.Contains("waste") || lowerName.Contains("landfill") || lowerName.Contains("inciner")))
                        continue;

                    if (!TryGetExistingSystemManaged(world, type, out var system))
                        continue;

                    var discoveredProduction = string.Empty;
                    var discoveredCapacity = string.Empty;
                    var discoveredProcessed = string.Empty;
                    var candidateFound = TryReadGarbageValuesFromSystem(system, out var production, out var capacity, out var processed);
                    if (!candidateFound &&
                        !TryDiscoverGarbageMemberNames(system, out discoveredProduction, out discoveredCapacity, out discoveredProcessed))
                    {
                        continue;
                    }

                    if (!candidateFound)
                    {
                        candidateFound |= TryReadIntMember(system, out production, discoveredProduction);
                        candidateFound |= TryReadIntMember(system, out capacity, discoveredCapacity);
                        candidateFound |= TryReadIntMember(system, out processed, discoveredProcessed);
                    }

                    if (!candidateFound)
                        continue;

                    var score = Math.Abs(production) + Math.Abs(capacity) + Math.Abs(processed);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        garbageProduction = production;
                        garbageProcessingCapacity = capacity;
                        garbageProcessed = processed;
                        foundAny = true;
                        bestType = type;
                        TryDiscoverGarbageMemberNames(system, out bestProductionMember, out bestCapacityMember, out bestProcessedMember);
                    }
                }
            }

            if (!foundAny)
                return false;

            CachedGarbageSystemType = bestType;
            if (!string.IsNullOrWhiteSpace(bestProductionMember))
                CachedGarbageProductionMember = bestProductionMember;
            if (!string.IsNullOrWhiteSpace(bestCapacityMember))
                CachedGarbageCapacityMember = bestCapacityMember;
            if (!string.IsNullOrWhiteSpace(bestProcessedMember))
                CachedGarbageProcessedMember = bestProcessedMember;

            garbageProduction = Math.Max(0, garbageProduction);
            garbageProcessingCapacity = Math.Max(0, garbageProcessingCapacity);
            garbageProcessed = Math.Max(0, garbageProcessed);
            MaybeLogGarbageDiscovery($"Fallback scan selected {bestType?.FullName ?? "<unknown>"} with prod={garbageProduction} cap={garbageProcessingCapacity} processed={garbageProcessed}");
            return true;
        }

        private static Type ResolveFirstType(params string[] typeNames)
        {
            if (typeNames == null || typeNames.Length == 0)
                return null;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                var assembly = assemblies[i];
                try
                {
                    for (var j = 0; j < typeNames.Length; j++)
                    {
                        var type = assembly.GetType(typeNames[j]);
                        if (type != null)
                            return type;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static bool TryGetExistingSystemManaged(World world, Type type, out object system)
        {
            system = null;
            if (world == null || type == null)
                return false;

            try
            {
                var getter = typeof(World).GetMethod("GetExistingSystemManaged", AnyInstance, null, new[] { typeof(Type) }, null);
                if (getter == null)
                    return false;

                system = getter.Invoke(world, new object[] { type });
                return system != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDiscoverGarbageSystem(World world, out Type discoveredType, out object discoveredSystem)
        {
            discoveredType = null;
            discoveredSystem = null;
            if (world == null)
                return false;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                var assembly = assemblies[i];
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                for (var j = 0; j < types.Length; j++)
                {
                    var type = types[j];
                    if (type == null)
                        continue;

                    var fullName = type.FullName ?? string.Empty;
                    var ns = type.Namespace ?? string.Empty;
                    if (!ns.StartsWith("Game.Simulation", StringComparison.Ordinal))
                        continue;

                    var lower = fullName.ToLowerInvariant();
                    if (!(lower.Contains("garbage") || lower.Contains("waste")))
                        continue;

                    if (!TryGetExistingSystemManaged(world, type, out var system))
                        continue;

                    discoveredType = type;
                    discoveredSystem = system;
                    MaybeLogGarbageDiscovery($"Discovered garbage system type: {fullName}");
                    return true;
                }
            }

            return false;
        }

        private static bool TryDiscoverGarbageMemberNames(object target, out string productionMember, out string capacityMember, out string processedMember)
        {
            productionMember = null;
            capacityMember = null;
            processedMember = null;
            if (target == null)
                return false;

            var type = target.GetType();
            var members = new System.Collections.Generic.List<string>();
            var flags = AnyInstance;

            try
            {
                var properties = type.GetProperties(flags);
                for (var i = 0; i < properties.Length; i++)
                {
                    var p = properties[i];
                    if (p == null || p.GetIndexParameters().Length != 0)
                        continue;
                    var t = p.PropertyType;
                    if (t == typeof(int) || t == typeof(long) || t == typeof(float) || t == typeof(double) || t == typeof(decimal) || t == typeof(short) || t == typeof(byte))
                    {
                        members.Add(p.Name);
                    }
                }
            }
            catch
            {
            }

            try
            {
                var fields = type.GetFields(flags);
                for (var i = 0; i < fields.Length; i++)
                {
                    var f = fields[i];
                    var t = f.FieldType;
                    if (t == typeof(int) || t == typeof(long) || t == typeof(float) || t == typeof(double) || t == typeof(decimal) || t == typeof(short) || t == typeof(byte))
                    {
                        members.Add(f.Name);
                    }
                }
            }
            catch
            {
            }

            var typeName = (type.FullName ?? string.Empty).ToLowerInvariant();
            var typeLooksGarbage = typeName.Contains("garbage") || typeName.Contains("waste") || typeName.Contains("landfill") || typeName.Contains("inciner");
            for (var i = 0; i < members.Count; i++)
            {
                var name = members[i];
                var n = name.ToLowerInvariant();
                var hasWasteWord = n.Contains("garbage") || n.Contains("waste");
                var hasProductionWord = n.Contains("prod") || n.Contains("generate");
                var hasCapacityWord = n.Contains("cap") || n.Contains("process") || n.Contains("throughput") || n.Contains("limit");
                var hasProcessedWord = n.Contains("processed") || n.Contains("fulfilled") || n.Contains("handled") || n.Contains("collected") || n.Contains("inciner");
                var isEligible = hasWasteWord || typeLooksGarbage;
                if (!isEligible)
                    continue;

                if (productionMember == null && hasProductionWord)
                    productionMember = name;
                if (capacityMember == null && hasCapacityWord)
                    capacityMember = name;
                if (processedMember == null && hasProcessedWord)
                    processedMember = name;
            }

            return productionMember != null || capacityMember != null || processedMember != null;
        }

        private static void MaybeLogGarbageDiscovery(string message)
        {
            var now = DateTime.UtcNow;
            if ((now - LastGarbageDiscoveryLogUtc).TotalSeconds < 15)
                return;
            LastGarbageDiscoveryLogUtc = now;
            ModDiagnostics.Write($"Garbage stats discovery: {message}");
        }

        private static void MaybeLogGarbageDeepScan(World world)
        {
            if (world == null)
                return;

            var now = DateTime.UtcNow;
            if (now < NextGarbageDeepDumpUtc)
                return;
            NextGarbageDeepDumpUtc = now.AddSeconds(20);

            try
            {
                var systemsLogged = 0;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (var i = 0; i < assemblies.Length; i++)
                {
                    Type[] types;
                    try
                    {
                        types = assemblies[i].GetTypes();
                    }
                    catch
                    {
                        continue;
                    }

                    for (var j = 0; j < types.Length; j++)
                    {
                        var type = types[j];
                        if (type == null)
                            continue;

                        var ns = type.Namespace ?? string.Empty;
                        if (!ns.StartsWith("Game.Simulation", StringComparison.Ordinal))
                            continue;

                        var lowerType = (type.FullName ?? string.Empty).ToLowerInvariant();
                        if (!(lowerType.Contains("garbage") || lowerType.Contains("waste") || lowerType.Contains("inciner") || lowerType.Contains("landfill")))
                            continue;

                        if (!TryGetExistingSystemManaged(world, type, out var system) || system == null)
                            continue;

                        var sb = new StringBuilder(512);
                        sb.Append("Garbage deep scan ").Append(type.FullName).Append(": ");
                        var members = type.GetMembers(AnyInstance)
                            .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property)
                            .Select(m => m.Name)
                            .Distinct(StringComparer.Ordinal)
                            .Where(name =>
                            {
                                var n = name.ToLowerInvariant();
                                return n.Contains("garbage") || n.Contains("waste") || n.Contains("inciner") || n.Contains("landfill") ||
                                       n.Contains("process") || n.Contains("capacity") || n.Contains("rate") || n.Contains("stored") ||
                                       n.Contains("accum") || n.Contains("collec") || n.Contains("handled");
                            })
                            .Take(40)
                            .ToList();

                        var foundAny = false;
                        for (var k = 0; k < members.Count; k++)
                        {
                            if (!TryReadMember(system, out var raw, members[k]))
                                continue;
                            if (!TryConvertToDouble(raw, out var numeric))
                                continue;

                            if (foundAny)
                                sb.Append(" | ");

                            sb.Append(members[k]).Append('=').Append(numeric.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                            foundAny = true;
                        }

                        if (!foundAny)
                        {
                            sb.Append("<no numeric members readable>");
                        }

                        ModDiagnostics.Write(sb.ToString());
                        systemsLogged++;
                        if (systemsLogged >= 6)
                            return;
                    }
                }

                if (systemsLogged == 0)
                {
                    ModDiagnostics.Write("Garbage deep scan: no matching simulation system found.");
                }
            }
            catch (Exception e)
            {
                ModDiagnostics.Write($"Garbage deep scan failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static bool TryReadIntMember(object target, out int value, params string[] memberNames)
        {
            value = 0;
            if (!TryReadMember(target, out var raw, memberNames))
                return false;

            switch (raw)
            {
                case int i:
                    value = i;
                    return true;
                case long l:
                    if (l > int.MaxValue) value = int.MaxValue;
                    else if (l < int.MinValue) value = int.MinValue;
                    else value = (int)l;
                    return true;
                case float f:
                    value = Mathf.RoundToInt(f);
                    return true;
                case double d:
                    value = Mathf.RoundToInt((float)d);
                    return true;
                case decimal m:
                    value = Mathf.RoundToInt((float)m);
                    return true;
                case short s:
                    value = s;
                    return true;
                case byte b:
                    value = b;
                    return true;
            }

            if (raw != null && int.TryParse(raw.ToString(), out var parsed))
            {
                value = parsed;
                return true;
            }

            return false;
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
