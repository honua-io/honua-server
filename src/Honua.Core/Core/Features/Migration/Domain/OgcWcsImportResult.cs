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

public sealed record OgcWcsImportResult
{
    public required MigrationManifestArtifact Manifest { get; init; }

    public OgcCoverageImportRecord[] Records { get; init; } = [];

    public required string RequestedOutputFormat { get; init; }

    public required string ResolvedVersion { get; init; }

    public bool ApplyMode { get; init; }

    public bool DryRun { get; init; }

    public MigrationCoverageStyleDiagnostic[] StyleDiagnostics { get; init; } = [];
}
