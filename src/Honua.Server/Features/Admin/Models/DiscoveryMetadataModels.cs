// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

public sealed record DiscoveryMetadataUpdateRequest
{
    public required string Etag { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string[] Keywords { get; init; } = [];
}

public sealed record DiscoveryMetadataResponse
{
    public required string Etag { get; init; }
    public required string EntityId { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<DiscoveryPublicationImpact> AffectedPublications { get; init; } = [];
}

public sealed record DiscoveryPublicationImpact(string PublicationId, string ServiceName, string Identifier);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiscoveryMetadataUpdateRequest))]
[JsonSerializable(typeof(ApiResponse<DiscoveryMetadataResponse>))]
public sealed partial class DiscoveryMetadataJsonContext : JsonSerializerContext;
