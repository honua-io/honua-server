// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Tests;

/// <summary>
/// Per-assembly collection definition for tests that share a Redis fixture.
/// </summary>
/// <remarks>
/// xUnit discovers collection definitions per test assembly, so each consuming project
/// compiles this shared source file. Tests in the collection remain serialized with one
/// another, while unrelated collections may run in parallel.
/// </remarks>
[CollectionDefinition(RedisFixture.CollectionName)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class RedisCollection : ICollectionFixture<RedisFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
