// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.Geoprocessing.Testing;

// Golden files are rewritten only when HONUA_GP_UPDATE_GOLDENS is set, so a normal run cannot overwrite them.
public static class GpGoldenAssert
{
    public const string UpdateEnvironmentVariable = "HONUA_GP_UPDATE_GOLDENS";

    public static bool UpdateModeEnabled => IsTruthy(Environment.GetEnvironmentVariable(UpdateEnvironmentVariable));

    public static GoldenUpdateMode ResolveUpdateMode() =>
        UpdateModeEnabled ? GoldenUpdateMode.Update : GoldenUpdateMode.Assert;

    public static Task<GpGoldenTestResult> AssertGoldenAsync(
        IEnumerable<IProcessExecutor> executors,
        GoldenFixture fixture,
        CancellationToken cancellationToken = default)
        => AssertGoldenAsync(new GpProcessTestRunner(executors), fixture, cancellationToken);

    public static async Task<GpGoldenTestResult> AssertGoldenAsync(
        GpProcessTestRunner runner,
        GoldenFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(fixture);

        var result = await runner.RunAsync(fixture, ResolveUpdateMode(), cancellationToken).ConfigureAwait(false);

        if (!result.Passed)
        {
            throw new GpGoldenAssertException(result.FormatFailure());
        }

        return result;
    }

    private static bool IsTruthy(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));
}

public sealed class GpGoldenAssertException : Exception
{
    public GpGoldenAssertException(string message)
        : base(message)
    {
    }
}
