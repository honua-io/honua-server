// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Server.Features.Operations;

internal sealed record StudioDraftRestoreRevisionPayload
{
    public required Guid DraftId { get; init; }
    public required RestoreStudioDraftRevisionCommand Command { get; init; }
    public string? TenantId { get; init; }
    public string? SchemaName { get; init; }
}

internal sealed class StudioDraftRestoreRevisionExecutor(IStudioPackageLifecycleService lifecycle, TimeProvider clock)
    : StudioDraftMutationExecutor<StudioDraftRestoreRevisionPayload, StudioPackageDraft>(lifecycle, clock)
{
    public override string OperationId => StudioDraftOperations.RestoreRevision;
    protected override JsonTypeInfo<StudioDraftRestoreRevisionPayload> PayloadType => StudioDraftOperationJsonContext.Default.StudioDraftRestoreRevisionPayload;
    protected override JsonTypeInfo<StudioPackageDraft> ResultType => StudioDraftOperationJsonContext.Default.StudioPackageDraft;

    protected override async Task<StudioPackageDraft> ActuateAsync(
        StudioDraftRestoreRevisionPayload payload,
        CancellationToken cancellationToken) => await Lifecycle
            .RestoreDraftRevisionAsync(payload.DraftId, payload.Command, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Studio draft '{payload.DraftId:D}' was not found.");

    protected override IReadOnlyDictionary<string, string> ResourceIds(StudioPackageDraft result) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["draftId"] = result.DraftId.ToString("D"),
            ["generation"] = result.Generation.ToString(CultureInfo.InvariantCulture),
        };
}
