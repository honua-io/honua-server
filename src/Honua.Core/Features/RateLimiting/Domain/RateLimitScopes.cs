// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.RateLimiting.Domain;

public static class RateLimitScopes
{
    public const string Plan = "plan";

    public const string Tenant = "tenant";

    public const string ApiKey = "api-key";

    public const string Endpoint = "endpoint";

    public static IReadOnlyList<string> TierPrecedence { get; } = new[] { ApiKey, Tenant, Plan };

    public static bool IsKnown(string? scope) =>
        string.Equals(scope, Plan, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, Tenant, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, ApiKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, Endpoint, StringComparison.OrdinalIgnoreCase);
}
