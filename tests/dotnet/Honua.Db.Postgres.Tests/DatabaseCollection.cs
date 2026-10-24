// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Db.Postgres.Tests;

/// <summary>
/// Collection definition for tests that share a PostgreSQL container.
/// </summary>
[CollectionDefinition("Database", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This is an xUnit collection definition which requires the Collection suffix.")]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
}
