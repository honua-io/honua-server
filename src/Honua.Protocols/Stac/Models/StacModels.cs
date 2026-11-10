// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Stac.Models;

public sealed record StacCatalog
{
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; init; } = StacConstants.StacVersion;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "Catalog";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("conformsTo")]
    public ImmutableArray<string>? ConformsTo { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "STAC specification type name")]
public sealed record StacCollection
{
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; init; } = StacConstants.StacVersion;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "Collection";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("keywords")]
    public ImmutableArray<string>? Keywords { get; init; }

    [JsonPropertyName("license")]
    public required string License { get; init; }

    [JsonPropertyName("providers")]
    public ImmutableArray<StacProvider>? Providers { get; init; }

    [JsonPropertyName("extent")]
    public required StacExtent Extent { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    [JsonPropertyName("stac_extensions")]
    public ImmutableArray<string>? StacExtensions { get; init; }
}

public sealed record StacProvider
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("roles")]
    public ImmutableArray<string>? Roles { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed record StacItem
{
    [JsonPropertyName("stac_version")]
    public string? StacVersion { get; init; } = StacConstants.StacVersion;

    [JsonPropertyName("type")]
    public string? Type { get; init; } = "Feature";

    [JsonPropertyName("id")]
    public required string? Id { get; init; }

    [JsonPropertyName("geometry")]
    public JsonElement? Geometry { get; init; }

    [JsonPropertyName("bbox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<double>? Bbox { get; init; }

    [JsonPropertyName("properties")]
    public required Dictionary<string, object?>? Properties { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link>? Links { get; init; }

    // STAC 1.0.0 requires an assets object on every item, so an item with no known assets still emits an empty dictionary.
    [JsonPropertyName("assets")]
    public required Dictionary<string, StacAsset>? Assets { get; init; }

    [JsonPropertyName("collection")]
    public string? Collection { get; init; }

    [JsonPropertyName("stac_extensions")]
    public ImmutableArray<string>? StacExtensions { get; init; }
}

public sealed record StacAsset
{
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("roles")]
    public ImmutableArray<string>? Roles { get; init; }
}

public sealed record StacExtent
{
    [JsonPropertyName("spatial")]
    public required StacSpatialExtent Spatial { get; init; }

    [JsonPropertyName("temporal")]
    public required StacTemporalExtent Temporal { get; init; }
}

public sealed record StacSpatialExtent
{
    [JsonPropertyName("bbox")]
    public required ImmutableArray<ImmutableArray<double>> Bbox { get; init; }
}

public sealed record StacTemporalExtent
{
    [JsonPropertyName("interval")]
    public required ImmutableArray<ImmutableArray<string?>> Interval { get; init; }
}

public sealed record StacSearchRequest
{
    [JsonPropertyName("bbox")]
    public ImmutableArray<double>? Bbox { get; init; }

    [JsonPropertyName("intersects")]
    public JsonElement? Intersects { get; init; }

    [JsonPropertyName("datetime")]
    public string? Datetime { get; init; }

    [JsonPropertyName("collections")]
    public ImmutableArray<string>? Collections { get; init; }

    [JsonPropertyName("ids")]
    public ImmutableArray<string>? Ids { get; init; }

    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    [JsonPropertyName("fields")]
    public StacFieldsExtension? Fields { get; init; }

    [JsonPropertyName("sortby")]
    public ImmutableArray<StacSortDefinition>? Sortby { get; init; }

    [JsonPropertyName("filter")]
    public JsonElement? Filter { get; init; }

    [JsonPropertyName("filter-crs")]
    public string? FilterCrs { get; init; }

    [JsonPropertyName("filter-lang")]
    public string? FilterLang { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }
}

public sealed record StacFieldsExtension
{
    [JsonPropertyName("include")]
    public ImmutableArray<string>? Includes { get; init; }

    [JsonPropertyName("exclude")]
    public ImmutableArray<string>? Excludes { get; init; }
}

public sealed record StacSortDefinition
{
    [JsonPropertyName("field")]
    public required string Field { get; init; }

    [JsonPropertyName("direction")]
    public string Direction { get; init; } = "asc";
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "STAC specification type name")]
public sealed record StacItemCollection
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "FeatureCollection";

    [JsonPropertyName("features")]
    public required ImmutableArray<StacItem> Features { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    [JsonPropertyName("context")]
    public StacSearchContext? Context { get; init; }

    [JsonPropertyName("numberMatched")]
    public long? NumberMatched { get; init; }

    [JsonPropertyName("numberReturned")]
    public int? NumberReturned { get; init; }
}

public sealed record StacSearchContext
{
    [JsonPropertyName("returned")]
    public required int Returned { get; init; }

    [JsonPropertyName("matched")]
    public long? Matched { get; init; }

    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}

public sealed record StacCollectionsResponse
{
    [JsonPropertyName("collections")]
    public required ImmutableArray<StacCollection> Collections { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}
