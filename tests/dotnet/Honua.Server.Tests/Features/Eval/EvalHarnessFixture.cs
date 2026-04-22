// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.TestKit;
using Honua.TestKit.Eval;
using Xunit;

namespace Honua.Server.Tests.Features.Eval;

/// <summary>
/// Class-scoped fixture that owns the shared <see cref="WebAppFixture"/> for the eval
/// harness run and aggregates per-scenario results into the versioned
/// <see cref="EvalReport"/> consumed by honua-devops-31.
/// </summary>
public sealed class EvalHarnessFixture : IAsyncLifetime
{
    private const string ReportFileName = "eval-report.json";

    private readonly List<EvalScenarioResult> _results = [];
    private readonly Lock _resultsLock = new();

    /// <summary>Shared web app fixture, initialized once per test class lifetime.</summary>
    public WebAppFixture WebApp { get; } = new();

    /// <summary>Fixture-corpus source resolved at startup: shared-corpus or local-seed.</summary>
    public IEvalFixtureSource FixtureSource { get; private set; } = new LocalSeedFixtureSource();

    /// <summary>Bound runner; created once the web host is ready.</summary>
    public EvalRunner Runner { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var scenarios = EvalScenarioLoader.DiscoverScenarioIds()
            .Select(EvalScenarioLoader.LoadById)
            .ToArray();

        FixtureSource = SharedCorpusFixtureSource.TryCreate() ?? (IEvalFixtureSource)new LocalSeedFixtureSource();
        var seedProfile = EvalHarnessSupport.ResolveSeedProfile(scenarios);
        WebApp.UseSeed(FixtureSource.SeedPath, seedProfile);
        await WebApp.InitializeAsync();
        Runner = new EvalRunner(WebApp, FixtureSource);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            EmitReport();
        }
        finally
        {
            await WebApp.DisposeAsync();
        }
    }

    /// <summary>Records the outcome of a scenario run for aggregation into the final report.</summary>
    public void Record(EvalScenarioResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_resultsLock)
        {
            _results.Add(result);
        }
    }

    private void EmitReport()
    {
        List<EvalScenarioResult> snapshot;
        lock (_resultsLock)
        {
            snapshot = [.. _results];
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        var firstFailure = snapshot.FirstOrDefault(s => s.Status == EvalOverallStatus.Failed)?.Id;
        var totalElapsed = snapshot.Sum(s => s.ElapsedMs);

        var report = new EvalReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Environment = new EvalReportEnvironment
            {
                CorpusVersion = FixtureSource.CorpusVersion,
                CorpusSource = FixtureSource.Id,
                CorpusPath = FixtureSource.CorpusPath,
                RedisAvailable = EvalHarnessSupport.DetermineRedisAvailability(snapshot)
            },
            Scenarios = snapshot,
            Rollup = new EvalReportRollup
            {
                Total = snapshot.Count,
                Passed = snapshot.Count(s => s.Status == EvalOverallStatus.Passed),
                Failed = snapshot.Count(s => s.Status == EvalOverallStatus.Failed),
                PassedWithSkips = snapshot.Count(s => s.Status == EvalOverallStatus.PassedWithSkips),
                FirstFailure = firstFailure,
                TotalElapsedMs = totalElapsed
            }
        };

        var outputRoot = ResolveReportDirectory();
        Directory.CreateDirectory(outputRoot);
        var reportPath = Path.Combine(outputRoot, ReportFileName);
        var json = JsonSerializer.Serialize(report, EvalJsonContext.Default.EvalReport);
        File.WriteAllText(reportPath, json);
    }

    private static string ResolveReportDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("HONUA_EVAL_REPORT_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return overrideDir;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        return directory != null
            ? Path.Combine(directory.FullName, "tests", "TestResults")
            : Path.Combine(AppContext.BaseDirectory, "eval-results");
    }
}
