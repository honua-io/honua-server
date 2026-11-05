// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Console.Models;

public sealed class StacProjectionLink
{
    [JsonPropertyName("rel")]
    public required string Rel { get; init; }

    [JsonPropertyName("href")]
    public required string Href { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

public sealed class StacProjectionAsset
{
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("roles")]
    public IReadOnlyList<string>? Roles { get; init; }
}

public sealed class StacProjectionCatalog
{
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; init; } = "1.0.0";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "Catalog";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("links")]
    public required IReadOnlyList<StacProjectionLink> Links { get; init; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "STAC specification type name")]
public sealed class StacProjectionCollection
{
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; init; } = "1.0.0";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "Collection";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("license")]
    public required string License { get; init; }

    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; init; }

    [JsonPropertyName("extent")]
    public required StacProjectionExtent Extent { get; init; }

    [JsonPropertyName("links")]
    public required IReadOnlyList<StacProjectionLink> Links { get; init; }

    [JsonPropertyName("assets")]
    public IReadOnlyDictionary<string, StacProjectionAsset>? Assets { get; init; }
}

public sealed class StacProjectionExtent
{
    [JsonPropertyName("spatial")]
    public required StacProjectionSpatialExtent Spatial { get; init; }

    [JsonPropertyName("temporal")]
    public required StacProjectionTemporalExtent Temporal { get; init; }
}

public sealed class StacProjectionSpatialExtent
{
    [JsonPropertyName("bbox")]
    public required IReadOnlyList<IReadOnlyList<double>> Bbox { get; init; }
}

public sealed class StacProjectionTemporalExtent
{
    [JsonPropertyName("interval")]
    public required IReadOnlyList<IReadOnlyList<string?>> Interval { get; init; }
}

public sealed class StacProjectionItem
{
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; init; } = "1.0.0";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "Feature";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("collection")]
    public required string Collection { get; init; }

    [JsonPropertyName("bbox")]
    public IReadOnlyList<double>? Bbox { get; init; }

    [JsonPropertyName("geometry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public StacProjectionGeometry? Geometry { get; init; }

    [JsonPropertyName("properties")]
    public required IReadOnlyDictionary<string, string?> Properties { get; init; }

    [JsonPropertyName("links")]
    public required IReadOnlyList<StacProjectionLink> Links { get; init; }

    [JsonPropertyName("assets")]
    public IReadOnlyDictionary<string, StacProjectionAsset> Assets { get; init; } = new Dictionary<string, StacProjectionAsset>();
}

public sealed class StacProjectionGeometry
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Polygon";

    [JsonPropertyName("coordinates")]
    public required IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>> Coordinates { get; init; }
}

public sealed class SchemaOrgDataset
{
    [JsonPropertyName("@context")]
    public string Context { get; init; } = "https://schema.org";

    [JsonPropertyName("@type")]
    public string Type { get; init; } = "Dataset";

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("spatialCoverage")]
    public SchemaOrgPlace? SpatialCoverage { get; init; }

    [JsonPropertyName("temporalCoverage")]
    public string? TemporalCoverage { get; init; }

    [JsonPropertyName("publisher")]
    public SchemaOrgOrganization? Publisher { get; init; }

    [JsonPropertyName("distribution")]
    public IReadOnlyList<SchemaOrgDataDownload>? Distribution { get; init; }
}

public sealed class SchemaOrgPlace
{
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "Place";

    [JsonPropertyName("geo")]
    public required SchemaOrgGeoShape Geo { get; init; }
}

public sealed class SchemaOrgGeoShape
{
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "GeoShape";

    [JsonPropertyName("box")]
    public required string Box { get; init; }
}

public sealed class SchemaOrgOrganization
{
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "Organization";

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed class SchemaOrgDataDownload
{
    [JsonPropertyName("@type")]
    public string Type { get; init; } = "DataDownload";

    [JsonPropertyName("contentUrl")]
    public required string ContentUrl { get; init; }

    [JsonPropertyName("encodingFormat")]
    public string? EncodingFormat { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
