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
    public static int Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
