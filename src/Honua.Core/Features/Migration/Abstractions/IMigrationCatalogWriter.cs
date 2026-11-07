// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Abstractions;

public interface IMigrationCatalogWriter
{
    Task<MigrationCatalogWriteOutcome> EnsureCatalogServiceAsync(
        string connectionString,
        MigrationCatalogServiceRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationCatalogWriteOutcome> EnsureDataSourceAsync(
        string connectionString,
        MigrationDataSourceRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationFeatureCopyOutcome> CopyFeatureDataAsync(
        string connectionString,
        MigrationFeatureCopyRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationCatalogWriteOutcome> EnsureStyleAsync(
        string connectionString,
        MigrationStyleRequest request,
        CancellationToken cancellationToken = default);

    // Every relationship in one call targets the same database, and a null graph store writes only the v1 row.
    Task<MigrationRelationshipApplyOutcome[]> EnsureRelationshipsAsync(
        string connectionString,
        IMetadataV2GraphStore? graphStore,
        MigrationRelationshipApplyRequest[] requests,
        CancellationToken cancellationToken = default);
}

public sealed record MigrationCatalogServiceRequest
{
    public required string ServiceName { get; init; }

    public required string Description { get; init; }

    public int Srid { get; init; } = 4326;

    public string EntryKind { get; init; } = "workspace";
}

public enum MigrationCatalogWriteOutcome
{
    Created,

    AlreadyExists
}

public sealed record MigrationDataSourceRequest
{
    public required string SourceKind { get; init; }

    public required string SourceId { get; init; }

    public required string DataSourceType { get; init; }

    public string? WorkspaceName { get; init; }

    public string? DisplayName { get; init; }

    // The connection summary is a host and database, or a file-path stem, and must not include secrets.
    public string ConnectionSummary { get; init; } = string.Empty;
}

public sealed record MigrationFeatureCopyRequest
{
    public required string SourceSchema { get; init; }

    public required string SourceTable { get; init; }

    public string TargetSchema { get; init; } = "honua_data";

    public required string TargetTable { get; init; }
}

public sealed record MigrationFeatureCopyOutcome
{
    public required MigrationFeatureCopyStatus Status { get; init; }

    public long RowCount { get; init; }
}

public enum MigrationFeatureCopyStatus
{
    Copied,

    AlreadyApplied,

    SourceMissing
}

public sealed record MigrationStyleRequest
{
    public required string SourceKind { get; init; }

    public required string SourceId { get; init; }

    public required string TargetStyleId { get; init; }

    public required string StyleName { get; init; }

    public string? WorkspaceName { get; init; }

    public string SourceFormat { get; init; } = "sld";

    public string? SourceLanguageVersion { get; init; }

    public string? SourceBody { get; init; }

    public string? ConvertedBody { get; init; }

    public string? ConvertedFormat { get; init; }

    // Each diagnostic is a JSON object with severity warning or error, and a warning means visual parity was not claimed.
    public string DiagnosticsJson { get; init; } = "[]";

    public string ReviewDisposition { get; init; } = "applied";
}

public sealed record MigrationRelationshipApplyRequest
{
    public required string SourceRelationshipId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("esriRelationshipId")]
    public int? GeoServicesRelationshipId { get; init; }

    public string? Name { get; init; }

    public required int OriginLayerId { get; init; }

    public required int RelatedLayerId { get; init; }

    public required string OriginKeyField { get; init; }

    public required string DestinationKeyField { get; init; }

    public required string Role { get; init; }

    public required string Cardinality { get; init; }

    public bool Composite { get; init; }
}

public sealed record MigrationRelationshipApplyOutcome
{
    public required string SourceRelationshipId { get; init; }

    public required MigrationCatalogWriteOutcome Outcome { get; init; }

    public required string Message { get; init; }

    public required string TargetRelationshipRef { get; init; }

    // Deferred marks a blocking incomplete migration, because an idempotent re-apply also reports MigrationCatalogWriteOutcome.AlreadyExists.
    public bool Deferred { get; init; }
}
