// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Honua.LoadTests;

public sealed class LoadHarnessCompletionTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_AllScenariosComplete_ReportsMeasuredSuccessesOrBodyTimeouts(bool stallBody)
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
            if (stallBody)
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
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory
        };
        foreach (var argument in new[]
        {
            typeof(Program).Assembly.Location, "--base-url", address, "--profile", "soak",
            "--ramp-up", "2s", "--duration", "6s", "--ramp-down", "2s",
            "--stats-out", statsPath, "--report-folder", directory, "--report-formats", "csv"
        })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["HONUA_LOAD_REQUEST_TIMEOUT_SECONDS"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout + await stderr;
            Assert.True(process.ExitCode == (stallBody ? 1 : 0), output);
            Assert.Contains("final statistics received", output, StringComparison.Ordinal);
            Assert.Contains("Completed load test", output, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(statsPath));
            var root = document.RootElement;
            Assert.Equal("soak", root.GetProperty("profile").GetString());
            Assert.Equal(address, root.GetProperty("baseUrl").GetString());
            Assert.Equal(10, root.GetProperty("durationSeconds").GetDouble());
            var total = root.GetProperty("allRequestCount").GetInt64();
            Assert.True(total > 0, output);
            Assert.Equal(total, root.GetProperty(stallBody ? "allFailCount" : "allOkCount").GetInt64());
            Assert.Equal(0, root.GetProperty(stallBody ? "allOkCount" : "allFailCount").GetInt64());
            Assert.Equal(Interlocked.Read(ref requests), total);
            var scenarios = root.GetProperty("scenarios").EnumerateArray().ToArray();
            Assert.Equal(Program.KnownScenarios.Order(StringComparer.Ordinal),
                scenarios.Select(s => s.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
            Assert.All(scenarios, scenario =>
            {
                Assert.True(scenario.GetProperty(stallBody ? "failCount" : "okCount").GetInt64() > 0);
                Assert.Equal(10, scenario.GetProperty("durationSeconds").GetDouble());
                if (!stallBody)
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
