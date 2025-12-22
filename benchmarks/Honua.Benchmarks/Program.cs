// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Running;
using Honua.Benchmarks;

// Run all benchmarks or filter by class
if (args.Length > 0 && args[0] == "--filter")
{
    var filter = args.Length > 1 ? args[1] : "*";
    BenchmarkSwitcher.FromAssembly(typeof(BenchmarkProgram).Assembly).Run(args);
}
else
{
    // Run query benchmarks only for now
    BenchmarkRunner.Run<QueryBenchmarks>();
}

namespace Honua.Benchmarks
{
    // Separate class to avoid conflicts with Honua.Server.Program
    public class BenchmarkProgram { }
}
