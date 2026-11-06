// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

public sealed class OidcProviderResponse
{
    public required Guid ProviderId { get; init; }

    public required string Name { get; init; }

    public required string ProviderType { get; init; }

    public required string Authority { get; init; }

    public required string ClientId { get; init; }

    public bool Enabled { get; init; }

    public bool IsHealthy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? LastHealthCheck { get; init; }
}

public sealed class CreateOidcProviderRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Name { get; init; }

    [Required]
    public required string ProviderType { get; init; }

    [Required]
    [Url]
    public required string Authority { get; init; }

    [Required]
    public required string ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public bool Enabled { get; init; } = true;
}

public sealed class UpdateOidcProviderRequest
{
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; init; }

    [Url]
    public string? Authority { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public bool? Enabled { get; init; }
}

public sealed class OidcProviderTestResponse
{
    public required Guid ProviderId { get; init; }

    public required bool IsReachable { get; init; }

    public required string Message { get; init; }

    public DateTimeOffset TestedAt { get; init; }
}
