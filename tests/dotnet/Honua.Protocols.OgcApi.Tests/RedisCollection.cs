// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api;

/// <summary>
/// Per-assembly collection definition for tests that share a Redis fixture.
/// </summary>
/// <remarks>
/// xUnit discovers collection definitions per test assembly, so each consuming assembly
/// requires this definition. Keep this canonical pattern aligned across test assemblies;
/// the shared collection name and fixture live in <see cref="RedisFixture"/>.
/// </remarks>
[CollectionDefinition(RedisFixture.CollectionName, DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class RedisCollection : ICollectionFixture<RedisFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
