using System;
using System.Collections.Generic;

namespace MultiSkyLineII
{
    internal static class MultiplayerContractRules
    {
        public static void CleanupExpiredProposals(List<MultiplayerContractProposal> proposals, DateTime nowUtc, int timeoutSeconds)
        {
            proposals.RemoveAll(p => IsExpired(p, nowUtc, timeoutSeconds));
        }

        public static bool TryApplyProposalDecision(
            List<MultiplayerContractProposal> pendingProposals,
            List<MultiplayerContract> contracts,
            string proposalId,
            string actorCity,
            bool accept,
            Func<string, string> normalizePlayerName,
            Func<string, bool> playerExists,
            Action<string> addDebugLog,
            int proposalTimeoutSeconds,
            out string error)
        {
            error = null;
            CleanupExpiredProposals(pendingProposals, DateTime.UtcNow, proposalTimeoutSeconds);

            var index = pendingProposals.FindIndex(p => string.Equals(p.Id, proposalId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                error = "Proposition introuvable ou expiree.";
                return false;
            }

            var proposal = pendingProposals[index];
            var isPublicOffer = string.IsNullOrWhiteSpace(proposal.BuyerPlayer);
            if (isPublicOffer)
            {
                if (string.Equals(proposal.SellerPlayer, actorCity, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Le vendeur ne peut pas accepter sa propre offre.";
                    return false;
                }
            }
            else if (!string.Equals(proposal.BuyerPlayer, actorCity, StringComparison.OrdinalIgnoreCase))
            {
                error = "Seul le joueur acheteur peut repondre.";
                return false;
            }

            if (!accept)
            {
                if (isPublicOffer)
                {
                    addDebugLog?.Invoke($"Public proposal {proposalId} ignored by {actorCity}");
                    return true;
                }

                pendingProposals.RemoveAt(index);
                addDebugLog?.Invoke($"Proposal {proposalId} refused by {actorCity}");
                return true;
            }

            var resolvedBuyer = isPublicOffer ? normalizePlayerName(actorCity) : proposal.BuyerPlayer;
            if (!playerExists(proposal.SellerPlayer) || !playerExists(resolvedBuyer))
            {
                error = "Joueurs introuvables au moment de l'acceptation.";
                return false;
            }

            pendingProposals.RemoveAt(index);
            contracts.Add(new MultiplayerContract
            {
                Id = Guid.NewGuid().ToString("N"),
                SellerPlayer = proposal.SellerPlayer,
                BuyerPlayer = resolvedBuyer,
                Resource = proposal.Resource,
                UnitsPerTick = proposal.UnitsPerTick,
                EffectiveUnitsPerTick = 0,
                PricePerTick = proposal.PricePerTick,
                CreatedUtc = DateTime.UtcNow
            });
            addDebugLog?.Invoke($"Proposal {proposalId} accepted by {resolvedBuyer}, contract active.");
            return true;
        }

        public static bool TryCancelContract(
            List<MultiplayerContract> contracts,
            Dictionary<string, int> contractFailureCounts,
            string contractId,
            string actorPlayer,
            Action<string> addDebugLog,
            out string error)
        {
            error = null;
            var index = contracts.FindIndex(c => string.Equals(c.Id, contractId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                error = "Contrat introuvable.";
                return false;
            }

            var contract = contracts[index];
            if (!string.Equals(contract.SellerPlayer, actorPlayer, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(contract.BuyerPlayer, actorPlayer, StringComparison.OrdinalIgnoreCase))
            {
                error = "Seuls le vendeur ou l'acheteur peuvent annuler ce contrat.";
                return false;
            }

            contracts.RemoveAt(index);
            contractFailureCounts.Remove(contract.Id);
            addDebugLog?.Invoke($"Contract {contractId} cancelled by {actorPlayer}.");
            return true;
        }

        public static int GetSellerAvailable(MultiplayerContractResource resource, MultiplayerResourceState state)
        {
            switch (resource)
            {
                case MultiplayerContractResource.Electricity:
                    return Math.Max(0, state.ElectricityProduction - state.ElectricityConsumption);
                case MultiplayerContractResource.FreshWater:
                    return Math.Max(0, state.FreshWaterCapacity - state.FreshWaterConsumption);
                case MultiplayerContractResource.Sewage:
                    return Math.Max(0, state.SewageCapacity - state.SewageConsumption);
                default:
                    return 0;
            }
        }

        public static bool CanUseTransferInfrastructure(MultiplayerResourceState state, MultiplayerContractResource resource)
        {
            switch (resource)
            {
                case MultiplayerContractResource.Electricity:
                    return true;
                case MultiplayerContractResource.FreshWater:
                    return state.HasWaterOutsideConnection;
                case MultiplayerContractResource.Sewage:
                    return state.HasSewageOutsideConnection;
                default:
                    return false;
            }
        }

        public static int GetCommittedOutgoingUnits(
            IReadOnlyList<MultiplayerContract> contracts,
            string sellerPlayer,
            MultiplayerContractResource resource,
            Func<string, string> normalizePlayerName)
        {
            if (string.IsNullOrWhiteSpace(sellerPlayer))
                return 0;

            var normalizedSeller = normalizePlayerName(sellerPlayer);
            var total = 0;
            for (var i = 0; i < contracts.Count; i++)
            {
                var c = contracts[i];
                if (c.Resource != resource)
                    continue;

                if (!string.Equals(c.SellerPlayer, normalizedSeller, StringComparison.OrdinalIgnoreCase))
                    continue;

                total += Math.Max(0, c.UnitsPerTick);
            }

            return total;
        }

        public static void ApplyResourceTransfer(MultiplayerContractResource resource, ref MultiplayerResourceState seller, ref MultiplayerResourceState buyer, int units)
        {
            switch (resource)
            {
                case MultiplayerContractResource.Electricity:
                    seller.ElectricityProduction = Math.Max(0, seller.ElectricityProduction - units);
                    buyer.ElectricityFulfilledConsumption = Math.Min(buyer.ElectricityConsumption, buyer.ElectricityFulfilledConsumption + units);
                    break;
                case MultiplayerContractResource.FreshWater:
                    seller.FreshWaterCapacity = Math.Max(0, seller.FreshWaterCapacity - units);
                    buyer.FreshWaterFulfilledConsumption = Math.Min(buyer.FreshWaterConsumption, buyer.FreshWaterFulfilledConsumption + units);
                    break;
                case MultiplayerContractResource.Sewage:
                    seller.SewageCapacity = Math.Max(0, seller.SewageCapacity - units);
                    buyer.SewageFulfilledConsumption = Math.Min(buyer.SewageConsumption, buyer.SewageFulfilledConsumption + units);
                    break;
            }
        }

        public static bool TryHandleContractFailure(
            List<MultiplayerContract> contracts,
            Dictionary<string, int> contractFailureCounts,
            string contractId,
            string reason,
            int index,
            int cancelFailureThreshold,
            Action<string> addDebugLog,
            out bool removedIndex)
        {
            removedIndex = false;
            var failures = 0;
            contractFailureCounts.TryGetValue(contractId, out failures);
            failures++;
            contractFailureCounts[contractId] = failures;
            if (failures < cancelFailureThreshold)
            {
                addDebugLog?.Invoke($"Contract {contractId} transient failure {failures}/{cancelFailureThreshold}: {reason}.");
                return false;
            }

            addDebugLog?.Invoke($"Contract {contractId} cancelled ({reason}) after {failures} failed checks.");
            contracts.RemoveAt(index);
            contractFailureCounts.Remove(contractId);
            removedIndex = true;
            return true;
        }

        public static int GetLocalTargetCapacityDelta(
            IReadOnlyList<MultiplayerContract> contracts,
            string localPlayerName,
            MultiplayerContractResource resource,
            Func<string, string> normalizePlayerName)
        {
            if (string.IsNullOrWhiteSpace(localPlayerName))
                return 0;

            localPlayerName = normalizePlayerName(localPlayerName);
            var delta = 0;
            for (var i = 0; i < contracts.Count; i++)
            {
                var contract = contracts[i];
                if (contract.Resource != resource || contract.UnitsPerTick <= 0)
                    continue;

                var units = Math.Max(0, contract.EffectiveUnitsPerTick);
                if (units <= 0)
                    continue;

                if (string.Equals(contract.SellerPlayer, localPlayerName, StringComparison.OrdinalIgnoreCase))
                {
                    delta -= units;
                }
                else if (string.Equals(contract.BuyerPlayer, localPlayerName, StringComparison.OrdinalIgnoreCase))
                {
                    delta += units;
                }
            }

            return delta;
        }

        private static bool IsExpired(MultiplayerContractProposal proposal, DateTime nowUtc, int timeoutSeconds)
        {
            return proposal.CreatedUtc.AddSeconds(timeoutSeconds) <= nowUtc;
        }
    }
}
