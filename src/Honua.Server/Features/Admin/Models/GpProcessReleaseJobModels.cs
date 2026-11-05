// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GpProcessReleaseJobRequest
{
    [JsonPropertyName("params")]
    public JsonElement Params { get; init; }

    // A normal setter preserves the initializer when source-generated JSON omits this
    // property. An explicit null still reaches the adapter's invalid-scope check.
    public IReadOnlyList<GpProcessReleaseJobScope> DeclaredScope { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GpProcessReleaseJobScope
{
    public string ServiceId { get; init; } = string.Empty;

    public string? LayerId { get; init; }

    public string Access { get; init; } = "read";
}

public sealed class GpProcessReleaseJobResponse
{
    public required string JobId { get; init; }

    public required string StatusUrl { get; init; }

    public required string DiagnosticsUrl { get; init; }

    public required string ArtifactsUrl { get; init; }
}
