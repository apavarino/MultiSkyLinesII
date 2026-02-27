using Game;
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

        private PrefabSystem _prefabSystem;
        private bool _attemptedRegistration;

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

            _attemptedRegistration = true;

            var desiredId = new PrefabID(nameof(BuildingPrefab), ExchangeHubPrefabName);
            if (_prefabSystem.TryGetPrefab(desiredId, out var existing) &&
                _prefabSystem.TryGetEntity(existing, out var existingEntity))
            {
                ExchangeHubPrefabEntity = existingEntity;
                ModDiagnostics.Write($"ExchangeHub prefab already available: {ExchangeHubPrefabEntity}");
                return;
            }

            var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TransformerData>());
            if (query.IsEmptyIgnoreFilter)
            {
                ModDiagnostics.Write("ExchangeHub prefab registration skipped: no TransformerData template found yet.");
                _attemptedRegistration = false;
                return;
            }

            BuildingPrefab fallbackTemplateForRuntimeUse = null;
            var templates = new List<BuildingPrefab>(32);

            using (var candidates = query.ToEntityArray(Allocator.Temp))
            {
                for (var i = 0; i < candidates.Length; i++)
                {
                    if (!_prefabSystem.TryGetPrefab(candidates[i], out BuildingPrefab template) || template == null)
                        continue;

                    if (!_prefabSystem.HasComponent<UIObjectData>(template) || !_prefabSystem.HasComponent<PlaceableObjectData>(template))
                        continue;

                    if (fallbackTemplateForRuntimeUse == null)
                        fallbackTemplateForRuntimeUse = template;
                    templates.Add(template);
                }
            }

            if (templates.Count > 0)
            {
                templates.Sort((a, b) => ScoreTemplate(b).CompareTo(ScoreTemplate(a)));

                for (var i = 0; i < templates.Count; i++)
                {
                    if (TryCreateExchangeHubFromTemplate(templates[i]))
                        return;
                }
            }

            if (fallbackTemplateForRuntimeUse != null && _prefabSystem.TryGetEntity(fallbackTemplateForRuntimeUse, out var fallbackEntity))
            {
                ExchangeHubPrefabEntity = fallbackEntity;
                ModDiagnostics.Write(
                    $"ExchangeHub fallback active: using vanilla transformer prefab '{fallbackTemplateForRuntimeUse.name}' entity={ExchangeHubPrefabEntity}. " +
                    "Custom prefab registration failed, but hub logic will work with this electricity-menu transformer.");
                return;
            }

            ModDiagnostics.Write("ExchangeHub prefab registration failed: no compatible BuildingPrefab template found.");
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

        private static int ScoreTemplate(BuildingPrefab template)
        {
            var name = (template.name ?? string.Empty).ToLowerInvariant();
            var score = 0;

            if (name.Contains("transformer"))
                score += 100;
            if (name.Contains("high") || name.Contains("hv"))
                score += 50;
            if (name.Contains("substation"))
                score += 40;
            if (name.Contains("sub") || name.Contains("dummy") || name.Contains("marker") || name.Contains("editor"))
                score -= 120;

            return score;
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
