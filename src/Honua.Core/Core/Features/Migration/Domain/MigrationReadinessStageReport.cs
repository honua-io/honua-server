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

public sealed record MigrationReadinessStageReport
{
    public string ArtifactKind { get; init; } = "honua.migration.readiness-stage-report";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required string ScanRunId { get; init; }

    public required string ApplyRunId { get; init; }

    public required string ParityRunId { get; init; }

    public required MigrationReadinessStageSummary Summary { get; init; }

    public MigrationReadinessStageEntry[] Sources { get; init; } = [];
}

public sealed record MigrationReadinessStageEntry
{
    public required string FixtureId { get; init; }

    public required string SourceKind { get; init; }

    public required string Status { get; init; }

    public required MigrationReadinessAttestationArtifact Attestation { get; init; }
}

public sealed record MigrationReadinessStageSummary
{
    public int SourceCount { get; init; }

    public int ReadySourceCount { get; init; }

    public int ConditionalSourceCount { get; init; }

    public int NotReadySourceCount { get; init; }

    public int ReasonCount { get; init; }

    public int EvidenceCitationCount { get; init; }
}
