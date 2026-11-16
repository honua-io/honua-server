// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Edit;

// StoredRows holds the rows as stored before the edit, by object id, where the protocol has read them;
// a delete hook sees the stored row instead of an empty feature.
public sealed record FeatureEditHookScope(string ServiceId, int LayerId)
{
    public string? LayerName { get; init; }

    public string? UserName { get; init; }

    public string? TraceId { get; init; }

    public IReadOnlyDictionary<long, Feature>? StoredRows { get; init; }
}

// A write and what it returned, as the after-hooks receive it.
public sealed record FeatureEditOutcome(FeatureEditBatch Batch, FeatureEditResult Result);

// Index counts rows of the same kind in batch order, which is how FeatureEditResult orders its per-kind results.
// A hook that reports a row the batch does not contain must throw: dropping it would let the row be written.
public sealed record FeatureEditHookRejection(FeatureEditOperationKind Kind, int Index, string Message);

// Edit protocols call these hooks around every write so a validator cannot be bypassed by choosing another protocol.
public interface IFeatureEditHooks
{
    bool HasHooks { get; }

    Task<IReadOnlyList<FeatureEditHookRejection>> RunBeforeAsync(
        FeatureEditHookScope scope,
        FeatureEditBatch batch,
        CancellationToken cancellationToken);

    // Sees only the rows the result reports as committed, and never throws: the edit has already committed.
    Task RunAfterAsync(FeatureEditHookScope scope, FeatureEditOutcome outcome, CancellationToken cancellationToken);
}

public sealed class NoOpFeatureEditHooks : IFeatureEditHooks
{
    public static NoOpFeatureEditHooks Instance { get; } = new();

    public bool HasHooks => false;

    public Task<IReadOnlyList<FeatureEditHookRejection>> RunBeforeAsync(
        FeatureEditHookScope scope,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<FeatureEditHookRejection>>([]);

    public Task RunAfterAsync(FeatureEditHookScope scope, FeatureEditOutcome outcome, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
