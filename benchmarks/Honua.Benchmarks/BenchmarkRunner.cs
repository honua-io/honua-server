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
            BenchmarkSwitcher.FromAssembly(typeof(SqlGenerationBenchmarks).Assembly).Run(args);
        }
        else
        {
            // Default: Show help and run all benchmarks
            Console.WriteLine("Honua Server Performance Benchmarks");
            Console.WriteLine("==================================");
            Console.WriteLine();
            Console.WriteLine("Available benchmark categories:");
            Console.WriteLine("  --filter *Query*              - End-to-end query performance");
            Console.WriteLine("  --filter *SqlGeneration*      - SQL query building performance");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  dotnet run --filter *SqlGeneration*");
            Console.WriteLine("  dotnet run --filter *Query* --job short");
            Console.WriteLine("  dotnet run --filter *Query* --exporters json,html");
            Console.WriteLine();
            Console.WriteLine("For performance analysis, use:");
            Console.WriteLine("  dotnet run --filter *Performance* --profiler ETW");
            Console.WriteLine();
            Console.WriteLine("Running all benchmarks (this may take a while)...");
            Console.WriteLine();

            // Run core benchmarks (query and SQL generation)
            var benchmarks = new[]
            {
                typeof(QueryBenchmarks),
                typeof(SqlGenerationBenchmarks)
            };

            BenchmarkRunner.Run(benchmarks);
        }
    }
}
