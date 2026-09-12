// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.LoadTests;

/// <summary>
/// Verifies duration arguments match the candidate producer's seconds contract.
/// </summary>
public sealed class LoadDurationOptionsTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e100")]
    public void TryParse_InvalidNumericDuration_ReturnsAnError(string value)
    {
        Assert.False(LoadTestOptions.TryParse(["--duration", value], out _, out var error));
        Assert.Contains("Invalid duration", error, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("300", 300)]
    [InlineData("3600", 3600)]
    [InlineData("1.5", 1.5)]
    [InlineData("0", 0)]
    [InlineData("300s", 300)]
    [InlineData("5m", 300)]
    [InlineData("00:05:00", 300)]
    [InlineData("1d", 86400)]
    public void TryParse_Durations_UseSecondsForBareNumbers(string value, double seconds)
    {
        // The capacity producer exports RAMP_UP=300; the existing shell runner
        // forwards that value verbatim as --ramp-up 300, without a unit suffix.
        Assert.True(LoadTestOptions.TryParse(
            ["--ramp-up", value, "--duration", value, "--ramp-down", value],
            out var options, out var error), error);
        var expected = TimeSpan.FromSeconds(seconds);
        Assert.Equal(expected, options.RampUp);
        Assert.Equal(expected, options.Duration);
        Assert.Equal(expected, options.RampDown);
    }
}
