// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

internal static partial class AuthenticationLog
{
    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Debug,
        Message = "Development authentication bypass enabled - allowing anonymous access")]
    public static partial void DevelopmentBypassEnabled(ILogger logger);

    [LoggerMessage(
        EventId = 4111,
        Level = LogLevel.Warning,
        Message = "SECURITY: Development authentication bypass blocked - production environment detected")]
    public static partial void DevelopmentBypassBlockedInProduction(ILogger logger);

    [LoggerMessage(
        EventId = 4112,
        Level = LogLevel.Warning,
        Message = "SECURITY WARNING: HONUA_DEV_AUTH bypass is ACTIVE for environment '{Environment}'. Admin endpoints accept any X-API-Key. This is only safe in disposable Test environments; unset HONUA_DEV_AUTH and HONUA_DEV_AUTH_ALLOW_BYPASS before deploying to Staging or Production.")]
    public static partial void DevelopmentBypassActiveAtStartup(ILogger logger, string environment);

    [LoggerMessage(
        EventId = 4113,
        Level = LogLevel.Warning,
        Message = "SECURITY: HONUA_DEV_AUTH=true is set in environment '{Environment}' but the dev-auth bypass is NOT active. Either remove HONUA_DEV_AUTH or, only for disposable Test environments, set HONUA_DEV_AUTH_ALLOW_BYPASS=true.")]
    public static partial void DevelopmentBypassRequestedButRejected(ILogger logger, string environment);

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Debug,
        Message = "No API key found in {ApiKeyHeader} header")]
    public static partial void NoApiKeyFound(ILogger logger, string apiKeyHeader);

    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Debug,
        Message = "Empty API key provided in {ApiKeyHeader} header")]
    public static partial void EmptyApiKeyProvided(ILogger logger, string apiKeyHeader);

    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Warning,
        Message = "No admin password configured in {AdminPasswordEnvVar} - authentication will fail")]
    public static partial void NoAdminPasswordConfigured(ILogger logger, string adminPasswordEnvVar);

    [LoggerMessage(
        EventId = 4104,
        Level = LogLevel.Warning,
        Message = "Invalid API key provided")]
    public static partial void InvalidApiKeyProvided(ILogger logger);

    [LoggerMessage(
        EventId = 4105,
        Level = LogLevel.Debug,
        Message = "API key authentication successful")]
    public static partial void ApiKeyAuthenticationSuccessful(ILogger logger);

    [LoggerMessage(
        EventId = 4106,
        Level = LogLevel.Warning,
        Message = "SECURITY WARNING: Development auth bypass is active because no HONUA_ADMIN_PASSWORD is configured. All admin endpoints are accessible without authentication. Set HONUA_ADMIN_PASSWORD to require API key authentication.")]
    public static partial void DevelopmentEnvironmentAuthBypass(ILogger logger);

    [LoggerMessage(
        EventId = 4107,
        Level = LogLevel.Error,
        Message = "Failed to resolve admin password from secret provider")]
    public static partial void AdminPasswordResolutionFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4108,
        Level = LogLevel.Debug,
        Message = "HTTP Basic authentication compatibility mode used")]
    public static partial void BasicAuthCompatibilityUsed(ILogger logger);

    [LoggerMessage(
        EventId = 4109,
        Level = LogLevel.Warning,
        Message = "HTTP Basic authentication compatibility mode rejected because request was not HTTPS")]
    public static partial void BasicAuthRejectedInsecureTransport(ILogger logger);

    [LoggerMessage(
        EventId = 4110,
        Level = LogLevel.Debug,
        Message = "Invalid HTTP Basic authorization header supplied")]
    public static partial void InvalidBasicAuthorizationHeader(ILogger logger);

    [LoggerMessage(
        EventId = 4112,
        Level = LogLevel.Warning,
        Message = "HONUA_DEV_AUTH set but bypass not active because {Reason}")]
    public static partial void DevelopmentBypassRejected(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 4113,
        Level = LogLevel.Error,
        Message = "SECURITY: HONUA_DEV_AUTH=true detected in Production environment. Refusing to start.")]
    public static partial void DevelopmentBypassInProductionFatal(ILogger logger);

    [LoggerMessage(
        EventId = 4114,
        Level = LogLevel.Warning,
        Message = "Scoped admin API key denied {Method} admin request: key permissions do not authorize this operation")]
    public static partial void ScopedAdminKeyDenied(ILogger logger, string method);

    [LoggerMessage(
        EventId = 4115,
        Level = LogLevel.Warning,
        Message = "Ops-reader authorization denied {Method} ops request: principal lacks an admin or ops:read grant authorizing this operation")]
    public static partial void OpsReaderKeyDenied(ILogger logger, string method);

    [LoggerMessage(
        EventId = 4116,
        Level = LogLevel.Debug,
        Message = "Studio lifecycle authorization denied: caller is not admin and Studio:EndUserAuthorization:Enabled is false")]
    public static partial void StudioEndUserModeDenied(ILogger logger);

    // A store outage falls through to the bootstrap password and otherwise fails closed instead of throwing.
    [LoggerMessage(
        EventId = 4117,
        Level = LogLevel.Error,
        Message = "Admin API key store is unavailable; bootstrap admin password remains the break-glass credential")]
    public static partial void AdminApiKeyStoreUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4119,
        Level = LogLevel.Warning,
        Message = "Credential management denied for {Method} request: principal lacks full-admin credential authority")]
    public static partial void CredentialManagementDenied(ILogger logger, string method);

    // A referrer or IP refusal logs the key identifier only, never the key material.
    [LoggerMessage(
        EventId = 4118,
        Level = LogLevel.Warning,
        Message = "API key {ApiKeyId} refused: the request does not satisfy the key's {Restriction} restriction")]
    public static partial void ApiKeyRequestRestrictionRefused(ILogger logger, Guid apiKeyId, ApiKeyRestrictionViolation restriction);
}
