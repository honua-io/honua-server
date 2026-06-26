// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// xUnit-friendly entry point for golden GP assertions (GP Devkit P6, issue #2127). A test
/// builds (or resolves from DI) the process executor set, then calls
/// <see cref="AssertGoldenAsync(IEnumerable{IProcessExecutor}, GoldenFixture, CancellationToken)"/>
/// to run a fixture and assert its artifact against the golden — throwing a
/// <see cref="GpGoldenAssertException"/> with a precise, located diff on mismatch.
///
/// <para>
/// UPDATE mode is guarded by the <c>HONUA_GP_UPDATE_GOLDENS</c> environment variable: when it
/// is set to a truthy value (<c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c>), the assertion instead
/// REGENERATES the golden from the produced artifact and passes. This keeps regeneration a
/// deliberate, opt-in action — a normal CI run can never silently overwrite a golden.
/// </para>
/// </summary>
public static class GpGoldenAssert
{
    /// <summary>
    /// The environment variable that switches the assertion into golden-regeneration mode.
    /// </summary>
    public const string UpdateEnvironmentVariable = "HONUA_GP_UPDATE_GOLDENS";

    /// <summary>
    /// Whether golden-update mode is currently enabled via <see cref="UpdateEnvironmentVariable"/>.
    /// </summary>
    public static bool UpdateModeEnabled => IsTruthy(Environment.GetEnvironmentVariable(UpdateEnvironmentVariable));

    /// <summary>
    /// Resolves the effective <see cref="GoldenUpdateMode"/> from the environment.
    /// </summary>
    public static GoldenUpdateMode ResolveUpdateMode() =>
        UpdateModeEnabled ? GoldenUpdateMode.Update : GoldenUpdateMode.Assert;

    /// <summary>
    /// Runs <paramref name="fixture"/> over the supplied executors and asserts the produced
    /// artifact matches the golden within tolerance (or regenerates it when update mode is on).
    /// </summary>
    /// <param name="executors">The process executor set to drive.</param>
    /// <param name="fixture">The golden test case.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The structured test result (always passing, or an exception is thrown).</returns>
    /// <exception cref="GpGoldenAssertException">The artifact diverged from the golden, or the run failed.</exception>
    public static Task<GpGoldenTestResult> AssertGoldenAsync(
        IEnumerable<IProcessExecutor> executors,
        GoldenFixture fixture,
        CancellationToken cancellationToken = default)
        => AssertGoldenAsync(new GpProcessTestRunner(executors), fixture, cancellationToken);

    /// <summary>
    /// Runs <paramref name="fixture"/> over an existing <see cref="GpProcessTestRunner"/> and
    /// asserts (or regenerates) the golden.
    /// </summary>
    /// <param name="runner">The golden test runner.</param>
    /// <param name="fixture">The golden test case.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The structured test result.</returns>
    /// <exception cref="GpGoldenAssertException">The artifact diverged from the golden, or the run failed.</exception>
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

/// <summary>
/// Thrown by <see cref="GpGoldenAssert"/> when a golden GP assertion fails. The message is
/// the located diff (what differed and where), so an xUnit failure points straight at the
/// offending coordinate/value.
/// </summary>
public sealed class GpGoldenAssertException : Exception
{
    /// <summary>Creates the exception with the formatted failure report.</summary>
    /// <param name="message">The located diff / failure report.</param>
    public GpGoldenAssertException(string message)
        : base(message)
    {
    }
}
