// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

public static class MigrationReconciliationClassifications
{
    public const string Pass = "pass";

    public const string Warn = "warn";

    // A probe outside the warn band keeps the run from Completed.
    public const string Fail = "fail";

    public const string Skipped = "skipped";
}

public sealed record MigrationReconciliationArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.reconciliation";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required string SourceKind { get; init; }

    public required string Classification { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required MigrationReconciliationSummary Summary { get; init; }

    public MigrationReconciliationLayerReport[] Layers { get; init; } = [];

    public string[] Reasons { get; init; } = [];

    public required LayerReconciliationOptions Options { get; init; }

    // Counts can match while the schema is wrong, so this report covers schema, domains, identifiers, relationships, attachments, and subtypes.
    public MigrationCatalogReconciliationReport? CatalogReconciliation { get; init; }
}

public sealed record MigrationReconciliationSummary
{
    public int LayerCount { get; init; }

    public int PassCount { get; init; }

    public int WarnCount { get; init; }

    public int FailCount { get; init; }

    public int SkippedCount { get; init; }
}

public sealed record MigrationReconciliationLayerReport
{
    public required string SourceLayerId { get; init; }

    public string? SourceLayerName { get; init; }

    public int? TargetHonuaLayerId { get; init; }

    public required string Classification { get; init; }

    public required MigrationReconciliationCountProbe Count { get; init; }

    public required MigrationReconciliationGeometryProbe Geometry { get; init; }

    public required MigrationReconciliationContentProbe Content { get; init; }

    public required MigrationReconciliationExtentProbe Extent { get; init; }
}

public sealed record MigrationReconciliationCountProbe
{
    public long? SourceCount { get; init; }

    public long? TargetCount { get; init; }

    public long? Delta { get; init; }

    public double? DeltaRatio { get; init; }

    public string? FilterMirror { get; init; }

    public required string Classification { get; init; }

    public string? Reason { get; init; }
}

public sealed record MigrationReconciliationGeometryProbe
{
    public int Sampled { get; init; }

    public int Valid { get; init; }

    public double Ratio { get; init; }

    public int InheritedSourceDefects { get; init; }

    public int MigrationLosses { get; init; }

    public int SourceDefectRewrites { get; init; }

    public required string Classification { get; init; }

    public string? Reason { get; init; }
}

public sealed record MigrationReconciliationContentProbe
{
    public string[] SourceFieldNames { get; init; } = [];

    public string[] TargetFieldNames { get; init; } = [];

    public string[] MissingOnTarget { get; init; } = [];

    public string[] ExtraOnTarget { get; init; } = [];

    public required string Classification { get; init; }

    public string? Reason { get; init; }
}

public sealed record MigrationReconciliationExtentProbe
{
    public ExtentBox? Source { get; init; }

    public ExtentBox? QueriedSource { get; init; }

    public bool AdvertisedExtentStale { get; init; }

    public ExtentBox? Target { get; init; }

    public ExtentBox? ComparisonTarget { get; init; }

    public double? MaxDimensionDelta { get; init; }

    public required string Classification { get; init; }

    public string? Reason { get; init; }
}

// A flat extent, so reconciliation evidence does not take on BoundingBox's CRS and axis-order surface.
public readonly record struct ExtentBox
{
    public required double MinX { get; init; }

    public required double MinY { get; init; }

    public required double MaxX { get; init; }

    public required double MaxY { get; init; }

    public required int Srid { get; init; }
}
