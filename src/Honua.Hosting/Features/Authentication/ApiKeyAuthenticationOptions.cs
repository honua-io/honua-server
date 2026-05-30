// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

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
    /// Gets or sets the explicit operator acknowledgement that the development
    /// authentication bypass should be honoured. Required in addition to
    /// <see cref="DevAuthBypass"/> so the bypass cannot be silently activated
    /// by a stray <c>HONUA_DEV_AUTH=true</c> in a Staging/QA environment.
    /// </summary>
    public string? DevAuthBypassAcknowledged { get; set; }

    /// <summary>
    /// Gets or sets the ASPNETCORE_ENVIRONMENT name as resolved at startup.
    /// The development auth bypass is only honoured when this is "Development"
    /// or "Test"; every other value (including Staging, QA, and Production)
    /// must reject the bypass even if <see cref="IsTestMode"/> is somehow set.
    /// </summary>
    public string? EnvironmentName { get; set; }

    /// <summary>
    /// Gets or sets whether HTTP Basic auth is accepted as a compatibility mode.
    /// </summary>
    public bool EnableBasicAuthCompatibility { get; set; }

    /// <summary>
    /// Gets or sets whether Basic auth compatibility requires HTTPS transport.
    /// </summary>
    public bool RequireHttpsForBasicAuth { get; set; } = true;
}
