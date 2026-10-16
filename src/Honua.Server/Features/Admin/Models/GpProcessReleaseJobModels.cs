// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>User inputs for a job admitted against the active release of a logical process.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GpProcessReleaseJobRequest
{
    /// <summary>Opaque process parameters. Execution identity is resolved by the server.</summary>
    [JsonPropertyName("params")]
    public JsonElement Params { get; init; }

    /// <summary>Callback resource access, attenuated against the submitting principal.</summary>
    public IReadOnlyList<GpProcessReleaseJobScope> DeclaredScope { get; init; } = [];
}

/// <summary>One requested callback resource permission.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GpProcessReleaseJobScope
{
    /// <summary>Service identifier.</summary>
    public string ServiceId { get; init; } = string.Empty;

    /// <summary>Optional layer identifier; omitted means the service scope.</summary>
    public string? LayerId { get; init; }

    /// <summary>Exactly <c>read</c> or <c>write</c>.</summary>
    public string Access { get; init; } = "read";
}

/// <summary>Accepted durable job and its authenticated observability routes.</summary>
public sealed class GpProcessReleaseJobResponse
{
    /// <summary>The durable job identifier, also returned on an identical keyed replay.</summary>
    public required string JobId { get; init; }

    /// <summary>Canonical admin job status URL.</summary>
    public required string StatusUrl { get; init; }

    /// <summary>Canonical diagnostics URL, including the admitted release provenance.</summary>
    public required string DiagnosticsUrl { get; init; }

    /// <summary>Canonical artifact listing URL.</summary>
    public required string ArtifactsUrl { get; init; }
}
