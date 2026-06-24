// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Scene.Conversion;

/// <summary>
/// An Esri I3S per-field statistics document (OGC 19-008
/// <c>statistics/f_{n}/0.json</c>): the aggregate summary an ArcGIS client reads
/// to drive identify, classification, and renderer ranges for one attribute
/// field (#1811).
/// </summary>
public sealed class I3sAttributeStatisticsDocument
{
    /// <summary>The statistics summary for the field.</summary>
    [JsonPropertyName("stats")]
    public I3sAttributeStatistics Stats { get; set; } = new();
}

/// <summary>
/// I3S attribute statistics summary (OGC 19-008 <c>stats</c>).
/// </summary>
public sealed class I3sAttributeStatistics
{
    /// <summary>Total number of non-null values for the field.</summary>
    [JsonPropertyName("totalValuesCount")]
    public long TotalValuesCount { get; set; }

    /// <summary>Minimum value (omitted for non-numeric fields).</summary>
    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Min { get; set; }

    /// <summary>Maximum value (omitted for non-numeric fields).</summary>
    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; set; }

    /// <summary>Count of distinct values (omitted when unknown).</summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Count { get; set; }
}

/// <summary>
/// Source-generated JSON context for serving I3S attribute statistics. AOT-safe.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(I3sAttributeStatisticsDocument))]
[JsonSerializable(typeof(I3sAttributeStatistics))]
public sealed partial class I3sAttributeStatisticsJsonContext : JsonSerializerContext
{
}
