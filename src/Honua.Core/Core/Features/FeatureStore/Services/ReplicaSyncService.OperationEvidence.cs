// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

public sealed partial class ReplicaSyncService
{
    private readonly record struct ConflictEvidenceContext(
        int StorageLayerId,
        long PreBatchGeneration,
        IReadOnlyDictionary<long, long> StorageObjectIdsByPublicId);

    private readonly record struct ConflictEvidenceCandidate(ReplicaSyncConflict Conflict, int Slot);

    private static IReadOnlyDictionary<string, long> ResolveConflictBaseGenerations(
        ConflictEvidenceContext context,
        IEnumerable<ConflictEvidenceCandidate> candidates,
        ReplicaLayerApplyResult result)
    {
        var generations = new Dictionary<string, long>(StringComparer.Ordinal);
        if (result.OperationEvidence.IsDefaultOrEmpty || result.CommittedEditIndexes.IsDefaultOrEmpty)
        {
            return generations;
        }

        var committed = result.CommittedEditIndexes.ToHashSet();
        var unknown = result.IndeterminateEditIndexes.IsDefaultOrEmpty
            ? [] : result.IndeterminateEditIndexes.ToHashSet();
        foreach (var candidate in candidates)
        {
            if (!committed.Contains(candidate.Slot) || unknown.Contains(candidate.Slot) ||
                candidate.Slot < 0 || candidate.Slot >= result.OperationEvidence.Length ||
                candidate.Conflict.ConflictId is not { Length: > 0 } conflictId)
            {
                continue;
            }

            var evidence = result.OperationEvidence[candidate.Slot];
            if (!evidence.IsDefaultOrEmpty && evidence.All(item => MatchesConflictEvidence(context, candidate, item)))
            {
                generations[conflictId] = evidence.Max(item => item.Generation);
            }
        }

        return generations;
    }

    private static bool MatchesConflictEvidence(
        ConflictEvidenceContext context,
        ConflictEvidenceCandidate candidate,
        FeatureOperationEvidence evidence)
    {
        var expectedOperation = candidate.Conflict.ClientKind switch
        {
            FeatureEditOperationKind.Create => FeatureChangeOperation.Insert,
            FeatureEditOperationKind.Update => FeatureChangeOperation.Update,
            FeatureEditOperationKind.Delete => FeatureChangeOperation.Delete,
            _ => (FeatureChangeOperation)0
        };
        var identityMatches = evidence.PublicObjectId is { } publicId
            ? publicId == candidate.Conflict.ObjectId
            : evidence.ObjectId == context.StorageObjectIdsByPublicId.GetValueOrDefault(
                candidate.Conflict.ObjectId, candidate.Conflict.ObjectId);
        return evidence.Generation > context.PreBatchGeneration &&
            evidence.LayerId == context.StorageLayerId && identityMatches &&
            evidence.Operation == expectedOperation;
    }
}
