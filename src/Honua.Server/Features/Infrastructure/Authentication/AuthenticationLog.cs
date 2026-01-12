// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Authentication;

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
    /// Logs when development environment with no admin password enables auth bypass
    /// </summary>
    [LoggerMessage(
        EventId = 4106,
        Level = LogLevel.Information,
        Message = "Development environment with no admin password configured - enabling auth bypass")]
    public static partial void DevelopmentEnvironmentAuthBypass(ILogger logger);

    /// <summary>
    /// Logs when resolving the admin password via secret providers fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4107,
        Level = LogLevel.Error,
        Message = "Failed to resolve admin password from secret provider")]
    public static partial void AdminPasswordResolutionFailed(ILogger logger, Exception exception);
}
