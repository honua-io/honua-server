// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Running;

namespace Honua.Benchmarks;

public static class BenchmarkEntryPoint
{
    public static int Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(BenchmarkEntryPoint).Assembly).Run(args);
        return 0;
    }
}
