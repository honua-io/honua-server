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

public static class MigrationEvidenceStates
{
    public const string Pass = "pass";

    public const string Fail = "fail";

    public const string Unknown = "unknown";

    public const string NotApplicable = "not-applicable";
}

public sealed record MigrationParityEvidenceArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.parity-evidence-pack";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string SourceKind { get; init; }

    public required MigrationSourceIdentity Source { get; init; }

    public required string OverallState { get; init; }

    public required string Summary { get; init; }

    public bool ManifestAvailable { get; init; }

    public MigrationParityEvidenceSection[] Sections { get; init; } = [];

    public required MigrationCutoverReadinessSummary CutoverReadiness { get; init; }

    public MigrationPerformanceCostEvidence? PerformanceCost { get; init; }
}

public sealed record MigrationParityEvidenceSection
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string State { get; init; }

    public MigrationParityEvidenceItem[] Items { get; init; } = [];
}

public sealed record MigrationParityEvidenceItem
{
    public required string Id { get; init; }

    public required string State { get; init; }

    public required string Summary { get; init; }

    public string[] Evidence { get; init; } = [];

    public string[] Remediation { get; init; } = [];

    public string[] RelatedIds { get; init; } = [];
}

public sealed record MigrationCutoverReadinessSummary
{
    public required string State { get; init; }

    public MigrationCutoverReadinessItem[] Items { get; init; } = [];
}

public sealed record MigrationCutoverReadinessItem
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string State { get; init; }

    public string[] Evidence { get; init; } = [];

    public string[] Remediation { get; init; } = [];

    public string? Owner { get; init; }
}

public sealed record MigrationReadinessAttestation
{
    public MigrationReadinessAttestationItem[] Items { get; init; } = [];
}

public sealed record MigrationReadinessAttestationItem
{
    public required string Id { get; init; }

    public required string State { get; init; }

    public string[] Evidence { get; init; } = [];

    public string? Owner { get; init; }
}

public sealed record MigrationPerformanceCostEvidence
{
    public string ArtifactKind { get; init; } = "honua.migration.performance-cost-evidence";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string State { get; init; }

    public required string Summary { get; init; }

    public required string MeasurementScope { get; init; }

    public required MigrationPerformanceCostTotals Totals { get; init; }

    public MigrationPerformanceCostOperation[] Operations { get; init; } = [];

    // Query strings, fragments, and URL credentials are removed before the reference is stored.
    public string[] EvidenceReferences { get; init; } = [];
}

public sealed record MigrationPerformanceCostTotals
{
    public long? DurationMilliseconds { get; init; }

    public long? ResourceCount { get; init; }

    public long? FeatureCount { get; init; }

    public long? BytesRead { get; init; }

    public long? BytesWritten { get; init; }

    public int? RetryCount { get; init; }

    public int? SourceRequestCount { get; init; }

    public int? ManualReviewCount { get; init; }

    public long? ArtifactSizeBytes { get; init; }
}

public sealed record MigrationPerformanceCostOperation
{
    public required string Id { get; init; }

    public required string Stage { get; init; }

    public required string State { get; init; }

    public long? DurationMilliseconds { get; init; }

    public long? ResourceCount { get; init; }

    public long? FeatureCount { get; init; }

    public long? BytesRead { get; init; }

    public long? BytesWritten { get; init; }

    public int? RetryCount { get; init; }

    public int? SourceRequestCount { get; init; }

    public int? ManualReviewCount { get; init; }

    public long? ArtifactSizeBytes { get; init; }

    // Query strings, fragments, and URL credentials are removed before the reference is stored.
    public string[] EvidenceReferences { get; init; } = [];
}
