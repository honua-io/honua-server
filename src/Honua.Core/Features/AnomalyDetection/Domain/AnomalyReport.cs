// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AnomalyDetection.Domain;

/// <summary>
/// Complete anomaly analysis report for a layer, containing both geometry and attribute findings.
/// </summary>
public sealed class AnomalyReport
{
    /// <summary>
    /// Layer name that was analyzed.
    /// </summary>
    public required string LayerName { get; init; }

    /// <summary>
    /// Total features scanned during analysis.
    /// </summary>
    public required long FeaturesScanned { get; init; }

    /// <summary>
    /// Geometry anomalies found.
    /// </summary>
    public IReadOnlyList<GeometryAnomaly> GeometryAnomalies { get; init; } = [];

    /// <summary>
    /// Attribute anomalies found.
    /// </summary>
    public IReadOnlyList<AttributeAnomaly> AttributeAnomalies { get; init; } = [];

    /// <summary>
    /// Timestamp when the analysis was performed.
    /// </summary>
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether any anomalies were detected.
    /// </summary>
    public bool HasAnomalies => GeometryAnomalies.Count > 0 || AttributeAnomalies.Count > 0;

    /// <summary>
    /// Total anomaly count across all categories.
    /// </summary>
    public int TotalAnomalyCount => GeometryAnomalies.Count + AttributeAnomalies.Count;
}

/// <summary>
/// A geometry-level anomaly detected in a layer.
/// </summary>
public sealed class GeometryAnomaly
{
    /// <summary>
    /// The type of geometry anomaly.
    /// </summary>
    public required GeometryAnomalyType Type { get; init; }

    /// <summary>
    /// Human-readable reason explaining the anomaly.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Severity level.
    /// </summary>
    public required AnomalySeverity Severity { get; init; }

    /// <summary>
    /// Number of features affected by this anomaly.
    /// </summary>
    public required int AffectedCount { get; init; }

    /// <summary>
    /// Sample feature identifiers exhibiting the anomaly (capped for safety).
    /// </summary>
    public IReadOnlyList<long> SampleFeatureIds { get; init; } = [];
}

/// <summary>
/// An attribute-level anomaly detected in a layer.
/// </summary>
public sealed class AttributeAnomaly
{
    /// <summary>
    /// The type of attribute anomaly.
    /// </summary>
    public required AttributeAnomalyType Type { get; init; }

    /// <summary>
    /// The field name where the anomaly was detected.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Human-readable reason explaining the anomaly.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Severity level.
    /// </summary>
    public required AnomalySeverity Severity { get; init; }

    /// <summary>
    /// Number of features affected.
    /// </summary>
    public required int AffectedCount { get; init; }

    /// <summary>
    /// Sample feature identifiers exhibiting the anomaly (capped for safety).
    /// </summary>
    public IReadOnlyList<long> SampleFeatureIds { get; init; } = [];
}

/// <summary>
/// Types of geometry anomalies.
/// </summary>
public enum GeometryAnomalyType
{
    /// <summary>Geometry is topologically invalid (self-intersection, etc.).</summary>
    InvalidGeometry,

    /// <summary>Geometry is empty or null.</summary>
    EmptyGeometry,

    /// <summary>Polygon has a suspiciously small area relative to its perimeter.</summary>
    SuspiciousAreaPerimeterRatio,

    /// <summary>Geometry SRID does not match the layer's declared SRID.</summary>
    SridMismatch,

    /// <summary>Geometry has duplicate consecutive vertices.</summary>
    DuplicateVertices
}

/// <summary>
/// Types of attribute anomalies.
/// </summary>
public enum AttributeAnomalyType
{
    /// <summary>High percentage of null values in a field.</summary>
    NullCluster,

    /// <summary>Cardinality is unusually high for a string field (may be mistyped).</summary>
    HighCardinality,

    /// <summary>Numeric field contains statistical outliers (beyond 3 standard deviations).</summary>
    NumericOutlier
}

/// <summary>
/// Severity levels for anomalies.
/// </summary>
public enum AnomalySeverity
{
    /// <summary>Informational — may be expected.</summary>
    Info,

    /// <summary>Warning — likely indicates a data quality issue.</summary>
    Warning,

    /// <summary>Error — the data is invalid and may cause processing failures.</summary>
    Error
}
