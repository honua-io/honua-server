// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Geoprocessing.LocalRunner;

namespace Honua.Geoprocessing.Testing;

public sealed class GpProcessTestRunner
{
    private readonly GeoprocessingLocalRunner _runner;

    public GpProcessTestRunner(IEnumerable<IProcessExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _runner = new GeoprocessingLocalRunner(executors);
    }

    public GpProcessTestRunner(GeoprocessingLocalRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public IReadOnlyList<string> AvailableProcessIds => _runner.AvailableProcessIds;

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
