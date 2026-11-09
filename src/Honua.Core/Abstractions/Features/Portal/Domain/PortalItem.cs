// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Portal.Domain;

public sealed record PortalItem
{
    [JsonPropertyName("groupCategories")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? GroupCategories { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("modified")]
    public long Modified { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("typeKeywords")]
    public IReadOnlyList<string> TypeKeywords { get; init; } = Array.Empty<string>();

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("snippet")]
    public string? Snippet { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("access")]
    public required string Access { get; init; }

    [JsonPropertyName("extent")]
    public IReadOnlyList<IReadOnlyList<double>> Extent { get; init; } =
        Array.Empty<IReadOnlyList<double>>();

    [JsonPropertyName("spatialReference")]
    public string? SpatialReference { get; init; }

    [JsonPropertyName("culture")]
    public string? Culture { get; init; }

    [JsonPropertyName("numComments")]
    public int NumComments { get; init; }

    [JsonPropertyName("numViews")]
    public int NumViews { get; init; }
}

public static class PortalAccessLevels
{
    public const string Public = "public";

    public const string Org = "org";

    public const string Private = "private";
}

public static class PortalItemTypes
{
    public const string FeatureService = "Feature Service";

    public const string MapService = "Map Service";

    public const string ImageService = "Image Service";
}
