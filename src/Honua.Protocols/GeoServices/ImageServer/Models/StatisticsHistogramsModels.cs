// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class ComputeStatisticsHistogramsResponse
{
    [JsonPropertyName("statistics")]
    public BandStatistic[] Statistics { get; init; } = [];

    [JsonPropertyName("histograms")]
    public BandHistogram[] Histograms { get; init; } = [];
}

public sealed class BandStatistic
{
    [JsonPropertyName("min")]
    public double Min { get; init; }

    [JsonPropertyName("max")]
    public double Max { get; init; }

    [JsonPropertyName("mean")]
    public double Mean { get; init; }

    [JsonPropertyName("standardDeviation")]
    public double StandardDeviation { get; init; }

    [JsonPropertyName("count")]
    public long Count { get; init; }
}

public sealed class BandHistogram
{
    // Keep canonical band identity for SOAP's named RasterHistogram_N members.
    // The REST contract remains an ordered array, without an added JSON member.
    [JsonIgnore]
    internal int SourceBand { get; init; }

    [JsonPropertyName("size")]
    public int Size { get; init; }

    [JsonPropertyName("min")]
    public double Min { get; init; }

    [JsonPropertyName("max")]
    public double Max { get; init; }

    [JsonPropertyName("counts")]
    public long[] Counts { get; init; } = [];
}
