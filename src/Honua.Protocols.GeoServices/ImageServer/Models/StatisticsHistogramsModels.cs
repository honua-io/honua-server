// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// Esri-conformant response for the Image Server <c>computeStatisticsHistograms</c> endpoint.
/// </summary>
public sealed class ComputeStatisticsHistogramsResponse
{
    /// <summary>
    /// Per-band statistics describing the analysed area (whole layer or AOI).
    /// </summary>
    [JsonPropertyName("statistics")]
    public BandStatistic[] Statistics { get; init; } = [];

    /// <summary>
    /// Per-band histograms describing the analysed area (whole layer or AOI).
    /// </summary>
    [JsonPropertyName("histograms")]
    public BandHistogram[] Histograms { get; init; } = [];
}

/// <summary>
/// Per-band statistics shaped exactly like the Esri spec response.
/// </summary>
public sealed class BandStatistic
{
    /// <summary>
    /// Minimum pixel value found in the band.
    /// </summary>
    [JsonPropertyName("min")]
    public double Min { get; init; }

    /// <summary>
    /// Maximum pixel value found in the band.
    /// </summary>
    [JsonPropertyName("max")]
    public double Max { get; init; }

    /// <summary>
    /// Mean pixel value of the band.
    /// </summary>
    [JsonPropertyName("mean")]
    public double Mean { get; init; }

    /// <summary>
    /// Standard deviation of the band.
    /// </summary>
    [JsonPropertyName("standardDeviation")]
    public double StandardDeviation { get; init; }

    /// <summary>
    /// Count of valid (non-NoData) pixels.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; init; }
}

/// <summary>
/// Per-band histogram shaped exactly like the Esri spec response.
/// </summary>
public sealed class BandHistogram
{
    /// <summary>
    /// Number of bins.
    /// </summary>
    [JsonPropertyName("size")]
    public int Size { get; init; }

    /// <summary>
    /// Lower bound of the value range.
    /// </summary>
    [JsonPropertyName("min")]
    public double Min { get; init; }

    /// <summary>
    /// Upper bound of the value range.
    /// </summary>
    [JsonPropertyName("max")]
    public double Max { get; init; }

    /// <summary>
    /// Counts per bin, ordered from <see cref="Min"/> to <see cref="Max"/>.
    /// </summary>
    [JsonPropertyName("counts")]
    public long[] Counts { get; init; } = [];
}
