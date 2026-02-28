using Game;
using Game.Buildings;
using Game.Prefabs;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace MultiSkyLineII
{
    public sealed partial class BuildingPlacementDiagnosticsSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private bool _initialized;
        private readonly HashSet<long> _seenBuildings = new HashSet<long>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        }

        protected override void OnUpdate()
        {
            var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<PrefabRef>());

            if (query.IsEmptyIgnoreFilter)
                return;

            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                if (!_initialized)
                {
                    for (var i = 0; i < entities.Length; i++)
                    {
                        _seenBuildings.Add(GetEntityKey(entities[i]));
                    }

                    _initialized = true;
                    ModDiagnostics.Write($"BuildingPlacementDiagnostics initialized with {_seenBuildings.Count} existing buildings.");
                    return;
                }

                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    var key = GetEntityKey(entity);
                    if (_seenBuildings.Contains(key))
                        continue;

                    _seenBuildings.Add(key);
                    TryLogBuildingPlacement(entity);
                }
            }
        }

        private void TryLogBuildingPlacement(Entity entity)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity))
                return;

            var prefabRef = EntityManager.GetComponentData<PrefabRef>(entity);
            if (!_prefabSystem.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab) || prefab == null)
                return;

            var hasProducer = _prefabSystem.HasComponent<ElectricityProducer>(prefab);
            var hasConsumer = _prefabSystem.HasComponent<ElectricityConsumer>(prefab);
            var hasTransformer = _prefabSystem.HasComponent<TransformerData>(prefab);
            var hasAnyElectricity = hasProducer || hasConsumer || hasTransformer;
            var lowerName = (prefab.name ?? string.Empty).ToLowerInvariant();
            var nameLooksRelevant =
                lowerName.Contains("battery") ||
                lowerName.Contains("accumulator") ||
                lowerName.Contains("storage") ||
                lowerName.Contains("power") ||
                lowerName.Contains("transformer") ||
                lowerName.Contains("substation");

            if (!hasAnyElectricity && !nameLooksRelevant)
                return;

            ModDiagnostics.Write(
                $"Placed building entity={entity} prefab='{prefab.name}' producer={hasProducer} consumer={hasConsumer} transformer={hasTransformer}");

        }

        private static long GetEntityKey(Entity entity)
        {
            return ((long)entity.Index << 32) | (uint)entity.Version;
        }
    }
}
