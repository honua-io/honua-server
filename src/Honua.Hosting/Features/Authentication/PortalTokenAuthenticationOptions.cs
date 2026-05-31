// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Configuration options for the ArcGIS-compatible <c>/sharing/rest/generateToken</c>
/// endpoint and the matching <c>PortalToken</c> authentication scheme.
/// </summary>
public sealed class PortalTokenAuthenticationOptions
{
    /// <summary>
    /// Configuration section binding root.
    /// </summary>
    public const string SectionName = "Authentication:PortalToken";

    /// <summary>
    /// Default token lifetime when the caller does not request an explicit expiration.
    /// </summary>
    public const int DefaultExpirationMinutesValue = 60;

    /// <summary>
    /// Default maximum token lifetime (10 days), matching the ArcGIS Portal default.
    /// </summary>
    public const int DefaultMaxExpirationMinutesValue = 14_400;

    /// <summary>
    /// Whether the portal-token endpoint and scheme are wired into the pipeline.
    /// Defaults to <see langword="true"/>; operators can opt out by setting the
    /// configuration value to <c>false</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether token issuance is restricted to HTTPS requests. Defaults to
    /// <see langword="true"/>. Operators may opt out only for development /
    /// test fixtures.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Default token lifetime in minutes when the caller does not supply
    /// <c>expiration</c>.
    /// </summary>
    public int DefaultExpirationMinutes { get; set; } = DefaultExpirationMinutesValue;

    /// <summary>
    /// Upper bound on the lifetime a caller may request via <c>expiration</c>.
    /// Requests above this value are clamped to the maximum.
    /// </summary>
    public int MaxExpirationMinutes { get; set; } = DefaultMaxExpirationMinutesValue;
}
