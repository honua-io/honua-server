// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

public sealed class LicenseInfo
{
    public LicenseMode Mode { get; init; } = LicenseMode.Enabled;

    public required string Edition { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public required bool IsValid { get; init; }

    public required string ValidationState { get; init; }

    public string? LicensedTo { get; init; }

    public string? LicenseId { get; init; }

    public DateTimeOffset? IssuedAt { get; init; }

    public IReadOnlyList<Entitlement> Entitlements { get; init; } = [];

    public LicenseCapacityTerms? CapacityTerms { get; init; }
}

public sealed class Entitlement
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public bool IsActive { get; init; }
}
