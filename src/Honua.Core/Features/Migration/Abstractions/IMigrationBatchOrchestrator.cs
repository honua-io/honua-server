// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

public interface IMigrationBatchOrchestrator
{
    Task<MigrationBatchRunRecord> StartAsync(
        MigrationBatchStartRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationBatchRunRecord?> AdvanceAsync(
        string batchId,
        CancellationToken cancellationToken = default);
}

public sealed record MigrationBatchStartRequest
{
    public required string SourceKind { get; init; }

    public string SourceUrl { get; init; } = string.Empty;

    public string? SourceDisplayName { get; init; }

    public required IReadOnlyList<MigrationBatchLayerSpec> Layers { get; init; }

    public string? ManifestBody { get; init; }

    public bool ApplyRelationships { get; init; }

    // When set, the batch does not start if pre-apply accounting finds a blocking construct or cannot read the manifest.
    public bool RequireFullFidelity { get; init; }
}

public sealed record MigrationBatchLayerSpec
{
    // The resource id must match the manifest SourceResourceId when relationship apply is requested.
    public required string SourceResourceId { get; init; }

    public required string ServiceUrl { get; init; }

    public required int LayerId { get; init; }

    public required string TableName { get; init; }

    public string? TargetSchema { get; init; }

    public string? ServiceName { get; init; }

    public GeoservicesImportTarget? Target { get; init; }

    public IReadOnlyList<string> DependsOn { get; init; } = [];
}
