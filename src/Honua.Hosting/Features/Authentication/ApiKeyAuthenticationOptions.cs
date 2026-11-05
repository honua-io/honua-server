// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

public sealed class ApiKeyAuthenticationOptions
{
    public bool IsDevelopmentMode { get; set; }

    public bool IsTestMode { get; set; }

    public string? AdminPassword { get; set; }

    public string? DevAuthBypass { get; set; }

    // A second acknowledgement is required so HONUA_DEV_AUTH alone cannot turn authentication off.
    public string? DevAuthBypassAcknowledged { get; set; }

    public string? EnvironmentName { get; set; }

    public bool EnableBasicAuthCompatibility { get; set; }

    public bool RequireHttpsForBasicAuth { get; set; } = true;
}
