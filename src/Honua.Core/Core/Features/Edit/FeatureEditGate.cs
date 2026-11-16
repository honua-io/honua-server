// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

// Without EffectiveAttributes, each row's own attributes are taken as the full row to validate.
public sealed record FeatureEditCheck(
    MetadataV2Object Resource,
    FeatureEditBatch Batch,
    Func<FeatureEditRow, IReadOnlyDictionary<string, object?>>? EffectiveAttributes = null);

// The pre-write checks every edit protocol shares: contingent values on the effective row, then the plugin before-hooks.
public static class FeatureEditGate
{
    private const int RejectedErrorCode = 1000;

    public static async Task<IReadOnlyList<FeatureEditHookRejection>> CheckAsync(
        IFeatureEditHooks hooks,
        FeatureEditHookScope scope,
        FeatureEditCheck check,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(check);
        var batch = check.Batch;
        var rejected = ValidateContingentValues(check.Resource, batch, check.EffectiveAttributes);
        if (!hooks.HasHooks || (rejected.Count > 0 && batch.RollbackOnFailure))
        {
            return rejected;
        }

        var remaining = WithoutRejected(batch, rejected);
        if (remaining.IsEmpty)
        {
            return rejected;
        }

        var hookRejections = await hooks.RunBeforeAsync(scope, remaining, cancellationToken).ConfigureAwait(false);
        return [.. rejected, .. ToBatchIndexes(batch, rejected, hookRejections)];
    }

    public static IReadOnlyList<FeatureEditHookRejection> ValidateContingentValues(
        MetadataV2Object resource,
        FeatureEditBatch batch,
        Func<FeatureEditRow, IReadOnlyDictionary<string, object?>>? effectiveAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.ContingentValueGroups.Count == 0)
        {
            return [];
        }

        var rejections = new List<FeatureEditHookRejection>();
        foreach (var row in FeatureEditRows.Enumerate(batch))
        {
            if (row.Kind == FeatureEditOperationKind.Delete || row.Feature is not { } feature)
            {
                continue;
            }

            var result = ContingentValueValidator.Validate(resource, effectiveAttributes?.Invoke(row) ?? feature.Attributes);
            if (!result.IsValid)
            {
                rejections.Add(new FeatureEditHookRejection(row.Kind, row.Index, result.Violations[0].Message));
            }
        }

        return rejections;
    }

    public static FeatureEditBatch WithoutRejected(FeatureEditBatch batch, IReadOnlyList<FeatureEditHookRejection> rejections)
    {
        ArgumentNullException.ThrowIfNull(rejections);
        if (rejections.Count == 0)
        {
            return batch;
        }

        var rejected = rejections.Select(static rejection => (rejection.Kind, rejection.Index)).ToHashSet();
        var rows = FeatureEditRows.Enumerate(batch);
        return batch with
        {
            Operations = batch.Operations.IsDefaultOrEmpty
                ? batch.Operations
                : [.. batch.Operations.Where((_, position) => !rejected.Contains((rows[position].Kind, rows[position].Index)))],
            Creates = Keep(batch.Creates, FeatureEditOperationKind.Create, rejected),
            Updates = Keep(batch.Updates, FeatureEditOperationKind.Update, rejected),
            Deletes = Keep(batch.Deletes, FeatureEditOperationKind.Delete, rejected),
        };
    }

    // Nothing is written: rejected rows carry their reason and every other row reports the rollback.
    public static FeatureEditResult RejectedResult(FeatureEditBatch batch, IReadOnlyList<FeatureEditHookRejection> rejections)
    {
        var messages = Messages(rejections);
        var results = NewResultBuilders();
        foreach (var row in FeatureEditRows.Enumerate(batch))
        {
            results[(int)row.Kind].Add(messages.TryGetValue((row.Kind, row.Index), out var message)
                ? EditOperationResult.Failure(message, RejectedErrorCode, row.ObjectId)
                : EditOperationResult.Success(row.ObjectId ?? 0));
        }

        return FeatureEditResult.Rollback(results[0].ToImmutable(), results[1].ToImmutable(), results[2].ToImmutable());
    }

    // Places the rejected rows back at their batch positions in the result of writing the remaining rows.
    public static FeatureEditResult WithRejectedRows(
        FeatureEditResult written,
        FeatureEditBatch batch,
        IReadOnlyList<FeatureEditHookRejection> rejections)
    {
        if (rejections.Count == 0)
        {
            return written;
        }

        var messages = Messages(rejections);
        var sources = new[] { written.CreateResults, written.UpdateResults, written.DeleteResults };
        var next = new int[3];
        var results = NewResultBuilders();
        foreach (var row in FeatureEditRows.Enumerate(batch))
        {
            var kind = (int)row.Kind;
            if (messages.TryGetValue((row.Kind, row.Index), out var message))
            {
                results[kind].Add(EditOperationResult.Failure(message, RejectedErrorCode, row.ObjectId));
            }
            else if (!sources[kind].IsDefault && next[kind] < sources[kind].Length)
            {
                results[kind].Add(sources[kind][next[kind]++]);
            }
        }

        return written with
        {
            CreateResults = results[0].ToImmutable(),
            UpdateResults = results[1].ToImmutable(),
            DeleteResults = results[2].ToImmutable(),
        };
    }

    private static IEnumerable<FeatureEditHookRejection> ToBatchIndexes(
        FeatureEditBatch batch,
        IReadOnlyList<FeatureEditHookRejection> dropped,
        IReadOnlyList<FeatureEditHookRejection> found)
    {
        if (dropped.Count == 0)
        {
            return found;
        }

        var droppedKeys = dropped.Select(static rejection => (rejection.Kind, rejection.Index)).ToHashSet();
        var kept = FeatureEditRows.Enumerate(batch)
            .Where(row => !droppedKeys.Contains((row.Kind, row.Index)))
            .GroupBy(static row => row.Kind)
            .ToDictionary(static group => group.Key, static group => group.Select(static row => row.Index).ToArray());
        return found
            .Where(rejection => kept.TryGetValue(rejection.Kind, out var indexes) && rejection.Index < indexes.Length)
            .Select(rejection => rejection with { Index = kept[rejection.Kind][rejection.Index] });
    }

    private static ImmutableArray<T> Keep<T>(
        ImmutableArray<T> items,
        FeatureEditOperationKind kind,
        HashSet<(FeatureEditOperationKind Kind, int Index)> rejected)
        => items.IsDefaultOrEmpty ? items : [.. items.Where((_, index) => !rejected.Contains((kind, index)))];

    private static Dictionary<(FeatureEditOperationKind Kind, int Index), string> Messages(
        IReadOnlyList<FeatureEditHookRejection> rejections)
    {
        var messages = new Dictionary<(FeatureEditOperationKind Kind, int Index), string>();
        foreach (var rejection in rejections)
        {
            messages.TryAdd((rejection.Kind, rejection.Index), rejection.Message);
        }

        return messages;
    }

    private static ImmutableArray<EditOperationResult>.Builder[] NewResultBuilders()
        =>
        [
            ImmutableArray.CreateBuilder<EditOperationResult>(),
            ImmutableArray.CreateBuilder<EditOperationResult>(),
            ImmutableArray.CreateBuilder<EditOperationResult>(),
        ];
}
