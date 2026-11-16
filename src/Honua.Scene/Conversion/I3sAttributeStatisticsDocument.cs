// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Scene.Conversion;

public sealed class I3sAttributeStatisticsDocument
{
    [JsonPropertyName("stats")]
    public I3sAttributeStatistics Stats { get; set; } = new();
}

public sealed class I3sAttributeStatistics
{
    [JsonPropertyName("totalValuesCount")]
    public long TotalValuesCount { get; set; }

    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Min { get; set; }

    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; set; }

    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Count { get; set; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(I3sAttributeStatisticsDocument))]
[JsonSerializable(typeof(I3sAttributeStatistics))]
public sealed partial class I3sAttributeStatisticsJsonContext : JsonSerializerContext
{
}
