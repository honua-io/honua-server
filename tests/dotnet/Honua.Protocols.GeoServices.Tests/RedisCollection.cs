// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

/// <summary>
/// Collection definition for the GeoServices GPServer durable-runtime tests that share a
/// Redis container. xUnit discovers <c>[CollectionDefinition]</c> per test assembly, so this
/// must live in <c>Honua.Protocols.GeoServices.Tests</c> (it previously resolved from
/// <c>Honua.Server.Tests</c> before the GeoServices tests were split into this assembly).
/// </summary>
[CollectionDefinition("Redis", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class RedisCollection : ICollectionFixture<RedisFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
