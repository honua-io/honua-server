// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Db.Postgres.Features.Studio;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Studio;

public sealed partial class PostgresStudioPackageStoreTests
{
    private const string SharedStyleId = "shared-roads";

    [IntegrationTest]
    public async Task SharedStyleConsumers_StayInsideTheCallersTenant()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests) + "_Style");
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);
            var tenantA = await store.CreateDraftAsync(StyledMapDraft("map-a") with { TenantId = "tenant-a" });
            await store.CreateVersionAsync(tenantA, "seed", "alice");
            var tenantB = await store.CreateDraftAsync(StyledMapDraft("map-b") with { TenantId = "tenant-b" });
            var unassigned = await store.CreateDraftAsync(StyledMapDraft("map-default"));
            var index = new PostgresSharedStyleConsumerIndex(provider, schema);

            var onlyA = await ConsumerItemsAsync(index, new TenantScopeFilter("tenant-a", IncludeUnassigned: false));
            onlyA.Should().BeEquivalentTo([tenantA.ItemId]);

            var defaultA = await ConsumerItemsAsync(index, new TenantScopeFilter("tenant-a", IncludeUnassigned: true));
            defaultA.Should().BeEquivalentTo([tenantA.ItemId, unassigned.ItemId]);

            var noTenant = await ConsumerItemsAsync(index, new TenantScopeFilter(null, IncludeUnassigned: false));
            noTenant.Should().BeEmpty();

            var unscoped = await ConsumerItemsAsync(index, tenant: null);
            unscoped.Should().BeEquivalentTo([tenantA.ItemId, tenantB.ItemId, unassigned.ItemId]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static async Task<IReadOnlyList<Guid>> ConsumerItemsAsync(
        PostgresSharedStyleConsumerIndex index,
        TenantScopeFilter? tenant)
    {
        var scan = await index.ListConsumersAsync(
            new SharedStyleConsumerQuery { StyleId = SharedStyleId, Limit = 50, Tenant = tenant });
        scan.Complete.Should().BeTrue();
        return scan.Consumers.Select(static row => Guid.Parse(row.ItemId)).Distinct().ToArray();
    }

    private static StudioPackageDraft StyledMapDraft(string packageKey)
    {
        var draft = BuildDraft("1=1", packageKey: packageKey);
        using var body = JsonDocument.Parse($$"""{"layers":[{"styleId":"{{SharedStyleId}}"}]}""");
        return draft with
        {
            Family = StudioPackageFamily.Map,
            Envelope = draft.Envelope with { Family = StudioPackageFamily.Map, Body = body.RootElement.Clone() },
        };
    }
}
