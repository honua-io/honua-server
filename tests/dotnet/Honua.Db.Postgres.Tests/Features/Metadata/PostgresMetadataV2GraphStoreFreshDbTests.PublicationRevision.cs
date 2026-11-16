// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    [IntegrationTest]
    public async Task SaveAsync_FirstSnapshotCreatedAfterBootstrap_RejectsStaleBootstrapEtag()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("publication_revision");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var bootstrap = await store.GetCurrentAsync();
            var concurrent = await store.SaveAsync(bootstrap.Graph, expectedEtag: null);
            var stale = () => store.SaveAsync(bootstrap.Graph, bootstrap.Etag);
            await stale.Should().ThrowAsync<MetadataV2GraphConcurrencyException>();
            (await store.GetCurrentAsync()).Etag.Should().Be(concurrent.Etag);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
