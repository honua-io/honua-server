// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// Esri-conformant response for the Image Server <c>computeClassStatistics</c> endpoint: one
/// statistical signature per requested class.
/// </summary>
public sealed class ComputeClassStatisticsResponse
{
    /// <summary>Per-class statistical signatures, in request order.</summary>
    [JsonPropertyName("classStatistics")]
    public ClassStatisticEntry[] ClassStatistics { get; init; } = [];
}

/// <summary>
/// The statistical signature of one class: pixel count, per-band mean vector, per-band summaries,
/// and the band-by-band covariance matrix used by maximum-likelihood classifiers.
/// </summary>
public sealed class ClassStatisticEntry
{
    /// <summary>Caller-assigned class identifier.</summary>
    [JsonPropertyName("classId")]
    public int ClassId { get; init; }

    /// <summary>Optional class name echoed from the request.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Number of valid pixels contributing to the signature.</summary>
    [JsonPropertyName("count")]
    public long Count { get; init; }

    /// <summary>1-based band numbers, in the order the mean/covariance components appear.</summary>
    [JsonPropertyName("bands")]
    public int[] Bands { get; init; } = [];

    /// <summary>Per-band mean value (the class centroid in band space).</summary>
    [JsonPropertyName("mean")]
    public double[] Mean { get; init; } = [];

    /// <summary>Per-band minimum pixel value.</summary>
    [JsonPropertyName("min")]
    public double[] Min { get; init; } = [];

    /// <summary>Per-band maximum pixel value.</summary>
    [JsonPropertyName("max")]
    public double[] Max { get; init; } = [];

    /// <summary>Per-band sample standard deviation (square root of the covariance diagonal).</summary>
    [JsonPropertyName("standardDeviation")]
    public double[] StandardDeviation { get; init; } = [];

    /// <summary>
    /// Band-by-band sample covariance matrix (divided by n-1). Symmetric and
    /// <see cref="Bands"/>.Length square; all zero when the class has fewer than two pixels.
    /// </summary>
    [JsonPropertyName("covarianceMatrix")]
    public double[][] CovarianceMatrix { get; init; } = [];
}
