// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geoprocessing.LocalRunner;

namespace Honua.Geoprocessing.Testing;

public sealed class GpGoldenTestResult
{
    private GpGoldenTestResult(
        GoldenFixture fixture,
        GoldenTestOutcome outcome,
        LocalRunResult run,
        GoldenComparisonResult? comparison,
        ArtifactPayload? artifact)
    {
        Fixture = fixture;
        Outcome = outcome;
        Run = run;
        Comparison = comparison;
        Artifact = artifact;
    }

    public GoldenFixture Fixture { get; }

    public GoldenTestOutcome Outcome { get; }

    public LocalRunResult Run { get; }

    public GoldenComparisonResult? Comparison { get; }

    public ArtifactPayload? Artifact { get; }

    public bool Passed =>
        Outcome is GoldenTestOutcome.Matched or GoldenTestOutcome.Updated;

    public string Reason => Outcome switch
    {
        GoldenTestOutcome.Matched => "golden matched within tolerance",
        GoldenTestOutcome.Updated => $"golden regenerated ({Fixture.GoldenPath})",
        GoldenTestOutcome.Mismatch => Comparison?.Summary ?? "golden mismatch",
        GoldenTestOutcome.RunFailed => $"process run failed: {Run.ErrorMessage}",
        GoldenTestOutcome.NoArtifact => "process run produced no artifact to compare",
        GoldenTestOutcome.MissingGolden =>
            $"golden file not found: {Fixture.GoldenPath} (run with update mode to generate it)",
        _ => "unknown outcome",
    };

    public string FormatFailure(int maxDifferences = 20)
    {
        if (Passed)
        {
            return string.Empty;
        }

        var header = $"GP golden test '{Fixture.Id}' (process '{Fixture.ProcessId}') failed: {Reason}";
        if (Outcome == GoldenTestOutcome.Mismatch && Comparison is not null)
        {
            return header + Environment.NewLine + Comparison.Format(maxDifferences);
        }

        return header;
    }

    internal static GpGoldenTestResult Compared(
        GoldenFixture fixture,
        LocalRunResult run,
        ArtifactPayload artifact,
        GoldenComparisonResult comparison)
        => new(
            fixture,
            comparison.Matched ? GoldenTestOutcome.Matched : GoldenTestOutcome.Mismatch,
            run,
            comparison,
            artifact);

    internal static GpGoldenTestResult Updated(GoldenFixture fixture, LocalRunResult run, ArtifactPayload artifact)
        => new(fixture, GoldenTestOutcome.Updated, run, comparison: null, artifact);

    internal static GpGoldenTestResult RunFailed(GoldenFixture fixture, LocalRunResult run)
        => new(fixture, GoldenTestOutcome.RunFailed, run, comparison: null, artifact: null);

    internal static GpGoldenTestResult NoArtifact(GoldenFixture fixture, LocalRunResult run)
        => new(fixture, GoldenTestOutcome.NoArtifact, run, comparison: null, artifact: null);

    internal static GpGoldenTestResult MissingGolden(GoldenFixture fixture, LocalRunResult run)
        => new(fixture, GoldenTestOutcome.MissingGolden, run, comparison: null, artifact: null);
}

public enum GoldenTestOutcome
{
    Matched = 0,

    Mismatch = 1,

    Updated = 2,

    RunFailed = 3,

    NoArtifact = 4,

    MissingGolden = 5,
}
