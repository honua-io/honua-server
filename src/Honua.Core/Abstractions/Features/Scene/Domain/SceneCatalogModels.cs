// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Scene.Domain;

public sealed record SceneCatalogEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required SceneServingFormat ServingFormat { get; init; }

    public required SceneContentKind ContentKind { get; init; }

    public SceneExtent? Extent { get; init; }

    public string? Crs { get; init; }

    public string TilesetFileName { get; init; } = "tileset.json";

    public AccessPolicy? AccessPolicy { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public bool RequiresAuthentication => AccessPolicy is not null;
}

public sealed record SceneCatalogAccessSessionAffordance(
    string Href,
    string Method);

public sealed record SceneCatalogEndpoint
{
    public required string Kind { get; init; }

    public required string EndpointFormat { get; init; }

    public required string Url { get; init; }

    public string? MediaType { get; init; }

    public bool RequiresAuthentication { get; init; }

    public SceneCatalogAccessSessionAffordance? AccessSession { get; init; }
}

public sealed record SceneCatalogLink(
    string Rel,
    string Href,
    string? Type = null,
    string? Title = null);

public sealed record SceneCatalogResolution
{
    public required string SceneId { get; init; }

    public required SceneServingFormat ServingFormat { get; init; }

    public required SceneContentKind ContentKind { get; init; }

    public string? TilesetUrl { get; init; }

    public required IReadOnlyList<SceneCatalogEndpoint> Endpoints { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    public bool RequiresAuthentication { get; init; }

    public required IReadOnlyList<string> AuthSchemes { get; init; }
}
