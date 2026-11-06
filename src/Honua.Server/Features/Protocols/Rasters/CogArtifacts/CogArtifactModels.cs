// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Rasters.CogArtifacts;

internal sealed record CogArtifactPublishRequest
{
    public int LayerId { get; init; }
}

internal sealed record CogArtifactDescriptor
{
    public required string ArtifactId { get; init; }

    public required int LayerId { get; init; }

    public required long RasterId { get; init; }

    public required long SizeBytes { get; init; }

    public required string ContentType { get; init; }

    public required string Url { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int BandCount { get; init; }

    public int? Srid { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }
}

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CogArtifactPublishRequest))]
[JsonSerializable(typeof(CogArtifactDescriptor))]
internal sealed partial class CogArtifactJsonContext : JsonSerializerContext
{
}
