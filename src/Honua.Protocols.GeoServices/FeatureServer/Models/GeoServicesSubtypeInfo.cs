// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class GeoServicesSubtypeInfo
{
    [JsonPropertyName("code")]
    public required JsonElement Code { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("defaultValues")]
    public IReadOnlyDictionary<string, JsonElement>? DefaultValues { get; init; }

    [JsonPropertyName("domains")]
    public IReadOnlyDictionary<string, GeoServicesFieldDomainInfo>? Domains { get; init; }
}
