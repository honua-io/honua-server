// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2TimeRange
{
    [JsonPropertyName("start")]
    public DateTimeOffset? Start { get; init; }

    [JsonPropertyName("end")]
    public DateTimeOffset? End { get; init; }
}

public sealed record MetadataV2ResourceTemporal
{
    [JsonPropertyName("startTimeField")]
    public string? StartTimeField { get; init; }

    [JsonPropertyName("endTimeField")]
    public string? EndTimeField { get; init; }

    [JsonPropertyName("trackIdField")]
    public string? TrackIdField { get; init; }

    [JsonPropertyName("extent")]
    public MetadataV2TimeRange? Extent { get; init; }
}
