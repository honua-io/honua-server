// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geoprocessing.LocalRunner;

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// The outcome of running one golden GP test (GP Devkit P6, issue #2127): the process run
/// itself, the golden comparison (when the run produced an artifact), and a classified
/// terminal <see cref="Outcome"/>. <see cref="Passed"/> folds run success + golden match (or
/// a successful update) into a single boolean for the assertion helper and the CLI.
/// </summary>
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

    /// <summary>The fixture that was run.</summary>
    public GoldenFixture Fixture { get; }

    /// <summary>The classified terminal outcome.</summary>
    public GoldenTestOutcome Outcome { get; }

    /// <summary>The underlying headless run result.</summary>
    public LocalRunResult Run { get; }

    /// <summary>The golden comparison, or <c>null</c> when the run failed or was updated.</summary>
    public GoldenComparisonResult? Comparison { get; }

    /// <summary>The decoded artifact payload, or <c>null</c> when no artifact was produced.</summary>
    public ArtifactPayload? Artifact { get; }

    /// <summary>Whether the test passed: the golden matched, or the golden was regenerated.</summary>
    public bool Passed =>
        Outcome is GoldenTestOutcome.Matched or GoldenTestOutcome.Updated;

    /// <summary>
    /// A one-line, human-readable reason for the outcome (run error, comparison summary, or
    /// a success note) suitable for CLI output and assertion messages.
    /// </summary>
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

    /// <summary>
    /// Renders a full multi-line failure report — the headline reason plus the located
    /// comparison differences — for an assertion message. Empty when <see cref="Passed"/>.
    /// </summary>
    /// <param name="maxDifferences">Maximum difference lines to render.</param>
    /// <returns>The formatted report, or an empty string on pass.</returns>
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

/// <summary>
/// The classified terminal outcome of a golden GP test (GP Devkit P6, issue #2127).
/// </summary>
public enum GoldenTestOutcome
{
    /// <summary>The run succeeded and its artifact matched the golden within tolerance.</summary>
    Matched = 0,

    /// <summary>The run succeeded but its artifact diverged from the golden beyond tolerance.</summary>
    Mismatch = 1,

    /// <summary>The golden was regenerated from the produced artifact (update mode).</summary>
    Updated = 2,

    /// <summary>The process run itself failed before producing an artifact.</summary>
    RunFailed = 3,

    /// <summary>The run succeeded but published no artifact to compare.</summary>
    NoArtifact = 4,

    /// <summary>No golden file exists yet for the fixture (and update mode was off).</summary>
    MissingGolden = 5,
}
