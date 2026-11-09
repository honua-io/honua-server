// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

// Data reconciliation and capability parity stay separate and are not averaged, because an unsupported construct can still import cleanly.
public sealed record MigrationReconciliationScorecard
{
    public string ArtifactKind { get; init; } = "honua.migration.reconciliation-scorecard";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required string SourceKind { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    // The gate uses data reconciliation only; an unsupported construct that was skipped does not fail the run by itself.
    public required string Verdict { get; init; }

    public required MigrationScorecardDataReconciliation DataReconciliation { get; init; }

    public required MigrationScorecardCapabilityParity CapabilityParity { get; init; }

    public string? Fingerprint { get; init; }
}

public sealed record MigrationScorecardDataReconciliation
{
    public required string Classification { get; init; }

    public int LayerCount { get; init; }

    public int PassCount { get; init; }

    public int WarnCount { get; init; }

    public int FailCount { get; init; }

    public int SkippedCount { get; init; }

    public int CatalogFindingCount { get; init; }

    public MigrationScorecardLayer[] Layers { get; init; } = [];

    public string[] Reasons { get; init; } = [];
}

public sealed record MigrationScorecardLayer
{
    public required string SourceLayerId { get; init; }

    public string? SourceLayerName { get; init; }

    public int? TargetHonuaLayerId { get; init; }

    public required string Classification { get; init; }
}

public sealed record MigrationScorecardCapabilityParity
{
    public int ConstructCount { get; init; }

    public int AutomatedCount { get; init; }

    public int AssistedCount { get; init; }

    public int ManualReviewCount { get; init; }

    public int UnsupportedCount { get; init; }

    // Capability parity is advisory and never gates the run.
    public double ParityRatio { get; init; }
}
