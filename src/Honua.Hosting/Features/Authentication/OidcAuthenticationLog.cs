// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

internal static partial class OidcAuthenticationLog
{
    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Information,
        Message = "OIDC authentication enabled with providers: {Providers}")]
    public static partial void OidcAuthenticationEnabled(ILogger logger, string providers);

    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Warning,
        Message = "JWT token validation failed: {ErrorMessage}")]
    public static partial void JwtAuthenticationFailed(ILogger logger, string errorMessage);

    [LoggerMessage(
        EventId = 4202,
        Level = LogLevel.Debug,
        Message = "JWT token validated")]
    public static partial void JwtTokenValidated(ILogger logger);

    [LoggerMessage(
        EventId = 4203,
        Level = LogLevel.Warning,
        Message = "OIDC authentication failed for scheme {Scheme}: {ErrorMessage}")]
    public static partial void OidcAuthenticationFailed(ILogger logger, string scheme, string errorMessage);

    [LoggerMessage(
        EventId = 4204,
        Level = LogLevel.Debug,
        Message = "OIDC token validated for scheme {Scheme}")]
    public static partial void OidcTokenValidated(ILogger logger, string scheme);

    [LoggerMessage(
        EventId = 4205,
        Level = LogLevel.Debug,
        Message = "Claims transformed: {ClaimCount} claims added")]
    public static partial void ClaimsTransformed(ILogger logger, int claimCount);

    [LoggerMessage(
        EventId = 4206,
        Level = LogLevel.Debug,
        Message = "Token refresh initiated")]
    public static partial void TokenRefreshInitiated(ILogger logger);

    [LoggerMessage(
        EventId = 4207,
        Level = LogLevel.Debug,
        Message = "Token refresh succeeded")]
    public static partial void TokenRefreshSucceeded(ILogger logger);

    [LoggerMessage(
        EventId = 4208,
        Level = LogLevel.Warning,
        Message = "Token refresh failed: {ErrorMessage}")]
    public static partial void TokenRefreshFailed(ILogger logger, string errorMessage);

    [LoggerMessage(
        EventId = 4209,
        Level = LogLevel.Information,
        Message = "OIDC configuration loaded: AzureAD={AzureAdEnabled}, Google={GoogleEnabled}, Generic={GenericEnabled}, Okta={OktaEnabled}, Auth0={Auth0Enabled}")]
    public static partial void OidcConfigurationLoaded(ILogger logger, bool azureAdEnabled, bool googleEnabled, bool genericEnabled, bool oktaEnabled, bool auth0Enabled);

    [LoggerMessage(
        EventId = 4210,
        Level = LogLevel.Information,
        Message = "Admin access granted via role {Role}")]
    public static partial void AdminAccessGranted(ILogger logger, string role);

    [LoggerMessage(
        EventId = 4211,
        Level = LogLevel.Warning,
        Message = "OIDC token replay detected")]
    public static partial void TokenReplayDetected(ILogger logger);

    [LoggerMessage(
        EventId = 4212,
        Level = LogLevel.Warning,
        Message = "OIDC token replay protection could not use Redis because the multiplexer is disconnected. Falling back to in-memory protection.")]
    public static partial void TokenReplayRedisDisconnected(ILogger logger);

    [LoggerMessage(
        EventId = 4213,
        Level = LogLevel.Warning,
        Message = "OIDC token replay protection failed while accessing Redis. Falling back to in-memory protection.")]
    public static partial void TokenReplayRedisAccessFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4214,
        Level = LogLevel.Warning,
        Message = "OIDC token replay protection is enabled but no replay cache is available. Allowing token to avoid permanent lockout.")]
    public static partial void TokenReplayCacheUnavailable(ILogger logger);

    [LoggerMessage(
        EventId = 4215,
        Level = LogLevel.Information,
        Message = "Portal named-user credential rejected by OIDC verifier: {Reason}")]
    public static partial void PortalCredentialRejected(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 4216,
        Level = LogLevel.Warning,
        Message = "OIDC discovery metadata unavailable for portal credential verification: {Reason}")]
    public static partial void PortalCredentialMetadataUnavailable(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 4217,
        Level = LogLevel.Warning,
        Message = "OIDC token rejected: replay protection cache is unavailable and ReplayProtectionFailClosed is enabled.")]
    public static partial void TokenReplayProtectionUnavailableFailClosed(ILogger logger);

    [LoggerMessage(
        EventId = 4218,
        Level = LogLevel.Debug,
        Message = "OIDC custom claims mapping is configured but the identity.claims-mapping entitlement is not active; applying default claims normalization only.")]
    public static partial void CustomClaimsMappingNotEntitled(ILogger logger);

    [LoggerMessage(
        EventId = 4219,
        Level = LogLevel.Debug,
        Message = "OIDC token reuse admitted as a continuation of the server-issued session it is bound to.")]
    public static partial void TokenReplayContinuationAccepted(ILogger logger);

    [LoggerMessage(
        EventId = 4220,
        Level = LogLevel.Warning,
        Message = "OIDC token replay continuation could not be recorded in Redis; reuse of this token will be rejected as a replay.")]
    public static partial void TokenReplayContinuationBindFailed(ILogger logger, Exception exception);

}
