// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class LicenseEntitlementsResponse
{
    public required string Edition { get; init; }

    public required FeatureEntitlementItem[] Features { get; init; }
}

public sealed class FeatureEntitlementItem
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public required string Category { get; init; }

    public required bool IsEnabled { get; init; }

    public required string MinimumEdition { get; init; }

    public required bool UpgradeRequired { get; init; }
}

public sealed class LicenseUploadResponse
{
    public required bool Success { get; init; }

    public required string Message { get; init; }
}

public sealed class LicenseSurgeModeRequest
{
    public required bool Enabled { get; init; }

    public string? Reason { get; init; }
}
