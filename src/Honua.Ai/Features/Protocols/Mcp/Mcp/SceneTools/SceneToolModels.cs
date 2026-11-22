// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.SceneTools;

internal sealed class McpSceneListArgument
{
    public string? ServingFormat { get; init; }

    public string? ContentKind { get; init; }

    public string? Filter { get; init; }

    public int? Limit { get; init; }

    public int? Offset { get; init; }
}

internal sealed class McpSceneGetArgument
{
    public string? SceneId { get; init; }
}

internal sealed class McpSceneResolveArgument
{
    public string? SceneId { get; init; }

    public string[]? PreferredFormats { get; init; }
}

internal sealed class McpSceneListOutput
{
    public int SceneCount { get; init; }

    public int TotalCount { get; init; }

    public int Offset { get; init; }

    public bool HasMore { get; init; }

    public int? NextOffset { get; init; }

    public IReadOnlyList<McpSceneSummary> Scenes { get; init; } = [];
}

internal sealed class McpSceneSummary
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string ServingFormat { get; init; }

    public required string ContentKind { get; init; }

    public string? TilesetUrl { get; init; }

    public McpSceneBounds? Bounds { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public McpSceneAuthRequirements Auth { get; init; } = McpSceneAuthRequirements.None;

    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed class McpSceneMetadataOutput
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string ServingFormat { get; init; }

    public required string ContentKind { get; init; }

    public McpSceneEndpoint? Tileset { get; init; }

    public McpSceneCoordinate? Center { get; init; }

    public McpSceneBounds? Bounds { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public McpSceneAuthRequirements Auth { get; init; } = McpSceneAuthRequirements.None;

    public IReadOnlyList<McpSceneLink> Links { get; init; } = [];

    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed class McpSceneResolveOutput
{
    public required string SceneId { get; init; }

    public required string ServingFormat { get; init; }

    public required string ContentKind { get; init; }

    public string? TilesetUrl { get; init; }

    public IReadOnlyList<McpSceneEndpoint> Endpoints { get; init; } = [];

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public McpSceneAuthRequirements Auth { get; init; } = McpSceneAuthRequirements.None;
}

internal sealed class McpSceneEndpoint
{
    public required string Kind { get; init; }

    public required string EndpointFormat { get; init; }

    public required string Url { get; init; }

    public string? MediaType { get; init; }

    public bool RequiresAuthentication { get; init; }

    public McpSceneAccessSessionAffordance? AccessSession { get; init; }
}

internal sealed class McpSceneAccessSessionAffordance
{
    public required string Href { get; init; }

    public required string Method { get; init; }
}

internal sealed class McpSceneAuthRequirements
{
    public static McpSceneAuthRequirements None { get; } = new()
    {
        RequiresAuthentication = false,
        Schemes = []
    };

    public bool RequiresAuthentication { get; init; }

    public IReadOnlyList<string> Schemes { get; init; } = [];
}

internal sealed class McpSceneBounds
{
    public required double West { get; init; }

    public required double South { get; init; }

    public required double East { get; init; }

    public required double North { get; init; }
}

internal sealed class McpSceneCoordinate
{
    public required double Latitude { get; init; }

    public required double Longitude { get; init; }

    public double? Height { get; init; }
}

internal sealed class McpSceneLink
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
[JsonSerializable(typeof(McpSceneListArgument))]
[JsonSerializable(typeof(McpSceneGetArgument))]
[JsonSerializable(typeof(McpSceneResolveArgument))]
[JsonSerializable(typeof(McpSceneListOutput))]
[JsonSerializable(typeof(McpSceneSummary))]
[JsonSerializable(typeof(McpSceneMetadataOutput))]
[JsonSerializable(typeof(McpSceneResolveOutput))]
[JsonSerializable(typeof(McpSceneEndpoint))]
[JsonSerializable(typeof(McpSceneAccessSessionAffordance))]
[JsonSerializable(typeof(McpSceneAuthRequirements))]
[JsonSerializable(typeof(McpSceneBounds))]
[JsonSerializable(typeof(McpSceneCoordinate))]
[JsonSerializable(typeof(McpSceneLink))]
internal sealed partial class SceneToolJsonContext : JsonSerializerContext
{
}
