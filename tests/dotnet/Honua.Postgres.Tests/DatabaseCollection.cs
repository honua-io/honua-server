// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Postgres.Tests;

/// <summary>
/// Collection definition for tests that share a PostgreSQL container.
/// </summary>
[CollectionDefinition("Database")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This is an xUnit collection definition which requires the Collection suffix.")]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
}
