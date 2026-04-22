// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit;

/// <summary>
/// Collection definition for tests that share a PostgreSQL container.
/// Tests in this collection will share the same database container but use
/// schema-based isolation for parallel execution.
/// </summary>
[CollectionDefinition("Database")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
