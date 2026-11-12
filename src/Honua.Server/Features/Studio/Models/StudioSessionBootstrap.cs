// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Studio.Models;

/// <summary>
/// The session bootstrap the browser Console reads before it opens a Studio workspace. Every value
/// comes from the authenticated request or server configuration, never from the caller.
/// </summary>
public sealed class StudioSessionBootstrap
{
    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; init; } = true;

    /// <summary>The authenticated caller's actor id, as Studio records it for ownership.</summary>
    [JsonPropertyName("principalId")]
    public required string PrincipalId { get; init; }

    /// <summary>The environment this server serves (<c>Studio:Session:EnvironmentId</c>).</summary>
    [JsonPropertyName("environmentId")]
    public required string EnvironmentId { get; init; }

    /// <summary>
    /// The path prefix of this server's API, without a trailing slash; empty when the API is
    /// served at the origin root.
    /// </summary>
    [JsonPropertyName("sourcePathBase")]
    public required string SourcePathBase { get; init; }

    /// <summary>The tenant resolved for this request, when tenancy applies.</summary>
    [JsonPropertyName("tenantId")]
    public string? TenantId { get; init; }
}
