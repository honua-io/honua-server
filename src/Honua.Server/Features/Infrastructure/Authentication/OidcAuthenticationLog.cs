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
        Message = "JWT token validated for user: {UserId}")]
    public static partial void JwtTokenValidated(ILogger logger, string userId);

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
        Message = "OIDC token validated for scheme {Scheme}, user: {UserId}")]
    public static partial void OidcTokenValidated(ILogger logger, string scheme, string userId);

    /// <summary>
    /// Logs when claims are transformed.
    /// </summary>
    [LoggerMessage(
        EventId = 4205,
        Level = LogLevel.Debug,
        Message = "Claims transformed: {ClaimCount} claims added for user {UserId}")]
    public static partial void ClaimsTransformed(ILogger logger, int claimCount, string userId);

    /// <summary>
    /// Logs when token refresh is attempted.
    /// </summary>
    [LoggerMessage(
        EventId = 4206,
        Level = LogLevel.Debug,
        Message = "Token refresh initiated for user: {UserId}")]
    public static partial void TokenRefreshInitiated(ILogger logger, string userId);

    /// <summary>
    /// Logs when token refresh succeeds.
    /// </summary>
    [LoggerMessage(
        EventId = 4207,
        Level = LogLevel.Debug,
        Message = "Token refresh succeeded for user: {UserId}")]
    public static partial void TokenRefreshSucceeded(ILogger logger, string userId);

    /// <summary>
    /// Logs when token refresh fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4208,
        Level = LogLevel.Warning,
        Message = "Token refresh failed for user {UserId}: {ErrorMessage}")]
    public static partial void TokenRefreshFailed(ILogger logger, string userId, string errorMessage);

    /// <summary>
    /// Logs when OIDC configuration is loaded.
    /// </summary>
    [LoggerMessage(
        EventId = 4209,
        Level = LogLevel.Information,
        Message = "OIDC configuration loaded: AzureAD={AzureAdEnabled}, Google={GoogleEnabled}, Generic={GenericEnabled}")]
    public static partial void OidcConfigurationLoaded(ILogger logger, bool azureAdEnabled, bool googleEnabled, bool genericEnabled);

    /// <summary>
    /// Logs when user is granted admin access via OIDC.
    /// </summary>
    [LoggerMessage(
        EventId = 4210,
        Level = LogLevel.Information,
        Message = "Admin access granted to OIDC user {UserId} via role {Role}")]
    public static partial void AdminAccessGranted(ILogger logger, string userId, string role);
}
