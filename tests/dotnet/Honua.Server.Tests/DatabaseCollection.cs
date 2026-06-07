// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests;

/// <summary>
/// Collection definition for Server tests that share a database container.
/// Tests in this collection share global catalog state and must remain isolated
/// from the schema-parallel shard collections.
/// Uses an abstracted database fixture to maintain Clean Architecture principles.
/// </summary>
[CollectionDefinition("Database", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
