// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Configuration options for API key authentication
/// </summary>
public sealed class ApiKeyAuthenticationOptions
{
    /// <summary>
    /// Gets or sets whether development mode authentication bypass is enabled
    /// </summary>
    public bool IsDevelopmentMode { get; set; }

    /// <summary>
    /// Gets or sets whether the application is running in the test environment
    /// </summary>
    public bool IsTestMode { get; set; }

    /// <summary>
    /// Gets or sets the admin password for authentication
    /// </summary>
    public string? AdminPassword { get; set; }

    /// <summary>
    /// Gets or sets the development authentication bypass value
    /// </summary>
    public string? DevAuthBypass { get; set; }

    /// <summary>
    /// Gets or sets whether HTTP Basic auth is accepted as a compatibility mode.
    /// </summary>
    public bool EnableBasicAuthCompatibility { get; set; }

    /// <summary>
    /// Gets or sets whether Basic auth compatibility requires HTTPS transport.
    /// </summary>
    public bool RequireHttpsForBasicAuth { get; set; } = true;
}
