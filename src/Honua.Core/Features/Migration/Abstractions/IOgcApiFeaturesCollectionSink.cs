// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Abstractions;

// Re-runs must converge on the same rows, so writes are idempotent for a given source and target.
public interface IOgcApiFeaturesCollectionSink
{
    Task EnsureTargetAsync(OgcApiFeaturesSinkTarget target, CancellationToken cancellationToken);

    // Re-runs upsert on the source identifier so they do not duplicate rows.
    Task<int> WriteFeaturesAsync(
        OgcApiFeaturesSinkTarget target,
        IReadOnlyList<OgcApiFeaturesSinkFeature> features,
        CancellationToken cancellationToken);

    Task<string?> GetLastScopeSignatureAsync(OgcApiFeaturesSinkTarget target, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    Task RecordScopeSignatureAsync(
        OgcApiFeaturesSinkTarget target,
        string scopeSignature,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    Task<IReadOnlyList<OgcApiFeaturesSinkColumn>> GetTargetColumnsAsync(
        OgcApiFeaturesSinkTarget target,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<OgcApiFeaturesSinkColumn>>([]);
}

public sealed record OgcApiFeaturesSinkColumn
{
    public required string Name { get; init; }

    public required string DataType { get; init; }

    public bool IsNullable { get; init; } = true;
}

public sealed record OgcApiFeaturesSinkTarget
{
    // The schema name is untrusted and must be quoted before DDL or DML.
    public required string Schema { get; init; }

    // The table name is untrusted and must be quoted before DDL or DML.
    public required string Table { get; init; }

    public required string CollectionId { get; init; }

    public int Srid { get; init; } = 4326;
}

public sealed record OgcApiFeaturesSinkFeature
{
    public required string SourceFeatureId { get; init; }

    public string? GeoJsonGeometry { get; init; }

    public required string PropertiesJson { get; init; }
}
