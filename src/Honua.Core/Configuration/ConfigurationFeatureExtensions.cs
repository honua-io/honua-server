using Microsoft.Extensions.Configuration;

namespace Honua.Core.Configuration;

/// <summary>
/// Feature-flag helpers for mixed environment-variable and section-based configuration.
/// </summary>
public static class ConfigurationFeatureExtensions
{
    /// <summary>
    /// Determines whether a feature flag is enabled.
    /// </summary>
    /// <param name="configuration">The configuration source.</param>
    /// <param name="featureName">The feature name with or without the <c>HONUA_</c> prefix.</param>
    /// <returns><see langword="true"/> when the feature is enabled; otherwise <see langword="false"/>.</returns>
    public static bool IsFeatureEnabled(this IConfiguration configuration, string featureName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);

        var normalized = featureName.StartsWith("HONUA_", StringComparison.OrdinalIgnoreCase)
            ? featureName["HONUA_".Length..]
            : featureName;

        var candidateKeys = new[]
        {
            $"HONUA_{normalized}",
            normalized,
            $"Features:{normalized}"
        };

        foreach (var key in candidateKeys)
        {
            var rawValue = configuration[key];
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            if (bool.TryParse(rawValue, out var enabled))
            {
                return enabled;
            }

            return true;
        }

        return false;
    }
}
