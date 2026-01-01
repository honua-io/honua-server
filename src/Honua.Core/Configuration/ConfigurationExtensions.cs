// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Honua.Core.Configuration;

/// <summary>
/// Extension methods for environment-first configuration access.
/// Provides helper methods for checking feature flags and retrieving required values
/// with clear error messages for container deployments.
/// </summary>
public static class ConfigurationExtensions
{
    /// <summary>
    /// Checks if a feature is enabled via environment variable.
    /// Features use the HONUA_* prefix pattern.
    /// </summary>
    /// <param name="configuration">The configuration instance.</param>
    /// <param name="featureName">The feature name (e.g., "ADMIN_UI", "OBSERVABILITY").</param>
    /// <returns>True if the feature is enabled; false otherwise.</returns>
    /// <example>
    /// <code>
    /// // Check if admin UI is enabled via HONUA_ADMIN_UI=true
    /// if (configuration.IsFeatureEnabled("ADMIN_UI"))
    /// {
    ///     // Enable admin UI features
    /// }
    /// </code>
    /// </example>
    public static bool IsFeatureEnabled(this IConfiguration configuration, string featureName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(featureName))
        {
            throw new ArgumentException("Feature name is required.", nameof(featureName));
        }

        var envVarName = $"HONUA_{featureName.ToUpperInvariant()}";
        var value = configuration[envVarName];

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Support common boolean representations
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a required configuration value with a clear error message if missing.
    /// </summary>
    /// <param name="configuration">The configuration instance.</param>
    /// <param name="key">The configuration key.</param>
    /// <returns>The configuration value.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the required value is not configured.</exception>
    /// <example>
    /// <code>
    /// var connectionString = configuration.GetRequiredValue("ConnectionStrings:DefaultConnection");
    /// </code>
    /// </example>
    public static string GetRequiredValue(this IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Configuration key is required.", nameof(key));
        }

        var value = configuration[key];

        if (string.IsNullOrEmpty(value))
        {
            var envVarName = key.Replace(":", "__").Replace(".", "__");
            throw new InvalidOperationException(
                $"Required configuration '{key}' is not set. " +
                $"Set the environment variable '{envVarName}' or add it to appsettings.json.");
        }

        return value;
    }

    /// <summary>
    /// Gets a configuration value or returns a default if not set.
    /// </summary>
    /// <typeparam name="T">The type to convert the value to.</typeparam>
    /// <param name="configuration">The configuration instance.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="defaultValue">The default value if not configured.</param>
    /// <returns>The configuration value or the default.</returns>
    public static T GetValueOrDefault<T>(this IConfiguration configuration, string key, T defaultValue)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Configuration key is required.", nameof(key));
        }

        var rawValue = configuration[key];
        if (string.IsNullOrEmpty(rawValue))
        {
            return defaultValue;
        }

        var targetType = typeof(T);

        if (targetType == typeof(string))
        {
            return (T)(object)rawValue;
        }

        if (targetType == typeof(int) &&
            int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            return (T)(object)intValue;
        }

        if (targetType == typeof(long) &&
            long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return (T)(object)longValue;
        }

        if (targetType == typeof(double) &&
            double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return (T)(object)doubleValue;
        }

        if (targetType == typeof(bool) && TryParseBoolean(rawValue, out var boolValue))
        {
            return (T)(object)boolValue;
        }

        if (targetType == typeof(TimeSpan) &&
            TimeSpan.TryParse(rawValue, CultureInfo.InvariantCulture, out var timeSpanValue))
        {
            return (T)(object)timeSpanValue;
        }

        if (targetType.IsEnum && Enum.TryParse(targetType, rawValue, true, out var enumValue))
        {
            return (T)enumValue;
        }

        return defaultValue;
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
        {
            return true;
        }

        if (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    /// <summary>
    /// Gets a configuration value from either the standard key or an environment variable override.
    /// Environment variables take precedence over appsettings.json values.
    /// </summary>
    /// <param name="configuration">The configuration instance.</param>
    /// <param name="key">The configuration key (e.g., "Cache:Enabled").</param>
    /// <param name="envVarName">Optional environment variable name override.</param>
    /// <returns>The configuration value or null if not set.</returns>
    public static string? GetWithEnvOverride(this IConfiguration configuration, string key, string? envVarName = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // If explicit env var name provided, check it first
        if (!string.IsNullOrEmpty(envVarName))
        {
            var envValue = Environment.GetEnvironmentVariable(envVarName);
            if (!string.IsNullOrEmpty(envValue))
            {
                return envValue;
            }
        }

        // Fall back to standard configuration (which already handles env vars via __ separator)
        return configuration[key];
    }
}
