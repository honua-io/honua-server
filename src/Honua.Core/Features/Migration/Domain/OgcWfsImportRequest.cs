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
namespace Honua.Core.Features.Migration.Domain;

public sealed record OgcWfsImportRequest
{
    public string? JobId { get; init; }

    public required string ServiceUrl { get; init; }

    public string? Version { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string[]? FeatureTypeNames { get; init; }

    public string? TargetSchema { get; init; }

    public bool OverwriteExisting { get; init; }

    public bool DryRun { get; init; }

    public bool ApplyMode { get; init; }

    public int? TargetSrid { get; init; }

    public const int MaxPageSize = 10_000;

    public int PageSize { get; init; } = 1000;

    public static int ClampPageSize(int pageSize)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "PageSize must be greater than zero.");
        }

        return Math.Min(pageSize, MaxPageSize);
    }

    public static void EnsurePageWithinRequest(int featureCount, int pageSize)
    {
        if (featureCount > pageSize)
        {
            throw new FormatException(
                $"WFS page returned {featureCount} features, which exceeds the requested page size of {pageSize}.");
        }
    }

    public int RequestTimeoutSeconds { get; init; } = 120;

    public bool AllowUnsafeLocalUrls { get; init; }
}

public sealed record OgcWfsImportResult
{
    public required bool Success { get; init; }

    public bool WasDryRun { get; init; }

    public required string SourceServiceUrl { get; init; }

    public string? SourceVersion { get; init; }

    public int FeatureTypesPlanned { get; init; }

    public int FeatureTypesImported { get; init; }

    public int FeatureTypesSkipped { get; init; }

    public int FeaturesCopied { get; init; }

    public int FeaturesFailed { get; init; }

    public IReadOnlyList<OgcWfsImportedFeatureType> FeatureTypes { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? ErrorMessage { get; init; }

    public string? ErrorCode { get; init; }

    public required MigrationManifestArtifact Manifest { get; init; }

    public required MigrationParityEvidenceArtifact ParityEvidence { get; init; }

    public TimeSpan Duration { get; init; }
}

public sealed record OgcWfsImportedFeatureType
{
    public required string SourceName { get; init; }

    public string? TargetSchema { get; init; }

    public string? TargetTable { get; init; }

    public string? GeometryType { get; init; }

    public int? Srid { get; init; }

    public int FeaturesCopied { get; init; }

    public int FeaturesFailed { get; init; }

    public required string Classification { get; init; }

    public string[] Warnings { get; init; } = [];
}
