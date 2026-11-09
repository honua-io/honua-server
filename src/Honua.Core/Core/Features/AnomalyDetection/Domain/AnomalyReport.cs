// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AnomalyDetection.Domain;

public sealed class AnomalyReport
{
    public required string LayerName { get; init; }

    public required long FeaturesScanned { get; init; }

    public IReadOnlyList<GeometryAnomaly> GeometryAnomalies { get; init; } = [];

    public IReadOnlyList<AttributeAnomaly> AttributeAnomalies { get; init; } = [];

    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool HasAnomalies => GeometryAnomalies.Count > 0 || AttributeAnomalies.Count > 0;

    public int TotalAnomalyCount => GeometryAnomalies.Count + AttributeAnomalies.Count;
}

public sealed class GeometryAnomaly
{
    public required GeometryAnomalyType Type { get; init; }

    public required string Reason { get; init; }

    public required AnomalySeverity Severity { get; init; }

    public required int AffectedCount { get; init; }

    public IReadOnlyList<long> SampleFeatureIds { get; init; } = [];
}

public sealed class AttributeAnomaly
{
    public required AttributeAnomalyType Type { get; init; }

    public required string FieldName { get; init; }

    public required string Reason { get; init; }

    public required AnomalySeverity Severity { get; init; }

    public required int AffectedCount { get; init; }

    public IReadOnlyList<long> SampleFeatureIds { get; init; } = [];
}

public enum GeometryAnomalyType
{
    InvalidGeometry,

    EmptyGeometry,

    SuspiciousAreaPerimeterRatio,

    SridMismatch,

    DuplicateVertices
}

public enum AttributeAnomalyType
{
    NullCluster,

    HighCardinality,

    NumericOutlier
}

public enum AnomalySeverity
{
    Info,

    Warning,

    Error
}
