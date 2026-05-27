// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Configuration;
using Honua.Server.Features.Infrastructure.Helpers;

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
    /// <param name="isDevelopment">Whether running in the development environment.</param>
    /// <param name="isTest">Whether running in the test environment.</param>
    /// <param name="allowMissingDatabase">Whether to allow a missing database connection string (e.g. when migrations are skipped).</param>
    /// <returns>List of validation errors, empty if configuration is valid.</returns>
    public static List<string> ValidateConfiguration(
        IConfiguration configuration,
        ILogger logger,
        bool isDevelopment,
        bool isTest = false,
        bool allowMissingDatabase = false)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var isRelaxedEnvironment = isDevelopment || isTest;

        // Check database connection (required in production unless explicitly allowed)
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrEmpty(connectionString) && !isRelaxedEnvironment && !allowMissingDatabase)
        {
            errors.Add("ConnectionStrings__DefaultConnection is required. Set this environment variable to your PostgreSQL connection string.");
        }

        var deploymentMode = ResolveDeploymentMode(configuration, errors);
        ValidateDeploymentDependencies(configuration, deploymentMode, errors, warnings);

        // Check feature flags and log their status
        LogFeatureStatus(configuration, logger, "HONUA_OBSERVABILITY", "Observability");
        LogFeatureStatus(configuration, logger, "HONUA_OPENTELEMETRY", "OpenTelemetry tracing");
        LogFeatureStatus(configuration, logger, "HONUA_SKIP_MIGRATIONS", "Skip migrations");

        // Warn about dev-only settings in production
        var basicAuthCompatibilityEnabled = configuration.GetValue(
            "Authentication:BasicCompatibility:Enabled",
            configuration.GetValue("HONUA_ENABLE_BASIC_AUTH_COMPAT", false));
        var basicAuthRequireHttps = configuration.GetValue(
            "Authentication:BasicCompatibility:RequireHttps",
            configuration.GetValue("HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH", true));

        if (!isRelaxedEnvironment)
        {
            if (configuration.IsFeatureEnabled("DEV_AUTH"))
            {
                errors.Add("HONUA_DEV_AUTH is enabled. This should only be used in development environments.");
            }

            var adminPassword = configuration["HONUA_ADMIN_PASSWORD"];
            if (string.IsNullOrEmpty(adminPassword))
            {
                errors.Add("HONUA_ADMIN_PASSWORD is required in non-development environments.");
            }
            else
            {
                ValidateAdminPassword(adminPassword, errors);
            }

            if (basicAuthCompatibilityEnabled && !basicAuthRequireHttps)
            {
                errors.Add("HTTP Basic authentication compatibility requires HTTPS in non-development environments. Set HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH=true.");
            }

            if (basicAuthCompatibilityEnabled)
            {
                warnings.Add("HTTP Basic authentication compatibility mode is enabled. Prefer X-API-Key or OIDC bearer tokens for long-term clients.");
            }
        }
        else
        {
            if (isDevelopment && configuration.IsFeatureEnabled("DEV_AUTH"))
            {
                errors.Add("HONUA_DEV_AUTH is no longer permitted outside the test environment. Configure HONUA_ADMIN_PASSWORD for development access instead.");
            }

            var adminPassword = configuration["HONUA_ADMIN_PASSWORD"];
            if (string.IsNullOrEmpty(adminPassword))
            {
                warnings.Add(isTest
                    ? "Test mode with no HONUA_ADMIN_PASSWORD configured. Both HONUA_DEV_AUTH=true AND HONUA_DEV_AUTH_ALLOW_BYPASS=true are required to bypass admin authentication during tests."
                    : "Development mode with no HONUA_ADMIN_PASSWORD configured. Admin endpoints require explicit credentials and will remain inaccessible until HONUA_ADMIN_PASSWORD is set.");
            }

            if (basicAuthCompatibilityEnabled && !basicAuthRequireHttps)
            {
                warnings.Add("HTTP Basic authentication compatibility is enabled without HTTPS enforcement for development use.");
            }
        }

        ValidateHostValidationConfiguration(configuration, errors, warnings, isRelaxedEnvironment);

        ValidateConnectionEncryptionConfiguration(configuration, errors, warnings, isRelaxedEnvironment);

        // Log configuration summary
        LogConfigurationSummary(configuration, logger);

        ValidateSecretReferences(configuration, errors, warnings, isRelaxedEnvironment);

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

    private static void ValidateSecretReferences(
        IConfiguration configuration,
        List<string> errors,
        List<string> warnings,
        bool isDevelopment)
    {
        foreach (var (path, allowedPrefixes) in _secretValidationRules)
        {
            var value = configuration[path];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (IsExternalConfigurationValue(configuration, path, value))
            {
                continue;
            }

            if (IsSecretReference(value, allowedPrefixes))
            {
                continue;
            }

            var prefixes = string.Join(", ", allowedPrefixes.OrderBy(prefix => prefix).Select(prefix => $"{prefix}:"));
            var message = $"Configuration value '{path}' should be supplied via a secret reference (supported prefixes: {prefixes}) " +
                          "or environment variable instead of plain text.";

            if (isDevelopment)
            {
                warnings.Add(message);
            }
            else
            {
                errors.Add(message);
            }
        }
    }

    private static DeploymentMode ResolveDeploymentMode(IConfiguration configuration, List<string> errors)
    {
        var rawValue = configuration[$"{DeploymentOptions.SectionName}:{nameof(DeploymentOptions.Mode)}"];
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return DeploymentMode.SingleInstance;
        }

        if (Enum.TryParse(rawValue, true, out DeploymentMode mode))
        {
            return mode;
        }

        errors.Add($"Deployment:Mode value '{rawValue}' is invalid. Allowed values: {string.Join(", ", Enum.GetNames<DeploymentMode>())}.");
        return DeploymentMode.SingleInstance;
    }

    private static void ValidateDeploymentDependencies(
        IConfiguration configuration,
        DeploymentMode deploymentMode,
        List<string> errors,
        List<string> warnings)
    {
        if (deploymentMode != DeploymentMode.MultiNode)
        {
            return;
        }

        var redisConnection = configuration.GetConnectionString("redis")
            ?? configuration["Aspire:StackExchange:Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            errors.Add("Multi-node deployment requires ConnectionStrings__redis for shared cache and output cache.");
        }

        var cacheFallbackEnabled = configuration.GetValue<bool?>("Cache:EnableFallback") ?? true;
        if (cacheFallbackEnabled)
        {
            warnings.Add("Cache:EnableFallback is enabled in multi-node mode. This can cause per-node cache divergence.");
        }

        var providerName = configuration.GetValue<string>("FileStorage:Provider")
            ?? Environment.GetEnvironmentVariable("HONUA_STORAGE_PROVIDER");
        var provider = ResolveStorageProvider(providerName, errors);

        if (provider == StorageProvider.Local)
        {
            errors.Add("Multi-node deployment requires a shared cloud file storage provider. Set FileStorage:Provider to AwsS3 or AzureBlob.");
        }

        ValidateFileStorageConfiguration(configuration, provider, errors);
    }

    private static StorageProvider ResolveStorageProvider(string? providerName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return StorageProvider.Local;
        }

        if (Enum.TryParse(providerName, true, out StorageProvider provider))
        {
            return provider;
        }

        errors.Add($"FileStorage:Provider value '{providerName}' is invalid. Allowed values: {string.Join(", ", Enum.GetNames<StorageProvider>())}.");
        return StorageProvider.Local;
    }

    private static void ValidateFileStorageConfiguration(
        IConfiguration configuration,
        StorageProvider provider,
        List<string> errors)
    {
        switch (provider)
        {
            case StorageProvider.AwsS3:
                RequireSetting(configuration, "FileStorage:AwsS3:BucketName", errors);
                RequireSetting(configuration, "FileStorage:AwsS3:Region", errors);
                break;
            case StorageProvider.AzureBlob:
                RequireSetting(configuration, "FileStorage:AzureBlob:ConnectionString", errors);
                RequireSetting(configuration, "FileStorage:AzureBlob:ContainerName", errors);
                break;
            case StorageProvider.Local:
            default:
                break;
        }
    }

    private static void RequireSetting(IConfiguration configuration, string key, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(configuration[key]))
        {
            errors.Add($"'{key.Replace(":", "__", StringComparison.Ordinal)}' is required for the selected FileStorage provider.");
        }
    }

    private static bool IsExternalConfigurationValue(IConfiguration configuration, string path, string expectedValue)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return false;
        }

        // Walk providers from highest precedence to lowest and inspect the provider
        // that currently supplies the effective value.
        var providers = root.Providers.ToArray();
        for (var index = providers.Length - 1; index >= 0; index--)
        {
            var provider = providers[index];
            if (!provider.TryGet(path, out var providerValue) ||
                string.IsNullOrWhiteSpace(providerValue))
            {
                continue;
            }

            if (!string.Equals(providerValue, expectedValue, StringComparison.Ordinal))
            {
                continue;
            }

            return provider is not FileConfigurationProvider;
        }

        return false;
    }

    private static bool IsSecretReference(string value, FrozenSet<string> allowedPrefixes)
    {
        if (SecretReferenceResolver.IsEnvironmentReference(value))
        {
            return true;
        }

        var colonIndex = value.IndexOf(':');
        if (colonIndex <= 0)
        {
            return false;
        }

        var prefix = value[..colonIndex];
        return allowedPrefixes.Contains(prefix);
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

    private static void ValidateAdminPassword(string password, List<string> errors)
    {
        if (_knownPlaceholderPasswords.Contains(password))
        {
            errors.Add("HONUA_ADMIN_PASSWORD is set to a known placeholder value. Please set a strong, unique password before deploying to production.");
        }

        if (password.Length < 12)
        {
            errors.Add("HONUA_ADMIN_PASSWORD must be at least 12 characters long in production.");
        }
    }

    private static void ValidateConnectionEncryptionConfiguration(
        IConfiguration configuration,
        List<string> errors,
        List<string> warnings,
        bool isDevelopment)
    {
        var masterKey = configuration["Security:ConnectionEncryption:MasterKey"];
        if (string.IsNullOrWhiteSpace(masterKey))
        {
            if (isDevelopment)
            {
                warnings.Add("Security:ConnectionEncryption:MasterKey is not configured. Secure connection encryption operations will fail until this value is set.");
            }
            else
            {
                errors.Add("Security__ConnectionEncryption__MasterKey is required in non-development environments.");
            }

            return;
        }

        if (masterKey.Length < 32)
        {
            const string message = "Security:ConnectionEncryption:MasterKey must be at least 32 characters long.";
            if (isDevelopment)
            {
                warnings.Add(message);
            }
            else
            {
                errors.Add(message);
            }
        }

        var salt = configuration["Security:ConnectionEncryption:Salt"];
        if (string.IsNullOrWhiteSpace(salt))
        {
            return;
        }

        try
        {
            _ = Convert.FromBase64String(salt);
        }
        catch (FormatException)
        {
            const string message = "Security:ConnectionEncryption:Salt must be a valid base64 string when configured.";
            if (isDevelopment)
            {
                warnings.Add(message);
            }
            else
            {
                errors.Add(message);
            }
        }
    }

    private static void ValidateHostValidationConfiguration(
        IConfiguration configuration,
        List<string> errors,
        List<string> warnings,
        bool isDevelopment)
    {
        var hostValidationEnabled = configuration.GetValue<bool?>("HostValidation:Enabled") ?? !isDevelopment;
        if (!hostValidationEnabled)
        {
            return;
        }

        if (HasConfiguredHostAllowlist(configuration) || HasValidPublicBaseUrl(configuration))
        {
            return;
        }

        const string message =
            "Host validation is enabled, but no explicit host allowlist is configured. Set HostValidation__AllowedHosts (or AllowedHosts) or PUBLIC_BASE_URL to trusted hosts.";

        var requireExplicitHosts = configuration.GetValue<bool>("HostValidation:RequireExplicitHosts");
        if (!isDevelopment && requireExplicitHosts)
        {
            errors.Add(message);
            return;
        }

        if (isDevelopment)
        {
            warnings.Add(message);
        }
        else
        {
            warnings.Add($"{message} Set HostValidation__RequireExplicitHosts=true to enforce this as a startup error.");
        }
    }

    private static bool HasConfiguredHostAllowlist(IConfiguration configuration)
    {
        var configuredHosts = new List<string>();

        var hostValidationAllowedHosts = configuration.GetSection("HostValidation:AllowedHosts").Get<string[]>();
        if (hostValidationAllowedHosts != null)
        {
            configuredHosts.AddRange(hostValidationAllowedHosts);
        }

        var allowedHostsRaw = configuration["AllowedHosts"];
        if (!string.IsNullOrWhiteSpace(allowedHostsRaw))
        {
            configuredHosts.AddRange(
                allowedHostsRaw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return configuredHosts.Any(IsExplicitAllowedHost);
    }

    private static bool HasValidPublicBaseUrl(IConfiguration configuration)
    {
        var value = configuration["Public:BaseUrl"] ?? configuration["PUBLIC_BASE_URL"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
               (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExplicitAllowedHost(string? hostEntry)
    {
        if (string.IsNullOrWhiteSpace(hostEntry))
        {
            return false;
        }

        return !string.Equals(hostEntry.Trim(), "*", StringComparison.Ordinal);
    }

    private static readonly FrozenSet<string> _knownPlaceholderPasswords = new[]
        {
            "CHANGE_ME_BEFORE_USE",
            "changeme",
            "password",
            "admin",
            "secret"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private enum StorageProvider
    {
        Local,
        AwsS3,
        AzureBlob
    }

    private static readonly FrozenSet<string> _envOnlyPrefixes = new[]
        {
            "env"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> _connectionSecretPrefixes = new[]
        {
            "env",
            "aws",
            "azure"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, FrozenSet<string>> _secretValidationRules =
        new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            ["HONUA_ADMIN_PASSWORD"] = _connectionSecretPrefixes,
            ["Security:ConnectionEncryption:MasterKey"] = _connectionSecretPrefixes,
            ["ConnectionStrings:DefaultConnection"] = _connectionSecretPrefixes,
            ["ConnectionStrings:redis"] = _envOnlyPrefixes,
            ["Oidc:AzureAd:ClientSecret"] = _envOnlyPrefixes,
            ["Oidc:Google:ClientSecret"] = _envOnlyPrefixes,
            ["Oidc:Generic:ClientSecret"] = _envOnlyPrefixes,
            ["FileStorage:AwsS3:AccessKeyId"] = _envOnlyPrefixes,
            ["FileStorage:AwsS3:SecretAccessKey"] = _envOnlyPrefixes,
            ["FileStorage:AzureBlob:ConnectionString"] = _envOnlyPrefixes,
            ["Monitoring:IntelligentAlerting:NotificationChannels:Email:Password"] = _envOnlyPrefixes,
            ["Monitoring:IntelligentAlerting:NotificationChannels:Slack:WebhookUrl"] = _envOnlyPrefixes,
            ["Monitoring:IntelligentAlerting:NotificationChannels:Webhook:Url"] = _envOnlyPrefixes,
            ["Monitoring:IntelligentAlerting:NotificationChannels:Webhook:Headers:Authorization"] = _envOnlyPrefixes,
            ["Monitoring:IntelligentAlerting:NotificationChannels:Sms:ApiKey"] = _envOnlyPrefixes
        }
        .ToFrozenDictionary(StringComparer.Ordinal);
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
    /// Log configuration error with exception.
    /// </summary>
    [LoggerMessage(
        EventId = 4017,
        Level = LogLevel.Error,
        Message = "Configuration error: {Message}")]
    public static partial void ConfigurationError(ILogger logger, string message, Exception exception);

    /// <summary>
    /// Log successful configuration validation.
    /// </summary>
    [LoggerMessage(
        EventId = 4014,
        Level = LogLevel.Information,
        Message = "Configuration validation: {Message}")]
    public static partial void ConfigurationValidated(ILogger logger, string message);

    /// <summary>
    /// Log configuration validation success.
    /// </summary>
    [LoggerMessage(
        EventId = 4015,
        Level = LogLevel.Information,
        Message = "All configuration options validated successfully")]
    public static partial void ConfigurationValidationSucceeded(ILogger logger);

    /// <summary>
    /// Log configuration validation failure.
    /// </summary>
    [LoggerMessage(
        EventId = 4016,
        Level = LogLevel.Error,
        Message = "Configuration validation failed with {ErrorCount} errors: {ErrorDetails}")]
    public static partial void ConfigurationValidationFailed(ILogger logger, int errorCount, string errorDetails);
}
