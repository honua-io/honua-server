// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.Server.Tests.Performance;

/// <summary>
/// Serializes server performance tests to reduce contention-driven timing noise.
/// </summary>
[CollectionDefinition("Performance", DisableParallelization = true)]
public sealed class PerformanceCollectionDefinition
{
}
