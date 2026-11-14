// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

public sealed record DiscoveryMetadataUpdateRequest
{
    public required string Etag { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string[] Keywords { get; init; } = [];

    private string[]? _themes;
    internal bool HasThemes { get; private set; }

    /// <summary>Omission preserves the stored value; an empty array clears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Themes
    {
        get => _themes;
        init
        {
            _themes = value;
            HasThemes = true;
        }
    }

    private string? _language;
    internal bool HasLanguage { get; private set; }

    /// <summary>Omission preserves the stored value; explicit null clears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language
    {
        get => _language;
        init
        {
            _language = value;
            HasLanguage = true;
        }
    }

    private string? _license;
    internal bool HasLicense { get; private set; }

    /// <summary>Omission preserves the stored value; explicit null clears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? License
    {
        get => _license;
        init
        {
            _license = value;
            HasLicense = true;
        }
    }

    private string? _attribution;
    internal bool HasAttribution { get; private set; }

    /// <summary>Omission preserves the stored value; explicit null clears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Attribution
    {
        get => _attribution;
        init
        {
            _attribution = value;
            HasAttribution = true;
        }
    }

    private string? _publisher;
    internal bool HasPublisher { get; private set; }

    /// <summary>Omission preserves the stored value; explicit null clears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Publisher
    {
        get => _publisher;
        init
        {
            _publisher = value;
            HasPublisher = true;
        }
    }

    private MetadataV2ContactPoint? _contactPoint;
    internal bool HasContactPoint { get; private set; }

    /// <summary>Omission preserves the stored value; explicit null clears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetadataV2ContactPoint? ContactPoint
    {
        get => _contactPoint;
        init
        {
            _contactPoint = value;
            HasContactPoint = true;
        }
    }

    private MetadataV2Link[]? _links;
    internal bool HasLinks { get; private set; }

    /// <summary>Omission preserves the stored value; an empty array clears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetadataV2Link[]? Links
    {
        get => _links;
        init
        {
            _links = value;
            HasLinks = true;
        }
    }
}

public sealed record DiscoveryMetadataResponse
{
    public required string Etag { get; init; }
    public required string EntityId { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<string> Themes { get; init; } = [];
    public string? Language { get; init; }
    public string? License { get; init; }
    public string? Attribution { get; init; }
    public string? Publisher { get; init; }
    public MetadataV2ContactPoint? ContactPoint { get; init; }
    public IReadOnlyList<MetadataV2Link> Links { get; init; } = [];
    public IReadOnlyList<DiscoveryPublicationImpact> AffectedPublications { get; init; } = [];
}

public sealed record DiscoveryPublicationImpact(string PublicationId, string ServiceName, string Identifier);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiscoveryMetadataUpdateRequest))]
[JsonSerializable(typeof(ApiResponse<DiscoveryMetadataResponse>))]
public sealed partial class DiscoveryMetadataJsonContext : JsonSerializerContext;
