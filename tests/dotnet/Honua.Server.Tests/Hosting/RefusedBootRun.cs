// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;

namespace Honua.Server.Tests.Hosting;

// A server process expected to refuse its boot; one that is still running at the deadline is killed.
internal sealed record RefusedBootRun(bool Exited, int ExitCode, string Output)
{
    private const int TailLength = 4000;

    public string Tail => Output.Length <= TailLength ? Output : Output[^TailLength..];

    // The cached test payload's server in Production against the given database; callers add the setting under test.
    public static ProcessStartInfo ProductionServer(string connectionString, IEnumerable<string> inheritedKeysToRemove)
    {
        var serverDirectory = AppContext.BaseDirectory;
        var serverAssembly = Path.Join(serverDirectory, "Honua.Server.dll");
        File.Exists(serverAssembly).Should().BeTrue("the cached test payload contains the server runtime ({0})", serverDirectory);
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = serverDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(serverAssembly);
        foreach (var key in inheritedKeysToRemove)
        {
            start.Environment.Remove(key);
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;
        start.Environment["DataSource__Provider"] = "postgis";
        return start;
    }

    public static async Task<RefusedBootRun> RunAsync(ProcessStartInfo start, TimeSpan deadline)
    {
        using var process = new Process { StartInfo = start };
        process.Start().Should().BeTrue();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(deadline);
        var exited = true;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            exited = false;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        var output = await stdoutTask.ConfigureAwait(false) + await stderrTask.ConfigureAwait(false);
        return new RefusedBootRun(exited, process.ExitCode, output);
    }
}
