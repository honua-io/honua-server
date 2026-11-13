// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Studio.Models;

public sealed class StudioSessionBootstrap
{
    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; init; } = true;

    [JsonPropertyName("principalId")]
    public required string PrincipalId { get; init; }

    [JsonPropertyName("environmentId")]
    public required string EnvironmentId { get; init; }

    [JsonPropertyName("sourcePathBase")]
    public required string SourcePathBase { get; init; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; init; }
}
