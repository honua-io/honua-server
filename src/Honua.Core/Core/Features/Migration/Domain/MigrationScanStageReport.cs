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

public sealed record MigrationScanStageReport
{
    public string ArtifactKind { get; init; } = "honua.migration.scan-stage-report";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required MigrationScanStageSummary Summary { get; init; }

    public MigrationScanStageEntry[] Sources { get; init; } = [];
}

public sealed record MigrationScanStageEntry
{
    public required string FixtureId { get; init; }

    public required string SourceKind { get; init; }

    public required MigrationSourceInventoryArtifact Inventory { get; init; }
}

public sealed record MigrationScanStageSummary
{
    public int SourceCount { get; init; }

    public int ContainerCount { get; init; }

    public int ResourceCount { get; init; }

    public int StyleCount { get; init; }

    public int ExternalDependencyCount { get; init; }

    public int AutomatedCount { get; init; }

    public int AssistedCount { get; init; }

    public int ManualReviewCount { get; init; }

    public int UnsupportedCount { get; init; }
}
