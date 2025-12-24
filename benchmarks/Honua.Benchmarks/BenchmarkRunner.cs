// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Running;

namespace Honua.Benchmarks;

public static class BenchmarkEntryPoint
{
    public static void Main(string[] args)
    {
        // Parse command line for benchmark selection
        if (args.Length > 0)
        {
            // Use BenchmarkSwitcher to enable filtering and multi-benchmark runs
            BenchmarkSwitcher.FromAssembly(typeof(QueryBenchmarks).Assembly).Run(args);
        }
        else
        {
            // Default: Run query benchmarks
            Console.WriteLine("Available benchmark categories:");
            Console.WriteLine("  --filter *Query*     - Query latency benchmarks");
            Console.WriteLine("  --filter *MemorySoak*  - Memory leak detection");
            Console.WriteLine("  --filter *LoadTest*  - Throughput and concurrency");
            Console.WriteLine();
            Console.WriteLine("Running query benchmarks by default...");
            Console.WriteLine();

            BenchmarkRunner.Run<QueryBenchmarks>();
        }
    }
}
