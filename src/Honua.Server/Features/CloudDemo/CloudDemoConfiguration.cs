// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.CloudDemo;

internal static class CloudDemoConfiguration
{
    public const string SectionName = "CloudDemoServices";
    public const string ResetTokenHeader = "X-Honua-Demo-Reset-Token";
    public const string WriteTokenHeader = "X-Honua-Demo-Write-Token";

    public static bool IsEnabled(IConfiguration configuration)
        => GetBoolean(configuration, "Enabled", "HONUA_CLOUD_DEMO_ENABLED");

    public static bool AllowWrites(IConfiguration configuration)
        => GetString(configuration, "AllowWrites", "HONUA_CLOUD_DEMO_ALLOW_WRITES")
            is { } value &&
           string.Equals(value, "true", StringComparison.Ordinal);

    public static bool SeedOnStartup(IConfiguration configuration)
        => GetBoolean(configuration, "SeedOnStartup", "HONUA_CLOUD_DEMO_SEED_ON_STARTUP");

    public static string? ResetToken(IConfiguration configuration)
        => GetString(configuration, "ResetToken", "HONUA_CLOUD_DEMO_RESET_TOKEN");

    public static string? WriteToken(IConfiguration configuration)
        => GetString(configuration, "WriteToken", "HONUA_CLOUD_DEMO_WRITE_TOKEN");

    private static bool GetBoolean(IConfiguration configuration, string key, string envName)
        => GetString(configuration, key, envName) is { } value &&
           bool.TryParse(value, out var parsed) &&
           parsed;

    private static string? GetString(IConfiguration configuration, string key, string envName)
    {
        var sectionValue = configuration[$"{SectionName}:{key}"];
        if (!string.IsNullOrWhiteSpace(sectionValue))
        {
            return sectionValue;
        }

        var envValue = configuration[envName];
        return string.IsNullOrWhiteSpace(envValue) ? null : envValue;
    }
}
