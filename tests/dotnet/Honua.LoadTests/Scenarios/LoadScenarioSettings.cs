// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.LoadTests.Scenarios;

/// <summary>
/// Shared configuration for the in-process load scenarios under
/// <see cref="Honua.LoadTests.Scenarios"/>. Each scenario reads its target
/// base URL, request rate (RPS), and duration from environment variables so
/// that PR CI can keep the scenario classes compiled and discoverable while
/// the actual run only happens on demand (nightly workflow or manual).
/// </summary>
/// <remarks>
/// <para>
/// Environment variables:
/// <list type="bullet">
/// <item><c>HONUA_LOAD_TARGET</c> — base URL (default <c>http://localhost:8080</c>).</item>
/// <item><c>HONUA_LOAD_DURATION_SECONDS</c> — steady-state duration in seconds (default <c>60</c>).</item>
/// <item><c>HONUA_LOAD_RPS_MULTIPLIER</c> — multiplier (double) applied to each scenario's
/// nominal rate so a single tuning knob can scale all three (default <c>1.0</c>).</item>
/// </list>
/// </para>
/// </remarks>
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
