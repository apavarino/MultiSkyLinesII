using System;
using System.Collections.Generic;

namespace MultiSkyLineII
{
    internal static class MultiplayerSettlementProcessor
    {
        internal sealed class Context
        {
            public List<MultiplayerContract> Contracts;
            public List<MultiplayerContractProposal> PendingProposals;
            public Dictionary<string, MultiplayerResourceState> RemoteStates;
            public Dictionary<string, int> ContractEffectiveUnits;
            public Dictionary<string, int> ContractFailureCounts;
            public DateTime NextSettlementUtc;
            public int ContractCancelFailureThreshold;
            public Func<MultiplayerResourceState> GetLocalState;
            public Func<string, string> NormalizePlayerName;
            public Func<MultiplayerResourceState, MultiplayerContractResource, bool> CanUseTransferInfrastructure;
            public Func<MultiplayerContractResource, MultiplayerResourceState, int> GetSellerAvailable;
            public Func<string, MultiplayerContractResource, int> GetCommittedOutgoingUnits;
            public Action<string> AddDebugLog;
            public Action<int> QueuePendingLocalMoneyDelta;
            public Action<string, string, int> RecordSettlementEvent;
            public Action<DateTime> CleanupExpiredProposals;
        }

        public static void ApplyIfDue(DateTime nowUtc, Context context)
        {
            if (context == null)
                return;
            if (nowUtc < context.NextSettlementUtc)
                return;

            context.NextSettlementUtc = nowUtc.AddSeconds(2);
            context.CleanupExpiredProposals?.Invoke(nowUtc);

            if (context.Contracts.Count == 0)
            {
                context.ContractEffectiveUnits.Clear();
                context.ContractFailureCounts.Clear();
                return;
            }

            var effectiveStates = new Dictionary<string, MultiplayerResourceState>(StringComparer.OrdinalIgnoreCase);
            var local = context.GetLocalState();
            effectiveStates[local.Name] = local;
            var localPlayerName = context.NormalizePlayerName(local.Name);
            foreach (var kvp in context.RemoteStates)
            {
                effectiveStates[kvp.Key] = kvp.Value;
            }

            context.ContractEffectiveUnits.Clear();

            for (var i = 0; i < context.Contracts.Count; i++)
            {
                var contract = context.Contracts[i];
                context.ContractEffectiveUnits[contract.Id] = 0;
                contract.EffectiveUnitsPerTick = 0;
                context.Contracts[i] = contract;
                if (!effectiveStates.TryGetValue(contract.SellerPlayer, out var seller) ||
                    !effectiveStates.TryGetValue(contract.BuyerPlayer, out var buyer))
                    continue;

                if (!context.CanUseTransferInfrastructure(seller, contract.Resource) ||
                    !context.CanUseTransferInfrastructure(buyer, contract.Resource))
                {
                    if (!MultiplayerContractRules.TryHandleContractFailure(
                            context.Contracts,
                            context.ContractFailureCounts,
                            contract.Id,
                            "transfer infrastructure unavailable",
                            i,
                            context.ContractCancelFailureThreshold,
                            context.AddDebugLog,
                            out var removedIndex))
                    {
                        continue;
                    }

                    if (removedIndex)
                        i--;
                    continue;
                }

                var available = context.GetSellerAvailable(contract.Resource, seller);
                var committedOutgoing = context.GetCommittedOutgoingUnits(contract.SellerPlayer, contract.Resource);
                var baselineAvailable = available + committedOutgoing;
                if (baselineAvailable < contract.UnitsPerTick)
                {
                    if (!MultiplayerContractRules.TryHandleContractFailure(
                            context.Contracts,
                            context.ContractFailureCounts,
                            contract.Id,
                            "seller cannot deliver full contracted amount",
                            i,
                            context.ContractCancelFailureThreshold,
                            context.AddDebugLog,
                            out var removedIndex))
                    {
                        continue;
                    }

                    if (removedIndex)
                        i--;
                    continue;
                }

                context.ContractFailureCounts[contract.Id] = 0;

                if (contract.PricePerTick <= 0 || buyer.Money < contract.PricePerTick)
                {
                    context.ContractEffectiveUnits[contract.Id] = 0;
                    contract.EffectiveUnitsPerTick = 0;
                    context.Contracts[i] = contract;
                    continue;
                }

                var transferUnits = contract.UnitsPerTick;
                context.ContractEffectiveUnits[contract.Id] = transferUnits;
                contract.EffectiveUnitsPerTick = transferUnits;
                context.Contracts[i] = contract;
                if (transferUnits <= 0)
                    continue;

                var payment = contract.PricePerTick;
                seller.Money += payment;
                buyer.Money -= payment;
                context.AddDebugLog?.Invoke($"Settlement {contract.SellerPlayer}->{contract.BuyerPlayer} res={contract.Resource} units={transferUnits} payment={payment}");

                MultiplayerContractRules.ApplyResourceTransfer(contract.Resource, ref seller, ref buyer, transferUnits);
                if (string.Equals(contract.SellerPlayer, localPlayerName, StringComparison.OrdinalIgnoreCase))
                {
                    context.QueuePendingLocalMoneyDelta?.Invoke(payment);
                }

                if (string.Equals(contract.BuyerPlayer, localPlayerName, StringComparison.OrdinalIgnoreCase))
                {
                    context.QueuePendingLocalMoneyDelta?.Invoke(-payment);
                }

                context.RecordSettlementEvent?.Invoke(contract.SellerPlayer, contract.BuyerPlayer, payment);
                effectiveStates[contract.SellerPlayer] = seller;
                effectiveStates[contract.BuyerPlayer] = buyer;
            }
        }
    }
}
