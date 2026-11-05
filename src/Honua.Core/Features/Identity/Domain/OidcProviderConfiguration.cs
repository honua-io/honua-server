// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Identity.Domain;

public sealed class OidcProviderConfiguration
{
    public Guid ProviderId { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    public required string ProviderType { get; init; }

    public required string Authority { get; init; }

    public required string ClientId { get; init; }

    // The client secret is stored encrypted and is never returned in an API response.
    public string? ClientSecret { get; init; }

    public bool Enabled { get; init; } = true;

    public bool IsHealthy { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastHealthCheck { get; init; }
}
