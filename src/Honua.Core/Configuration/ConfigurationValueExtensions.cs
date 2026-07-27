// Licensed under the Elastic License 2.0. See LICENSE in the project root.
using System.Globalization;
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
        var targetType = GetTargetType<T>();
        if (targetType == typeof(string))
        {
            return (T)(object)value;
        }

        try
        {
            var converted = targetType.IsEnum
                ? Enum.Parse(targetType, value, ignoreCase: true)
                : Type.GetTypeCode(targetType) switch
                {
                    TypeCode.Boolean => bool.Parse(value),
                    TypeCode.Byte => byte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    TypeCode.Int16 => short.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    TypeCode.Int32 => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    TypeCode.Int64 => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    TypeCode.Single => float.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
                    TypeCode.Double => double.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
                    TypeCode.Decimal => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture),
                    TypeCode.DateTime => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    _ when targetType == typeof(Guid) => Guid.Parse(value),
                    _ when targetType == typeof(TimeSpan) => TimeSpan.Parse(value, CultureInfo.InvariantCulture),
                    _ when targetType == typeof(DateTimeOffset) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    _ when targetType == typeof(Uri) => new Uri(value, UriKind.RelativeOrAbsolute),
                    _ => throw new InvalidOperationException(
                        $"Configuration value '{key}' cannot be converted to {targetType.Name}.")
                };

            return (T)converted;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or UriFormatException)
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' is not a valid {targetType.Name}: '{value}'.",
                ex);
        }
    }

    private static Type GetTargetType<T>() => Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

    private static string ToEnvironmentVariableKey(string key) => key.Replace(":", "__", StringComparison.Ordinal);
}
