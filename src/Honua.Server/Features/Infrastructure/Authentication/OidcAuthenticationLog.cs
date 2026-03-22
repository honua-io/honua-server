// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Source-generated logger for OIDC authentication features (AOT compatible).
/// </summary>
internal static partial class OidcAuthenticationLog
{
    /// <summary>
    /// Logs when OIDC authentication is enabled.
    /// </summary>
    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Information,
        Message = "OIDC authentication enabled with providers: {Providers}")]
    public static partial void OidcAuthenticationEnabled(ILogger logger, string providers);

    /// <summary>
    /// Logs when JWT token validation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Warning,
        Message = "JWT token validation failed: {ErrorMessage}")]
    public static partial void JwtAuthenticationFailed(ILogger logger, string errorMessage);

    /// <summary>
    /// Logs when JWT token is successfully validated.
    /// </summary>
    [LoggerMessage(
        EventId = 4202,
        Level = LogLevel.Debug,
        Message = "JWT token validated")]
    public static partial void JwtTokenValidated(ILogger logger);

    /// <summary>
    /// Logs when OIDC authentication fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4203,
        Level = LogLevel.Warning,
        Message = "OIDC authentication failed for scheme {Scheme}: {ErrorMessage}")]
    public static partial void OidcAuthenticationFailed(ILogger logger, string scheme, string errorMessage);

    /// <summary>
    /// Logs when OIDC token is successfully validated.
    /// </summary>
    [LoggerMessage(
        EventId = 4204,
        Level = LogLevel.Debug,
        Message = "OIDC token validated for scheme {Scheme}")]
    public static partial void OidcTokenValidated(ILogger logger, string scheme);

    /// <summary>
    /// Logs when claims are transformed.
    /// </summary>
    [LoggerMessage(
        EventId = 4205,
        Level = LogLevel.Debug,
        Message = "Claims transformed: {ClaimCount} claims added")]
    public static partial void ClaimsTransformed(ILogger logger, int claimCount);

    /// <summary>
    /// Logs when token refresh is attempted.
    /// </summary>
    [LoggerMessage(
        EventId = 4206,
        Level = LogLevel.Debug,
        Message = "Token refresh initiated")]
    public static partial void TokenRefreshInitiated(ILogger logger);

    /// <summary>
    /// Logs when token refresh succeeds.
    /// </summary>
    [LoggerMessage(
        EventId = 4207,
        Level = LogLevel.Debug,
        Message = "Token refresh succeeded")]
    public static partial void TokenRefreshSucceeded(ILogger logger);

    /// <summary>
    /// Logs when token refresh fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4208,
        Level = LogLevel.Warning,
        Message = "Token refresh failed: {ErrorMessage}")]
    public static partial void TokenRefreshFailed(ILogger logger, string errorMessage);

    /// <summary>
    /// Logs when OIDC configuration is loaded.
    /// </summary>
    [LoggerMessage(
        EventId = 4209,
        Level = LogLevel.Information,
        Message = "OIDC configuration loaded: AzureAD={AzureAdEnabled}, Google={GoogleEnabled}, Generic={GenericEnabled}, Okta={OktaEnabled}, Auth0={Auth0Enabled}")]
    public static partial void OidcConfigurationLoaded(ILogger logger, bool azureAdEnabled, bool googleEnabled, bool genericEnabled, bool oktaEnabled, bool auth0Enabled);

    /// <summary>
    /// Logs when user is granted admin access via OIDC.
    /// </summary>
    [LoggerMessage(
        EventId = 4210,
        Level = LogLevel.Information,
        Message = "Admin access granted via role {Role}")]
    public static partial void AdminAccessGranted(ILogger logger, string role);

    /// <summary>
    /// Logs when a token replay is detected.
    /// </summary>
    [LoggerMessage(
        EventId = 4211,
        Level = LogLevel.Warning,
        Message = "OIDC token replay detected")]
    public static partial void TokenReplayDetected(ILogger logger);

}
