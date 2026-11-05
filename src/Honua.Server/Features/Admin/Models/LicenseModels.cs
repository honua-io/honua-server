// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Server.Features.Admin.Models;

public sealed class LicenseStatusResponse
{
    public string Mode { get; init; } = "enabled";

    public required string Edition { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public required bool IsValid { get; init; }

    public int? DaysUntilExpiry { get; init; }

    public bool ExpiryWarning { get; init; }

    public required string ValidationState { get; init; }

    public string? LicensedTo { get; init; }

    public string? LicenseId { get; init; }

    public DateTimeOffset? IssuedAt { get; init; }

    public IReadOnlyList<EntitlementResponse> Entitlements { get; init; } = [];

    public LicenseCapacityState? Capacity { get; init; }
}

public sealed class EntitlementResponse
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public bool IsActive { get; init; }
}
