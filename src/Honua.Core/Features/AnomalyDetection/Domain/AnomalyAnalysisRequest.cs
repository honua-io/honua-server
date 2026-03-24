// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AnomalyDetection.Domain;

/// <summary>
/// Request to perform anomaly analysis on a layer.
/// </summary>
/// <param name="TableName">The PostgreSQL table name to analyze.</param>
/// <param name="LayerName">The display layer name for the report.</param>
/// <param name="GeometryColumn">The geometry column name (null for non-spatial layers).</param>
/// <param name="DeclaredSrid">The declared SRID for the layer.</param>
/// <param name="AttributeColumns">Attribute column names and types to analyze.</param>
/// <param name="ObjectIdColumn">The primary key column name.</param>
/// <param name="MaxSampleFeatures">Maximum sample feature IDs to include in anomaly reports.</param>
/// <param name="ScanLimit">Maximum features to scan (0 = no limit).</param>
public sealed record AnomalyAnalysisRequest(
    string TableName,
    string LayerName,
    string? GeometryColumn,
    int DeclaredSrid,
    IReadOnlyList<AnomalyFieldDescriptor> AttributeColumns,
    string ObjectIdColumn = "objectid",
    int MaxSampleFeatures = 5,
    int ScanLimit = 10000);

/// <summary>
/// Describes a field for anomaly analysis.
/// </summary>
/// <param name="Name">The column name.</param>
/// <param name="DataType">The data type category for analysis heuristics.</param>
public sealed record AnomalyFieldDescriptor(
    string Name,
    AnomalyFieldDataType DataType);

/// <summary>
/// Simplified data type for anomaly analysis heuristics.
/// </summary>
public enum AnomalyFieldDataType
{
    /// <summary>Text or string values.</summary>
    Text,

    /// <summary>Numeric values (integer or floating point).</summary>
    Numeric,

    /// <summary>Date or timestamp values.</summary>
    Temporal,

    /// <summary>Boolean values.</summary>
    Boolean,

    /// <summary>Other types not specially analyzed.</summary>
    Other
}
