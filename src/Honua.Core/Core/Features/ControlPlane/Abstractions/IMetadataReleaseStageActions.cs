// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public sealed record MetadataReleasePreflightResult
{
    public required bool CanProceed { get; init; }

    public required MetadataRollbackReadinessClassification RollbackClassification { get; init; }

    public required string Reason { get; init; }

    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();
}

public enum MetadataReleaseSmokePhase
{
    Candidate,

    Activated,

    Recovered
}

public sealed record MetadataReleaseSmokeRequest
{
    public required MetadataReleaseSmokePhase Phase { get; init; }

    public required long Revision { get; init; }

    public long? BaselineRevision { get; init; }

    public required bool ExpectNewField { get; init; }
}

public sealed record MetadataReleaseSmokeResult
{
    public required bool Passed { get; init; }

    public long RowCount { get; init; }

    public bool NewFieldPresent { get; init; }

    public required string Message { get; init; }
}

public sealed record MetadataReleaseScriptResult
{
    public required MetadataV2Graph Graph { get; init; }

    public IReadOnlyList<MetadataReleaseScriptOperation> AppliedOperations { get; init; } = Array.Empty<MetadataReleaseScriptOperation>();
}

public sealed class MetadataReleasePreparationException : Exception
{
    public MetadataReleasePreparationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public enum MetadataReleaseActivationOutcome
{
    Activated,

    AlreadyActive,

    Conflict
}

public sealed record MetadataReleaseActivationResult
{
    public required MetadataReleaseActivationOutcome Outcome { get; init; }

    public required long CurrentRevision { get; init; }

    public required string CurrentEtag { get; init; }
}

public interface IMetadataReleasePreflightGate
{
    Task<MetadataReleasePreflightResult> EvaluateAsync(
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken = default);
}

public interface IMetadataReleaseScriptExecutor
{
    MetadataReleaseScriptResult PrepareForward(
        MetadataReleaseExecutionPlan plan,
        MetadataV2Graph baseline);

    MetadataReleaseScriptResult PrepareInverse(
        IReadOnlyList<MetadataReleaseScriptOperation> ownedOperations,
        MetadataV2Graph current);
}

public interface IMetadataReleaseDataJobDispatcher
{
    Task<bool> DispatchAndAwaitAsync(
        MetadataReleaseExecutionPlan plan,
        string operationId,
        CancellationToken cancellationToken = default);
}

public interface IMetadataReleaseActivator
{
    Task<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default);

    Task<MetadataV2GraphSnapshot?> GetRevisionAsync(long revision, CancellationToken cancellationToken = default);

    Task<MetadataV2GraphSnapshot> StageAsync(MetadataV2Graph candidate, CancellationToken cancellationToken = default);

    Task DiscardAsync(long revision, CancellationToken cancellationToken = default);

    Task<MetadataReleaseActivationResult> ActivateAsync(
        long revision,
        string expectedCurrentEtag,
        CancellationToken cancellationToken = default);

    Task<MetadataV2GraphSnapshot> CommitRevertAsync(
        MetadataV2Graph reverted,
        string expectedCurrentEtag,
        CancellationToken cancellationToken = default);
}

public interface IMetadataReleaseSmokeChecker
{
    Task<MetadataReleaseSmokeResult> RunAsync(
        MetadataReleaseExecutionPlan plan,
        MetadataReleaseSmokeRequest request,
        CancellationToken cancellationToken = default);
}
