// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Honua.ReleaseWorkflows.Tests;

public sealed class DispatchBindingTests
{
    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Join(directory.FullName, "scripts/release/dispatch-and-wait.sh")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    [Fact]
    public async Task Dispatch_OverlappingUnrelatedSuccess_AwaitsIntendedFailure()
    {
        var result = await DispatchAsync("overlap");
        Assert.Equal(0, result.ExitCode);
        using var receipt = JsonDocument.Parse(result.Output);
        Assert.Equal("failure", receipt.RootElement.GetProperty("conclusion").GetString());
        Assert.Contains("202", result.Calls, StringComparison.Ordinal);
        Assert.DoesNotContain("watch 303", result.Calls, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error, string Calls)> DispatchAsync(string scenario, params string[] arguments)
    {
        var temporary = Path.Join(Path.GetTempPath(), "release-dispatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var fixture = Path.Join(RepositoryRoot, "tests/dotnet/Honua.ReleaseWorkflows.Tests/Fixtures/github.py");
            await File.WriteAllTextAsync(Path.Join(temporary, "gh"), $"#!/bin/bash\nexec python3 '{fixture}' \"$@\"\n");
            await File.WriteAllTextAsync(Path.Join(temporary, "sleep"), "#!/bin/bash\nexit 0\n");
            using (var chmod = Process.Start("chmod", $"+x {temporary}/gh {temporary}/sleep"))
            {
                await chmod!.WaitForExitAsync();
                Assert.Equal(0, chmod.ExitCode);
            }

            var start = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = RepositoryRoot
            };
            start.ArgumentList.Add("scripts/release/dispatch-and-wait.sh");
            foreach (var argument in new[] { "--id", "sdk-test", "--repo", "honua-io/client", "--workflow", "compat.yml", "--ref", "trunk", "--timeout", "2" }.Concat(arguments))
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["PATH"] = temporary + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            start.Environment["FIXTURE_ROOT"] = temporary;
            start.Environment["FIXTURE_SCENARIO"] = scenario;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var calls = Path.Join(temporary, "calls");
            return (process.ExitCode, await output, await error, File.Exists(calls) ? await File.ReadAllTextAsync(calls) : "");
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}
