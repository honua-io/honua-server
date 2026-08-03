// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Running;

namespace Honua.Benchmarks;

/// <summary>
/// Entry point for the BenchmarkDotNet harness.
/// Use <c>dotnet run -c Release -- --filter '*'</c> to execute all benchmarks,
/// or pass a category/class filter to narrow the run.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "raster-storage", StringComparison.Ordinal))
        {
            return await RasterStorage.RasterStorageCommand.RunAsync(args[1..], CancellationToken.None).ConfigureAwait(false);
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
