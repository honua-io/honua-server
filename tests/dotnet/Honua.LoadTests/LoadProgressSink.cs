// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.Contracts.Metrics;
using NBomber.Contracts.Stats;

namespace Honua.LoadTests;

/// <summary>
/// Records the session lifecycle even when NBomber never reaches its final report.
/// Progress is diagnostic only: the receipt still consumes completed run statistics.
/// </summary>
internal sealed class LoadProgressSink : IReportingSink
{
    private static string _lastProgress = "session has not started";

    internal static string LastProgress => Volatile.Read(ref _lastProgress);

    public string SinkName => "load-progress";

    public Task Init(IBaseContext context, IConfiguration infraConfig) => Task.CompletedTask;

    public Task Start(SessionStartInfo sessionInfo)
    {
        Report("session started");
        return Task.CompletedTask;
    }

    public Task SaveRealtimeStats(ScenarioStats[] stats)
    {
        // Emit once per minute, including the first interval, to keep a full soak log bounded.
        // Whole seconds come from ticks. TotalSeconds is a double, so `% 60 == 0`
        // misses minute boundaries that are not an exact binary multiple of 60.
        foreach (var scenario in stats)
        {
            var wholeSeconds = scenario.Duration.Ticks / TimeSpan.TicksPerSecond;
            if (scenario.Duration <= TimeSpan.FromSeconds(5) || wholeSeconds % 60 == 0)
            {
                Report($"{scenario.ScenarioName}: elapsed={scenario.Duration}, ok={scenario.Ok.Request.Count}, failed={scenario.Fail.Request.Count}, simulation={scenario.LoadSimulationStats.SimulationName}");
            }
        }

        return Task.CompletedTask;
    }

    public Task SaveRealtimeMetrics(MetricStats metrics) => Task.CompletedTask;

    public Task SaveFinalStats(NodeStats stats)
    {
        Report($"final statistics received: duration={stats.Duration}");
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        Report("reporting stopped");
        return Task.CompletedTask;
    }

    public void Dispose() { }

    private static void Report(string message)
    {
        Volatile.Write(ref _lastProgress, message);
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} load-progress: {message}");
    }
}
