// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel;
using Microsoft.Extensions.Configuration;

namespace Honua.Core.Configuration;

/// <summary>
/// Helpers for retrieving scalar configuration values with consistent fallback and error behavior.
/// </summary>
public static class ConfigurationValueExtensions
{
    /// <summary>
    /// Gets a required configuration value.
    /// </summary>
    /// <param name="configuration">The configuration source.</param>
    /// <param name="key">The configuration key.</param>
    /// <returns>The configured value.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the value is missing or empty.</exception>
    public static string GetRequiredValue(this IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var value = configuration.GetWithEnvOverride(key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var environmentKey = ToEnvironmentVariableKey(key);
        throw new InvalidOperationException(
            $"Required configuration value '{key}' was not found. Configure '{key}' or set the '{environmentKey}' environment variable.");
    }

    /// <summary>
    /// Gets a configuration value or returns the supplied default when the key is missing.
    /// </summary>
    /// <typeparam name="T">The target value type.</typeparam>
    /// <param name="configuration">The configuration source.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="defaultValue">The fallback value.</param>
    /// <returns>The converted configuration value, or <paramref name="defaultValue"/> when the key is missing.</returns>
    public static T GetValueOrDefault<T>(this IConfiguration configuration, string key, T defaultValue)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var value = configuration.GetWithEnvOverride(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return ConvertValue<T>(value, key);
    }

    /// <summary>
    /// Gets a configuration value, preferring a matching environment variable override when present.
    /// </summary>
    /// <param name="configuration">The configuration source.</param>
    /// <param name="key">The configuration key.</param>
    /// <returns>The resolved value, or <see langword="null"/> when the key is absent.</returns>
    public static string? GetWithEnvOverride(this IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var environmentValue = Environment.GetEnvironmentVariable(ToEnvironmentVariableKey(key));
        return environmentValue ?? configuration[key];
    }

    private static T ConvertValue<T>(string value, string key)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (targetType == typeof(string))
        {
            return (T)(object)value;
        }

        var converter = TypeDescriptor.GetConverter(targetType);
        if (!converter.CanConvertFrom(typeof(string)))
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' cannot be converted to {targetType.Name}.");
        }

        try
        {
            var converted = converter.ConvertFromInvariantString(value);
            if (converted is T typedValue)
            {
                return typedValue;
            }

            return (T)converted!;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' is not a valid {targetType.Name}: '{value}'.",
                ex);
        }
    }

    private static string ToEnvironmentVariableKey(string key) => key.Replace(":", "__", StringComparison.Ordinal);
}
