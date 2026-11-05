// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

[JsonConverter(typeof(GeoServicesFeatureJsonConverter))]
public sealed class GeoServicesFeature
{
    public required Dictionary<string, object?> Attributes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesGeometry? Geometry { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesGeometry? Centroid { get; init; }

    [JsonIgnore]
    public bool IncludeGeometry { get; init; } = true;

    [JsonIgnore]
    internal bool ClearGeometry { get; init; }

    [JsonIgnore]
    internal bool ReplaceAttributes { get; init; }

    [JsonIgnore]
    internal IReadOnlySet<string> InheritedNonEditableAttributes { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
