// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class IdentityProvidersResponse
{
    public required bool Enabled { get; init; }

    public required IdentityProviderStatus[] Providers { get; init; }
}

public sealed class IdentityProviderStatus
{
    public required string Type { get; init; }

    public required bool Enabled { get; init; }

    public required string DisplayName { get; init; }

    public string? Authority { get; init; }

    public string? CallbackPath { get; init; }

    public string[]? Scopes { get; init; }

    public required bool IsConfigurationValid { get; init; }
}

public sealed class IdentityProviderTestResult
{
    public required string ProviderType { get; init; }

    public required bool IsReachable { get; init; }

    public double? ResponseTimeMs { get; init; }

    public string? DiscoveryUrl { get; init; }

    public string? ErrorMessage { get; init; }

    public string? Issuer { get; init; }
}
