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

public sealed record OgcCoverageImportResult
{
    public required MigrationManifestArtifact Manifest { get; init; }

    public OgcCoverageImportRecord[] Records { get; init; } = [];

    public bool ApplyMode { get; init; }

    public bool DryRun { get; init; }

    public MigrationCoverageStyleDiagnostic[] StyleDiagnostics { get; init; } = [];
}

public sealed record OgcCoverageImportRecord
{
    public required string SourceCoverageId { get; init; }

    public required string TargetCoverageName { get; init; }

    public required string OutputFormat { get; init; }

    public required string Classification { get; init; }

    public required string Action { get; init; }

    public string[] DeclaredCrs { get; init; } = [];

    public string[] DeclaredBands { get; init; } = [];

    public long ByteCount { get; init; }

    public long? RasterId { get; init; }

    public int? TargetLayerId { get; init; }

    public string[] Warnings { get; init; } = [];

    public string? ErrorMessage { get; init; }
}
