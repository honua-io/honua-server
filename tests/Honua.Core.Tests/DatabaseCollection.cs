// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Tests.Infrastructure;
using Xunit;

namespace Honua.Core.Tests;

/// <summary>
/// Collection definition for Core tests that share a database container.
/// Tests in this collection will share the same database container but use
/// schema-based isolation for parallel execution.
/// Uses an abstracted database fixture to maintain Clean Architecture principles.
/// </summary>
[CollectionDefinition("Database")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}