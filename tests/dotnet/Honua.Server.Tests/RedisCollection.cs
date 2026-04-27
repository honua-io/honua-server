// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests;

/// <summary>
/// Collection definition for Server tests that share a Redis container.
/// Some Redis-backed tests also create database-backed web app fixtures, so
/// this collection is isolated from other collections to avoid shared seed
/// setup contention.
/// </summary>
[CollectionDefinition("Redis", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class RedisCollection : ICollectionFixture<RedisFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
