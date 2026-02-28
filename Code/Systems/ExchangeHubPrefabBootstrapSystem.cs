using Game;
using Game.Buildings;
using Game.Prefabs;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace MultiSkyLineII
{
    public sealed partial class ExchangeHubPrefabBootstrapSystem : GameSystemBase
    {
        private const string ExchangeHubPrefabName = "MS2 Exchange Hub";
        private const string PreferredBatteryPrefabName = "EmergencyBatteryStation01";

        private PrefabSystem _prefabSystem;
        private bool _attemptedRegistration;
        private double _nextRetryTime;

        public static Entity ExchangeHubPrefabEntity { get; private set; } = Entity.Null;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        }

        protected override void OnUpdate()
        {
            if (_attemptedRegistration)
                return;
            var now = World.Time.ElapsedTime;
            if (now < _nextRetryTime)
                return;

            _attemptedRegistration = true;

            var desiredId = new PrefabID(nameof(BuildingPrefab), ExchangeHubPrefabName);
            if (_prefabSystem.TryGetPrefab(desiredId, out var existing) &&
                _prefabSystem.TryGetEntity(existing, out var existingEntity))
            {
                ExchangeHubPrefabEntity = existingEntity;
                ModDiagnostics.Write($"ExchangeHub prefab already available: {ExchangeHubPrefabEntity}");
                return;
            }

            if (!TryGetPreferredBatteryTemplate(out var preferredTemplate))
            {
                ModDiagnostics.Write(
                    $"ExchangeHub prefab registration skipped: preferred battery template '{PreferredBatteryPrefabName}' not found yet.");
                _attemptedRegistration = false;
                _nextRetryTime = now + 1.0;
                return;
            }

            ModDiagnostics.Write($"ExchangeHub trying battery template '{preferredTemplate.name}'");
            if (TryCreateExchangeHubFromTemplate(preferredTemplate))
                return;

            ModDiagnostics.Write("ExchangeHub prefab registration failed: no compatible electricity BuildingPrefab template found.");
            _attemptedRegistration = false;
            _nextRetryTime = now + 1.0;
        }

        private bool TryCreateExchangeHubFromTemplate(BuildingPrefab template)
        {
            var duplicate = _prefabSystem.DuplicatePrefab(template, ExchangeHubPrefabName) as BuildingPrefab;
            if (duplicate == null)
                return false;

            duplicate.active = true;
            EnsureComponentCopied<UIObjectData>(template, duplicate);
            EnsureComponentCopied<PlaceableObjectData>(template, duplicate);
            EnsureComponentCopied<ServiceObjectData>(template, duplicate);
            EnsureComponentCopied<UtilityObjectData>(template, duplicate);

            // Ensure the hub is always placeable in sandbox/testing contexts.
            RemoveIfExists<UnlockRequirementData>(duplicate);
            RemoveIfExists<ObjectBuiltRequirementData>(duplicate);
            RemoveIfExists<StrictObjectBuiltRequirementData>(duplicate);

            if (!_prefabSystem.AddPrefab(duplicate))
            {
                ModDiagnostics.Write($"ExchangeHub AddPrefab returned false for template '{template.name}', trying AddOrUpdatePrefab.");
                try
                {
                    _prefabSystem.AddOrUpdatePrefab(duplicate);
                }
                catch (System.Exception e)
                {
                    ModDiagnostics.Write($"ExchangeHub AddOrUpdatePrefab exception: {e.Message}");
                    return false;
                }
            }

            if (_prefabSystem.TryGetEntity(duplicate, out var hubPrefabEntity))
            {
                ExchangeHubPrefabEntity = hubPrefabEntity;
                ModDiagnostics.Write($"ExchangeHub prefab created from template '{template.name}': {ExchangeHubPrefabEntity}");
            }
            else
            {
                var desiredId = new PrefabID(nameof(BuildingPrefab), ExchangeHubPrefabName);
                if (_prefabSystem.TryGetPrefab(desiredId, out var resolvedPrefab) &&
                    _prefabSystem.TryGetEntity(resolvedPrefab, out hubPrefabEntity))
                {
                    ExchangeHubPrefabEntity = hubPrefabEntity;
                    ModDiagnostics.Write($"ExchangeHub prefab resolved by ID after registration: {ExchangeHubPrefabEntity}");
                }
                else
                {
                    ModDiagnostics.Write("ExchangeHub prefab registration warning: prefab entity not resolved after registration.");
                    return false;
                }
            }

            return true;
        }

        private bool TryGetPreferredBatteryTemplate(out BuildingPrefab template)
        {
            template = null;

            var preferredId = new PrefabID(nameof(BuildingPrefab), PreferredBatteryPrefabName);
            if (_prefabSystem.TryGetPrefab(preferredId, out var byId) && byId is BuildingPrefab byIdBuilding)
            {
                template = byIdBuilding;
                return true;
            }

            // Broad prefab lookup over all building-prefab entities currently loaded.
            var buildingPrefabQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<BuildingData>());
            if (!buildingPrefabQuery.IsEmptyIgnoreFilter)
            {
                using (var candidates = buildingPrefabQuery.ToEntityArray(Allocator.Temp))
                {
                    for (var i = 0; i < candidates.Length; i++)
                    {
                        if (!_prefabSystem.TryGetPrefab(candidates[i], out PrefabBase candidateBase) ||
                            candidateBase is not BuildingPrefab candidateBuilding)
                            continue;
                        if (!string.Equals(candidateBuilding.name, PreferredBatteryPrefabName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        template = candidateBuilding;
                        ModDiagnostics.Write($"ExchangeHub resolved preferred template from BuildingData query: '{candidateBuilding.name}'.");
                        return true;
                    }
                }
            }

            var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<UIObjectData>(),
                ComponentType.ReadOnly<PlaceableObjectData>());
            if (query.IsEmptyIgnoreFilter)
                return false;

            using (var candidates = query.ToEntityArray(Allocator.Temp))
            {
                for (var i = 0; i < candidates.Length; i++)
                {
                    if (!_prefabSystem.TryGetPrefab(candidates[i], out BuildingPrefab candidate) || candidate == null)
                        continue;
                    if (!string.Equals(candidate.name, PreferredBatteryPrefabName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    template = candidate;
                    return true;
                }
            }

            return false;
        }

        private void EnsureComponentCopied<T>(PrefabBase template, PrefabBase target)
            where T : unmanaged, IComponentData
        {
            if (_prefabSystem.HasComponent<T>(target))
                return;

            if (_prefabSystem.TryGetComponentData<T>(template, out var data))
            {
                _prefabSystem.AddComponentData(target, data);
            }
        }

        private void RemoveIfExists<T>(PrefabBase prefab)
            where T : unmanaged, IComponentData
        {
            if (_prefabSystem.HasComponent<T>(prefab))
            {
                _prefabSystem.RemoveComponent<T>(prefab);
            }
        }
    }
}
