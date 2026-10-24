// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.Server.Tests.Performance;

/// <summary>
/// Serializes server performance tests to reduce contention-driven timing noise.
/// </summary>
[CollectionDefinition("Performance", DisableParallelization = true)]
public sealed class PerformanceCollectionDefinition
{
}
