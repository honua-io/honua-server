// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Service for validating configuration at startup.
/// </summary>
internal static class ConfigurationValidationService
{
    /// <summary>
    /// Validates required configuration values at startup.
    /// </summary>
    /// <param name="configuration">The configuration to validate.</param>
    /// <param name="logger">Logger for validation messages.</param>
    /// <param name="isDevelopment">Whether running in development mode.</param>
    /// <returns>List of validation errors, empty if configuration is valid.</returns>
    public static List<string> ValidateConfiguration(
        IConfiguration configuration,
        ILogger logger,
        bool isDevelopment)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Check database connection (required in production)
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrEmpty(connectionString) && !isDevelopment)
        {
            errors.Add("ConnectionStrings__DefaultConnection is required. Set this environment variable to your PostgreSQL connection string.");
        }

        // Check feature flags and log their status
        LogFeatureStatus(configuration, logger, "HONUA_ADMIN_UI", "Admin UI");
        LogFeatureStatus(configuration, logger, "HONUA_OBSERVABILITY", "Observability");
        LogFeatureStatus(configuration, logger, "HONUA_OPENTELEMETRY", "OpenTelemetry tracing");
        LogFeatureStatus(configuration, logger, "HONUA_SKIP_MIGRATIONS", "Skip migrations");

        // Warn about dev-only settings in production
        if (!isDevelopment)
        {
            if (configuration.IsFeatureEnabled("DEV_AUTH"))
            {
                warnings.Add("HONUA_DEV_AUTH is enabled. This should only be used in development environments.");
            }

            if (string.IsNullOrEmpty(configuration["HONUA_ADMIN_PASSWORD"]))
            {
                warnings.Add("HONUA_ADMIN_PASSWORD is not set. Admin endpoints will require authentication in production.");
            }
        }

        // Log configuration summary
        LogConfigurationSummary(configuration, logger);

        // Log warnings
        foreach (var warning in warnings)
        {
            ConfigurationLog.ConfigurationWarning(logger, warning);
        }

        // Log errors
        foreach (var error in errors)
        {
            ConfigurationLog.ConfigurationError(logger, error);
        }

        return errors;
    }

    private static void LogFeatureStatus(IConfiguration configuration, ILogger logger, string featureName, string displayName)
    {
        var isEnabled = configuration.IsFeatureEnabled(featureName.Replace("HONUA_", ""));
        ConfigurationLog.FeatureStatus(logger, displayName, isEnabled ? "enabled" : "disabled");
    }

    private static void LogConfigurationSummary(IConfiguration configuration, ILogger logger)
    {
        var hasConnection = !string.IsNullOrEmpty(configuration.GetConnectionString("DefaultConnection"));
        var cacheEnabled = configuration.GetValue<bool>("Cache:Enabled");

        ConfigurationLog.ConfigurationSummary(logger,
            hasConnection ? "configured" : "not configured",
            cacheEnabled ? "enabled" : "disabled");
    }
}

/// <summary>
/// Source-generated logger for configuration validation (AOT compatible).
/// </summary>
internal static partial class ConfigurationLog
{
    /// <summary>
    /// Log feature flag status.
    /// </summary>
    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Information,
        Message = "Feature '{FeatureName}' is {Status}")]
    public static partial void FeatureStatus(ILogger logger, string featureName, string status);

    /// <summary>
    /// Log configuration summary at startup.
    /// </summary>
    [LoggerMessage(
        EventId = 4011,
        Level = LogLevel.Information,
        Message = "Configuration summary: Database {DatabaseStatus}, Cache {CacheStatus}")]
    public static partial void ConfigurationSummary(ILogger logger, string databaseStatus, string cacheStatus);

    /// <summary>
    /// Log configuration warning.
    /// </summary>
    [LoggerMessage(
        EventId = 4012,
        Level = LogLevel.Warning,
        Message = "Configuration warning: {Message}")]
    public static partial void ConfigurationWarning(ILogger logger, string message);

    /// <summary>
    /// Log configuration error.
    /// </summary>
    [LoggerMessage(
        EventId = 4013,
        Level = LogLevel.Error,
        Message = "Configuration error: {Message}")]
    public static partial void ConfigurationError(ILogger logger, string message);

    /// <summary>
    /// Log successful configuration validation.
    /// </summary>
    [LoggerMessage(
        EventId = 4014,
        Level = LogLevel.Information,
        Message = "Configuration validation completed successfully")]
    public static partial void ConfigurationValidationSuccess(ILogger logger);

    /// <summary>
    /// Log failed configuration validation.
    /// </summary>
    [LoggerMessage(
        EventId = 4015,
        Level = LogLevel.Error,
        Message = "Configuration validation failed with {ErrorCount} error(s)")]
    public static partial void ConfigurationValidationFailed(ILogger logger, int errorCount);
}
