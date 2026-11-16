// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.LoadTests.Scenarios;

internal static class LoadScenarioSettings
{
    public const string TargetEnvVar = "HONUA_LOAD_TARGET";
    public const string DurationEnvVar = "HONUA_LOAD_DURATION_SECONDS";
    public const string RpsMultiplierEnvVar = "HONUA_LOAD_RPS_MULTIPLIER";

    public const string DefaultBaseUrl = "http://localhost:8080";
    public const int DefaultDurationSeconds = 60;

    public static string GetBaseUrl()
    {
        var value = Environment.GetEnvironmentVariable(TargetEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultBaseUrl;
        }

        return value.TrimEnd('/');
    }

    public static TimeSpan GetDuration()
    {
        var raw = Environment.GetEnvironmentVariable(DurationEnvVar);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(DefaultDurationSeconds);
    }

    public static int GetRate(int nominalRate)
    {
        if (nominalRate <= 0)
        {
            return 1;
        }

        var raw = Environment.GetEnvironmentVariable(RpsMultiplierEnvVar);
        if (string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier)
            || multiplier <= 0d)
        {
            return nominalRate;
        }

        var scaled = (int)Math.Round(nominalRate * multiplier, MidpointRounding.AwayFromZero);
        return scaled > 0 ? scaled : 1;
    }
}
