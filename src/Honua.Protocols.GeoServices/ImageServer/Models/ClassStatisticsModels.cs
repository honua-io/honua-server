// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class ComputeClassStatisticsResponse
{
    [JsonPropertyName("classStatistics")]
    public ClassStatisticEntry[] ClassStatistics { get; init; } = [];
}

public sealed class ClassStatisticEntry
{
    [JsonPropertyName("classId")]
    public int ClassId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("count")]
    public long Count { get; init; }

    [JsonPropertyName("bands")]
    public int[] Bands { get; init; } = [];

    [JsonPropertyName("mean")]
    public double[] Mean { get; init; } = [];

    [JsonPropertyName("min")]
    public double[] Min { get; init; } = [];

    [JsonPropertyName("max")]
    public double[] Max { get; init; } = [];

    [JsonPropertyName("standardDeviation")]
    public double[] StandardDeviation { get; init; } = [];

    [JsonPropertyName("covarianceMatrix")]
    public double[][] CovarianceMatrix { get; init; } = [];
}
