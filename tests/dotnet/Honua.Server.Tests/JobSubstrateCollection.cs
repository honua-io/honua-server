// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests;

// Hosts built with WithProductionJobSubstrate share the job tables of one database, so their
// tests run one at a time, without overlapping hosts in other collections. The database
// fixture keeps the container alive between them and gives tests a data source to clear tables.
[CollectionDefinition(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection, DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class JobSubstrateCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
}
