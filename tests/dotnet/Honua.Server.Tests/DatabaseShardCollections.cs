// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests;

// xUnit parallelizes across collections, never within one collection. Each test has an
// isolated PostgreSQL schema, routed through X-Honua-Test-Schema. Metadata-v2 singleton
// state is partitioned by schema in TestMetadataV2GraphProvider, allowing concurrent mutations.
//
// Sibling collections share DatabaseFixtureAdapter and the single ref-counted PostGIS
// container owned by PostgresFixture. Assigning classes by CI shard area enables
// parallelism within each filtered test set without starting additional containers.

[CollectionDefinition("Database.CoreFeatureStore")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCoreFeatureStoreCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
}

[CollectionDefinition("Database.CoreEndpoints")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCoreEndpointsCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
}

[CollectionDefinition("Database.Alerts", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseAlertsCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
}

[CollectionDefinition("Database.CoreSpatial")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseCoreSpatialCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
}

[CollectionDefinition("Database.AdminAuthorization")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseAdminAuthorizationCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
}

[CollectionDefinition("Database.SceneGrpc")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseSceneGrpcCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
}

[CollectionDefinition("Database.OidcAuthentication")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public class DatabaseOidcAuthenticationCollection : ICollectionFixture<DatabaseFixtureAdapter>
{
}
