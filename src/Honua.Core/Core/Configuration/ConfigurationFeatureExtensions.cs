using Microsoft.Extensions.Configuration;

namespace Honua.Core.Configuration;

public static class ConfigurationFeatureExtensions
{
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

        foreach (var rawValue in candidateKeys.Select(key => configuration[key]))
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            if (bool.TryParse(rawValue, out var enabled))
            {
                return enabled;
            }

            var token = rawValue.Trim();
            if (token.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        return false;
    }
}
