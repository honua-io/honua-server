// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Records.Models;

public sealed record OgcRecordCollectionsResponse
{
    [JsonPropertyName("collections")]
    public required ImmutableArray<OgcRecordCollection> Collections { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Collection is the OGC API resource name.")]
public sealed record OgcRecordCollection
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("itemType")]
    public string ItemType { get; init; } = "record";

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "FeatureCollection is the standard GeoJSON type name.")]
public sealed record OgcRecordFeatureCollection
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "FeatureCollection";

    [JsonPropertyName("features")]
    public required ImmutableArray<OgcRecordFeature> Features { get; init; }

    [JsonPropertyName("numberMatched")]
    public int NumberMatched { get; init; }

    [JsonPropertyName("numberReturned")]
    public int NumberReturned { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

public sealed record OgcRecordFeature
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Feature";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("geometry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public OgcRecordGeometry? Geometry { get; init; }

    [JsonPropertyName("bbox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<double>? Bbox { get; init; }

    [JsonPropertyName("properties")]
    public required OgcRecordProperties Properties { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

public sealed record OgcRecordGeometry
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Polygon";

    [JsonPropertyName("coordinates")]
    public required ImmutableArray<ImmutableArray<ImmutableArray<double>>> Coordinates { get; init; }
}

public sealed record OgcRecordProperties
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("resourceType")]
    public required string ResourceType { get; init; }

    [JsonPropertyName("geometryType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeometryType { get; init; }

    [JsonPropertyName("externalIds")]
    public required ImmutableArray<string> ExternalIds { get; init; }

    [JsonPropertyName("layerIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<string>? LayerIds { get; init; }

    [JsonPropertyName("keywords")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<string>? Keywords { get; init; }

    [JsonPropertyName("themes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<ThemeMember>? Themes { get; init; }

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LanguageMember? Language { get; init; }

    [JsonPropertyName("license")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? License { get; init; }

    [JsonPropertyName("rights")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Rights { get; init; }

    [JsonPropertyName("contacts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<OgcRecordContact>? Contacts { get; init; }
}

public sealed record OgcRecordContact
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("organization")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Organization { get; init; }

    [JsonPropertyName("emails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<OgcRecordContactEmail>? Emails { get; init; }

    [JsonPropertyName("links")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<Link>? Links { get; init; }

    [JsonPropertyName("roles")]
    public required ImmutableArray<string> Roles { get; init; }
}

public sealed record OgcRecordContactEmail
{
    [JsonPropertyName("value")]
    public required string Value { get; init; }
}
