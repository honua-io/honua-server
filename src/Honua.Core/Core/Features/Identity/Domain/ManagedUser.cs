// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Identity.Domain;

public sealed class ManagedUser
{
    public required string UserId { get; init; }

    // The external id is indexed so the identity resolves when the SCIM user name differs from the OIDC subject.
    public string? ExternalId { get; init; }

    public string? ExternalIssuer { get; init; }

    public required string DisplayName { get; init; }

    public string? Email { get; init; }

    public required string ProvisioningSource { get; init; }

    public Guid? ProviderId { get; init; }

    public bool IsActive { get; init; } = true;

    public IReadOnlyList<string> Roles { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
