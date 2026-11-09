// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class GeoServicesFieldDomainInfo
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("codedValues")]
    public GeoServicesFieldDomainCodedValueInfo[]? CodedValues { get; init; }

    [JsonPropertyName("range")]
    public JsonElement[]? Range { get; init; }

    [JsonPropertyName("mergePolicy")]
    public string? MergePolicy { get; init; }

    [JsonPropertyName("splitPolicy")]
    public string? SplitPolicy { get; init; }
}

public sealed class GeoServicesFieldDomainCodedValueInfo
{
    [JsonPropertyName("code")]
    public required JsonElement Code { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}
