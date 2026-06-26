// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Geoprocessing.LocalRunner;

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// The golden-file GP test runner (GP Devkit P6, issue #2127). It wraps the headless,
/// Redis-free <see cref="GeoprocessingLocalRunner"/> (#2123): it runs a fixture's process
/// against its inputs in-process, decodes the single published artifact, and compares it to
/// the fixture's golden file with the right tolerance-aware comparator — geometry-coordinate
/// diff (NTS) for vector outputs, numeric/structural diff for raster/scalar outputs.
///
/// <para>
/// This is the rung of the GP lifecycle that makes every process FIRST-CLASS UNIT-TESTABLE
/// — a guarantee Esri geoprocessing fundamentally lacks. The same runner backs both the
/// xUnit assertion helper (<see cref="GpGoldenAssert"/>) and the <c>honua gp test</c> CLI
/// verb, and it runs fully offline with no Redis, job store, or control plane.
/// </para>
/// </summary>
public sealed class GpProcessTestRunner
{
    private readonly GeoprocessingLocalRunner _runner;

    /// <summary>
    /// Creates a runner over the supplied executor set — typically the same managed +
    /// native <see cref="IProcessExecutor"/> registrations the worker host uses, resolved
    /// from DI as <c>IEnumerable&lt;IProcessExecutor&gt;</c>.
    /// </summary>
    /// <param name="executors">The per-process executors the runner can invoke.</param>
    public GpProcessTestRunner(IEnumerable<IProcessExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _runner = new GeoprocessingLocalRunner(executors);
    }

    /// <summary>
    /// Creates a runner over an already-built <see cref="GeoprocessingLocalRunner"/>.
    /// </summary>
    /// <param name="runner">The headless runner to drive.</param>
    public GpProcessTestRunner(GeoprocessingLocalRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>The process ids this runner can invoke, ordinally sorted.</summary>
    public IReadOnlyList<string> AvailableProcessIds => _runner.AvailableProcessIds;

    /// <summary>
    /// Runs a fixture and evaluates it against its golden file.
    /// </summary>
    /// <param name="fixture">The golden test case.</param>
    /// <param name="updateMode">
    /// In <see cref="GoldenUpdateMode.Update"/> the produced artifact is WRITTEN to the
    /// golden path (regenerating it) and the comparison is reported as matched; in
    /// <see cref="GoldenUpdateMode.Assert"/> (the default) the golden is read and compared.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The structured outcome (run status + golden comparison).</returns>
    public async Task<GpGoldenTestResult> RunAsync(
        GoldenFixture fixture,
        GoldenUpdateMode updateMode = GoldenUpdateMode.Assert,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var run = await _runner.RunAsync(fixture.ProcessId, fixture.Inputs, cancellationToken).ConfigureAwait(false);

        if (!run.Succeeded)
        {
            return GpGoldenTestResult.RunFailed(fixture, run);
        }

        if (run.Artifacts.Count == 0)
        {
            return GpGoldenTestResult.NoArtifact(fixture, run);
        }

        var payload = ArtifactPayload.Decode(run.Artifacts[0]);
        var actualText = payload.AsText();

        if (updateMode == GoldenUpdateMode.Update)
        {
            var directory = Path.GetDirectoryName(fixture.GoldenPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fixture.GoldenPath, actualText, cancellationToken).ConfigureAwait(false);
            return GpGoldenTestResult.Updated(fixture, run, payload);
        }

        if (!File.Exists(fixture.GoldenPath))
        {
            return GpGoldenTestResult.MissingGolden(fixture, run);
        }

        var goldenText = await File.ReadAllTextAsync(fixture.GoldenPath, cancellationToken).ConfigureAwait(false);
        var comparison = Compare(fixture, payload, actualText, goldenText);
        return GpGoldenTestResult.Compared(fixture, run, payload, comparison);
    }

    private static GoldenComparisonResult Compare(
        GoldenFixture fixture,
        ArtifactPayload payload,
        string actualText,
        string goldenText)
    {
        var mode = fixture.Mode;
        if (mode == GoldenComparisonMode.Auto)
        {
            mode = payload.IsGeoJson ? GoldenComparisonMode.Geometry : GoldenComparisonMode.ScalarStructural;
        }

        return mode == GoldenComparisonMode.Geometry
            ? GeometryGoldenComparer.Compare(actualText, goldenText, fixture.Tolerance)
            : ScalarStructuralGoldenComparer.Compare(actualText, goldenText, fixture.Tolerance);
    }
}
