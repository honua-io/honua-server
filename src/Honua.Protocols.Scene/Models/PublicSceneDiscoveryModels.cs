// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.Scene.Models;

internal sealed class PublicSceneListResponse
{
    public PublicSceneSummary[] Scenes { get; init; } = [];
}

internal sealed class PublicSceneSummary
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? TilesetUrl { get; init; }

    public PublicSceneBounds? Bounds { get; init; }

    public string[] Capabilities { get; init; } = [];

    public string[] Attribution { get; init; } = [];

    public PublicSceneAuthRequirements Auth { get; init; } = PublicSceneAuthRequirements.None;

    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed class PublicSceneMetadata
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public PublicSceneEndpoint? Tileset { get; init; }

    public PublicSceneCoordinate? Center { get; init; }

    public PublicSceneBounds? Bounds { get; init; }

    public string[] Capabilities { get; init; } = [];

    public string[] Attribution { get; init; } = [];

    public PublicSceneAuthRequirements Auth { get; init; } = PublicSceneAuthRequirements.None;

    public PublicSceneLink[] Links { get; init; } = [];

    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed class PublicSceneResolution
{
    public required string SceneId { get; init; }

    public string? TilesetUrl { get; init; }

    public PublicSceneEndpoint[] Endpoints { get; init; } = [];

    public string[] Capabilities { get; init; } = [];

    public PublicSceneAuthRequirements Auth { get; init; } = PublicSceneAuthRequirements.None;
}

internal sealed class PublicSceneEndpoint
{
    public required string Kind { get; init; }

    public required string Url { get; init; }

    public string? MediaType { get; init; }

    public string? Format { get; init; }

    public bool RequiresAuthentication { get; init; }
}

internal sealed class PublicSceneAuthRequirements
{
    public static PublicSceneAuthRequirements None { get; } = new()
    {
        RequiresAuthentication = false,
        Schemes = []
    };

    public bool RequiresAuthentication { get; init; }

    public string[] Schemes { get; init; } = [];

    public string? Policy { get; init; }
}

internal sealed class PublicSceneBounds
{
    public required double West { get; init; }

    public required double South { get; init; }

    public required double East { get; init; }

    public required double North { get; init; }
}

internal sealed class PublicSceneCoordinate
{
    public required double Latitude { get; init; }

    public required double Longitude { get; init; }

    public double? Height { get; init; }
}

internal sealed class PublicSceneLink
{
    public required string Rel { get; init; }

    public required string Href { get; init; }

    public string? Type { get; init; }

    public string? Title { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PublicSceneListResponse))]
[JsonSerializable(typeof(PublicSceneSummary))]
[JsonSerializable(typeof(PublicSceneSummary[]))]
[JsonSerializable(typeof(PublicSceneMetadata))]
[JsonSerializable(typeof(PublicSceneResolution))]
[JsonSerializable(typeof(PublicSceneEndpoint))]
[JsonSerializable(typeof(PublicSceneEndpoint[]))]
[JsonSerializable(typeof(PublicSceneAuthRequirements))]
[JsonSerializable(typeof(PublicSceneBounds))]
[JsonSerializable(typeof(PublicSceneCoordinate))]
[JsonSerializable(typeof(PublicSceneLink))]
[JsonSerializable(typeof(PublicSceneLink[]))]
internal sealed partial class PublicSceneDiscoveryJsonContext : JsonSerializerContext
{
}
