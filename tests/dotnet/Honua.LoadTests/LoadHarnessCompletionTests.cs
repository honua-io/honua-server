// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.TestKit.Attributes;
using Honua.TestKit.Performance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Honua.LoadTests;

/// <summary>
/// Exercises real CLI sessions against independently counted HTTP fixtures.
/// </summary>
public sealed class LoadHarnessCompletionTests
{
    [IntegrationTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Cli_CompletesWindow_ReportsMeasuredSuccessesAndFailures(int failureMode)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"honua-load-completion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        long requests = 0;
        app.Run(async context =>
        {
            Interlocked.Increment(ref requests);
            context.Response.ContentType = "application/json";
            if (failureMode >= 2)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentLength = 0;
            }
            else if (failureMode == 1)
            {
                // Headers succeed; the advertised body never arrives. HttpClient's header
                // timeout alone cannot bound this fixture's response-body read.
                context.Response.ContentLength = 100;
                await context.Response.StartAsync(context.RequestAborted);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                }
            }
            else
            {
                await Task.Delay(25, context.RequestAborted);
                await context.Response.WriteAsync("{\"features\":[],\"value\":[]}", context.RequestAborted);
            }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        var statsPath = Path.Combine(directory, "stats.json");
        await File.WriteAllTextAsync(statsPath, "stale statistics from an earlier run");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory
        };
        foreach (var argument in new[]
        {
            typeof(Program).Assembly.Location, "--base-url", address, "--profile", failureMode == 3 ? "quick" : " SoAk ",
            // Match the bare numeric seconds forwarded by the candidate producer.
            "--ramp-up", "2", "--duration", "6", "--ramp-down", "2",
            "--stats-out", statsPath, "--report-folder", directory, "--report-formats", "csv"
        })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["HONUA_LOAD_REQUEST_TIMEOUT_SECONDS"] = "1";
        if (failureMode >= 2)
        {
            start.ArgumentList.Add("--target-scenarios");
            start.ArgumentList.Add(LoadTestScenarios.FeatureQueryScenarioName);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout + await stderr;
            var fails = failureMode != 0;
            Assert.True(process.ExitCode == (fails ? 1 : 0), output);
            if (failureMode == 3)
            {
                // Quick retains NBomber's circuit breaker. A deliberately truncated run
                // must not replace the stale input with apparently complete receipt data.
                Assert.True(Interlocked.Read(ref requests) >= 5000, output);
                Assert.Contains("Refusing partial statistics", output, StringComparison.Ordinal);
                Assert.False(File.Exists(statsPath));
                return;
            }

            Assert.Contains("final statistics received", output, StringComparison.Ordinal);
            Assert.Contains("Completed load test", output, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(statsPath));
            var root = document.RootElement;
            Assert.Equal("soak", root.GetProperty("profile").GetString());
            Assert.Equal(address, root.GetProperty("baseUrl").GetString());
            Assert.Equal(10, root.GetProperty("durationSeconds").GetDouble());
            var total = root.GetProperty("allRequestCount").GetInt64();
            Assert.True(total > 0, output);
            Assert.Equal(total, root.GetProperty(fails ? "allFailCount" : "allOkCount").GetInt64());
            Assert.Equal(0, root.GetProperty(fails ? "allOkCount" : "allFailCount").GetInt64());
            Assert.Equal(Interlocked.Read(ref requests), total);
            if (failureMode == 2)
            {
                // Deliberately exceed NBomber's default per-scenario abort count. A soak
                // must finish the full window, retain every failure, and STILL exit red.
                Assert.True(total > 5000, output);
            }

            var scenarios = root.GetProperty("scenarios").EnumerateArray().ToArray();
            var expectedNames = failureMode == 2
                ? [LoadTestScenarios.FeatureQueryScenarioName]
                : Program.KnownScenarios;
            Assert.Equal(expectedNames.Order(StringComparer.Ordinal),
                scenarios.Select(s => s.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
            Assert.All(scenarios, scenario =>
            {
                Assert.True(scenario.GetProperty(fails ? "failCount" : "okCount").GetInt64() > 0);
                Assert.Equal(10, scenario.GetProperty("durationSeconds").GetDouble());
                if (!fails)
                {
                    Assert.True(scenario.GetProperty("p95Ms").GetDouble() >= 25,
                        scenario.ToString());
                    Assert.True(scenario.GetProperty("p99Ms").GetDouble() >= scenario.GetProperty("p95Ms").GetDouble());
                    var expectedRps = scenario.GetProperty("okCount").GetDouble() / 10;
                    Assert.InRange(scenario.GetProperty("okRps").GetDouble(), expectedRps - 0.01, expectedRps + 0.01);
                }
            });
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            await app.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
