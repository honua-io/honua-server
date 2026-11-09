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

public static class GeoServicesMigrationRunStatuses
{
    public const string ManifestOnly = "manifest-only";

    public const string Pass = "pass";

    public const string Warn = "warn";

    public const string Fail = "fail";
}

public sealed record GeoServicesMigrationRunRecord
{
    public required string RunId { get; init; }

    public required string SourceUrl { get; init; }

    public string? SourceDisplayName { get; init; }

    public string? SourceVersion { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? Actor { get; init; }
}

public sealed record GeoServicesMigrationRunFilter
{
    public string? SourceUrl { get; init; }

    public string? Status { get; init; }

    public int Page { get; init; }

    public int PageSize { get; init; } = 25;
}

public sealed record GeoServicesMigrationRunListResult
{
    public GeoServicesMigrationRunSummary[] Items { get; init; } = [];

    public int TotalCount { get; init; }

    public int Page { get; init; }

    public int PageSize { get; init; }
}

public sealed record GeoServicesMigrationRunSummary
{
    public required string RunId { get; init; }

    public required string SourceUrl { get; init; }

    public string? SourceDisplayName { get; init; }

    public string? SourceVersion { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? Actor { get; init; }

    public required string Status { get; init; }

    public bool HasParity { get; init; }

    public int TargetResourceCount { get; init; }
}
