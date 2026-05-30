// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Source-generated logger for Authentication features (AOT compatible)
/// </summary>
internal static partial class AuthenticationLog
{
    /// <summary>
    /// Logs when development authentication bypass is enabled
    /// </summary>
    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Debug,
        Message = "Development authentication bypass enabled - allowing anonymous access")]
    public static partial void DevelopmentBypassEnabled(ILogger logger);

    /// <summary>
    /// Logs when development authentication bypass is blocked in production environment
    /// </summary>
    [LoggerMessage(
        EventId = 4111,
        Level = LogLevel.Warning,
        Message = "SECURITY: Development authentication bypass blocked - production environment detected")]
    public static partial void DevelopmentBypassBlockedInProduction(ILogger logger);

    /// <summary>
    /// Logs once at startup when the development authentication bypass is fully
    /// enabled. Operators monitoring logs MUST see this warning if admin
    /// endpoints are accessible without an API key.
    /// </summary>
    [LoggerMessage(
        EventId = 4112,
        Level = LogLevel.Warning,
        Message = "SECURITY WARNING: HONUA_DEV_AUTH bypass is ACTIVE for environment '{Environment}'. Admin endpoints accept any X-API-Key. This is only safe in disposable Test environments; unset HONUA_DEV_AUTH and HONUA_DEV_AUTH_ALLOW_BYPASS before deploying to Staging or Production.")]
    public static partial void DevelopmentBypassActiveAtStartup(ILogger logger, string environment);

    /// <summary>
    /// Logs once at startup when HONUA_DEV_AUTH=true is configured but the
    /// bypass cannot activate because the explicit acknowledgement
    /// (HONUA_DEV_AUTH_ALLOW_BYPASS=true) or the environment guard is missing.
    /// Surfaces accidental staging/QA configuration drift.
    /// </summary>
    [LoggerMessage(
        EventId = 4113,
        Level = LogLevel.Warning,
        Message = "SECURITY: HONUA_DEV_AUTH=true is set in environment '{Environment}' but the dev-auth bypass is NOT active. Either remove HONUA_DEV_AUTH or, only for disposable Test environments, set HONUA_DEV_AUTH_ALLOW_BYPASS=true.")]
    public static partial void DevelopmentBypassRequestedButRejected(ILogger logger, string environment);

    /// <summary>
    /// Logs when no API key is found in headers
    /// </summary>
    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Debug,
        Message = "No API key found in {ApiKeyHeader} header")]
    public static partial void NoApiKeyFound(ILogger logger, string apiKeyHeader);

    /// <summary>
    /// Logs when empty API key is provided
    /// </summary>
    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Debug,
        Message = "Empty API key provided in {ApiKeyHeader} header")]
    public static partial void EmptyApiKeyProvided(ILogger logger, string apiKeyHeader);

    /// <summary>
    /// Logs when no admin password is configured
    /// </summary>
    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Warning,
        Message = "No admin password configured in {AdminPasswordEnvVar} - authentication will fail")]
    public static partial void NoAdminPasswordConfigured(ILogger logger, string adminPasswordEnvVar);

    /// <summary>
    /// Logs when invalid API key is provided
    /// </summary>
    [LoggerMessage(
        EventId = 4104,
        Level = LogLevel.Warning,
        Message = "Invalid API key provided")]
    public static partial void InvalidApiKeyProvided(ILogger logger);

    /// <summary>
    /// Logs successful API key authentication
    /// </summary>
    [LoggerMessage(
        EventId = 4105,
        Level = LogLevel.Debug,
        Message = "API key authentication successful")]
    public static partial void ApiKeyAuthenticationSuccessful(ILogger logger);

    /// <summary>
    /// Logs when development environment with no admin password enables auth bypass.
    /// This is a security-sensitive path: all admin endpoints are accessible without authentication.
    /// </summary>
    [LoggerMessage(
        EventId = 4106,
        Level = LogLevel.Warning,
        Message = "SECURITY WARNING: Development auth bypass is active because no HONUA_ADMIN_PASSWORD is configured. All admin endpoints are accessible without authentication. Set HONUA_ADMIN_PASSWORD to require API key authentication.")]
    public static partial void DevelopmentEnvironmentAuthBypass(ILogger logger);

    /// <summary>
    /// Logs when resolving the admin password via secret providers fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4107,
        Level = LogLevel.Error,
        Message = "Failed to resolve admin password from secret provider")]
    public static partial void AdminPasswordResolutionFailed(ILogger logger, Exception exception);

    /// <summary>
    /// Logs when Basic auth compatibility mode is used.
    /// </summary>
    [LoggerMessage(
        EventId = 4108,
        Level = LogLevel.Debug,
        Message = "HTTP Basic authentication compatibility mode used")]
    public static partial void BasicAuthCompatibilityUsed(ILogger logger);

    /// <summary>
    /// Logs when Basic auth compatibility mode is rejected due to insecure transport.
    /// </summary>
    [LoggerMessage(
        EventId = 4109,
        Level = LogLevel.Warning,
        Message = "HTTP Basic authentication compatibility mode rejected because request was not HTTPS")]
    public static partial void BasicAuthRejectedInsecureTransport(ILogger logger);

    /// <summary>
    /// Logs when an invalid Basic auth header is supplied.
    /// </summary>
    [LoggerMessage(
        EventId = 4110,
        Level = LogLevel.Debug,
        Message = "Invalid HTTP Basic authorization header supplied")]
    public static partial void InvalidBasicAuthorizationHeader(ILogger logger);

    /// <summary>
    /// Logs when HONUA_DEV_AUTH=true is set but the bypass is not activated because
    /// preconditions (environment must be exactly "Test" and a matching
    /// HONUA_DEV_AUTH_ALLOW_BYPASS token must be set) were not met. This is a startup-time
    /// security signal so developers learn that their attempted opt-in was rejected.
    /// </summary>
    [LoggerMessage(
        EventId = 4112,
        Level = LogLevel.Warning,
        Message = "HONUA_DEV_AUTH set but bypass not active because {Reason}")]
    public static partial void DevelopmentBypassRejected(ILogger logger, string reason);

    /// <summary>
    /// Logs when HONUA_DEV_AUTH=true is detected in a production environment. This
    /// is a critical misconfiguration and the application will refuse to start.
    /// </summary>
    [LoggerMessage(
        EventId = 4113,
        Level = LogLevel.Error,
        Message = "SECURITY: HONUA_DEV_AUTH=true detected in Production environment. Refusing to start.")]
    public static partial void DevelopmentBypassInProductionFatal(ILogger logger);
}
