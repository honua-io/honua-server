// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.Architecture.Tests;

internal static class RepositoryPythonScript
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string relativeScript, params string[] arguments)
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var start = new ProcessStartInfo("python3")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ArchitectureTestHelpers.CombinePath([root, .. relativeScript.Split('/')]));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start python3 {relativeScript}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{relativeScript} exceeded 10 minutes.");
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }
}
