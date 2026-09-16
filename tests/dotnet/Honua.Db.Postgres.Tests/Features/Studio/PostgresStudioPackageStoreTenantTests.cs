// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Text.Json;
using DbUp;
using DbUp.Helpers;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Studio.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Studio;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Studio;

/// <summary>
/// Durable-store half of the honua-server#4905 proof: migration 120 adds the
/// <c>tenant_id</c> column the Studio lifecycle records its owning tenant in, the store writes
/// and reads it back on drafts, immutable versions and content items, and enumeration is scoped
/// to the caller's tenant in SQL rather than in the endpoint layer.
/// </summary>
[Collection("Database")]
public sealed class PostgresStudioPackageStoreTenantTests(PostgresFixture fixture)
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string LegacyTenant = "tenant-legacy";

    [IntegrationTest]
    public Task StudioContent_RecordsItsTenant_AndEnumerationStaysInsideIt()
        => WithSchemaAsync(async (schema, store) =>
        {
            var tenantADraft = await store.CreateDraftAsync(Draft("alpha", TenantA));
            var tenantBDraft = await store.CreateDraftAsync(Draft("beta", TenantB));
            var tenantAVersion = await store.CreateVersionAsync(tenantADraft, "seed", "alice");

            (await store.GetDraftAsync(tenantADraft.DraftId))!.TenantId.Should().Be(TenantA);
            tenantAVersion.TenantId.Should().Be(TenantA, "a version inherits the tenant of the draft it was minted from");
            (await store.GetVersionAsync(tenantADraft.ItemId, tenantAVersion.VersionId))!.TenantId.Should().Be(TenantA);
            (await store.GetPointersAsync(tenantADraft.ItemId))!.TenantId.Should().Be(TenantA);

            var tenantAItems = await store.ListContentItemsAsync(ItemQuery(new TenantScopeFilter(TenantA, false)));
            tenantAItems.Items.Select(static item => item.ItemId).Should().BeEquivalentTo([tenantADraft.ItemId]);
            tenantAItems.Total.Should().Be(1, "the COUNT behind the page must use the same tenant filter");

            var tenantBItems = await store.ListContentItemsAsync(ItemQuery(new TenantScopeFilter(TenantB, false)));
            tenantBItems.Items.Select(static item => item.ItemId).Should().BeEquivalentTo([tenantBDraft.ItemId]);

            var tenantADrafts = await store.ListDraftsAsync(DraftQuery(new TenantScopeFilter(TenantA, false)));
            tenantADrafts.Items.Select(static draft => draft.DraftId).Should().BeEquivalentTo([tenantADraft.DraftId]);
            tenantADrafts.Total.Should().Be(1);

            var everyTenant = await store.ListContentItemsAsync(ItemQuery(tenant: null));
            everyTenant.Items.Should().HaveCount(2, "a null scope is the multi-tenant-admin case and is not filtered");
        });

    [IntegrationTest]
    public Task StudioContent_WithNoRecordedTenant_IsOnlyEnumerableByTheDefaultTenantScope()
        => WithSchemaAsync(async (schema, store) =>
        {
            // A background writer with no request tenant records no tenant at all; such rows
            // belong to the deployment default, which the caller's scope reports through
            // IncludeUnassigned.
            var unassigned = await store.CreateDraftAsync(Draft("legacy", tenantId: null));

            var defaultScope = await store.ListContentItemsAsync(ItemQuery(new TenantScopeFilter("public", true)));
            defaultScope.Items.Select(static item => item.ItemId).Should().Contain(unassigned.ItemId);
            defaultScope.Total.Should().Be(1);

            var otherTenant = await store.ListContentItemsAsync(ItemQuery(new TenantScopeFilter(TenantB, false)));
            otherTenant.Items.Should().BeEmpty("another tenant never inherits tenant-unassigned content");
            otherTenant.Total.Should().Be(0);

            var unresolvedTenant = await store.ListDraftsAsync(DraftQuery(new TenantScopeFilter(null, true)));
            unresolvedTenant.Items.Select(static draft => draft.DraftId).Should().Contain(unassigned.DraftId);

            var unresolvedButNotDefault = await store.ListDraftsAsync(DraftQuery(new TenantScopeFilter(null, false)));
            unresolvedButNotDefault.Items.Should().BeEmpty();
            unresolvedButNotDefault.Total.Should().Be(0);
        });

    [IntegrationTest]
    public async Task Migration120_BackfillsTheTenantAlreadyEncodedInATenantQualifiedOwnerId()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTenantTests));
        try
        {
            // Pre-upgrade state: the Studio tables without the tenant column.
            ApplyMigrations(schema, PreTenantMigrations);

            var qualifiedItem = Guid.NewGuid();
            var unqualifiedItem = Guid.NewGuid();
            var ownerKey = $"subject:https%3A%2F%2Fidp.example.com:alice@tenant:{LegacyTenant}";
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = $"""
                    INSERT INTO {Quote(schema)}.studio_content_items
                        (item_id, package_key, family, owner_id, created_by, updated_by)
                    VALUES
                        (@qualified, 'legacy-qualified', 'query', @owner, 'alice', 'alice'),
                        (@unqualified, 'legacy-unqualified', 'query', 'shared-studio-key', 'key', 'key');
                    """;
                seed.Parameters.AddWithValue("qualified", qualifiedItem);
                seed.Parameters.AddWithValue("unqualified", unqualifiedItem);
                seed.Parameters.AddWithValue("owner", ownerKey);
                await seed.ExecuteNonQueryAsync();
            }

            // Upgrade. Applied twice so the migration's idempotence is proven, not assumed.
            ApplyMigrations(schema, [TenantMigration]);
            ApplyMigrations(schema, [TenantMigration]);

            var store = CreateStore(schema);
            (await store.GetPointersAsync(qualifiedItem))!.TenantId.Should().Be(
                LegacyTenant,
                "a tenant-qualified owner id already names its tenant, so the upgrade recovers it precisely");
            (await store.GetPointersAsync(unqualifiedItem))!.TenantId.Should().BeNull(
                "an owner id with no tenant segment is left unassigned and attributed to the default tenant at runtime");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    /// <summary>
    /// The Studio lifecycle DDL this store needs, up to but excluding the tenant column.
    /// Migration 089 is deliberately omitted: it only adds an enumeration index over the
    /// publishing slice's <c>content_publication_versions</c> table, which this Studio-only
    /// schema does not create.
    /// </summary>
    private static readonly string[] PreTenantMigrations =
    [
        "035_CreateStudioPackageLifecycle.sql",
        "090_AddStudioContentItemOwner.sql",
    ];

    private const string TenantMigration = "120_AddStudioTenantOwnership.sql";

    private async Task WithSchemaAsync(Func<string, PostgresStudioPackageStore, Task> action)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTenantTests));
        try
        {
            ApplyMigrations(schema, [.. PreTenantMigrations, TenantMigration]);
            await action(schema, CreateStore(schema));
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private void ApplyMigrations(string schema, IReadOnlyList<string> migrations)
    {
        var builder = DeployChanges.To.PostgresqlDatabase(fixture.ConnectionString)
            .JournalTo(new NullJournal());
        foreach (var migration in migrations)
        {
            builder = builder.WithScript(
                migration,
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", migration)));
        }

        var result = builder
            .WithVariable("HonuaSchema", SchemaSearchPath.ValidateAndQuote(schema))
            .WithTransaction()
            .Build()
            .PerformUpgrade();
        result.Successful.Should().BeTrue(result.Error?.ToString() ?? "(no error reported)");
    }

    private PostgresStudioPackageStore CreateStore(string schema)
    {
        var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            (DbConnection)await fixture.DataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        return new PostgresStudioPackageStore(provider, schema);
    }

    private static string Quote(string schema) => SchemaSearchPath.ValidateAndQuote(schema);

    private static StudioContentItemQuery ItemQuery(TenantScopeFilter? tenant)
        => new() { Tenant = tenant, Limit = 50 };

    private static StudioPackageDraftQuery DraftQuery(TenantScopeFilter? tenant)
        => new() { Tenant = tenant, Limit = 50 };

    private static StudioPackageDraft Draft(string packageKey, string? tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        using var body = JsonDocument.Parse("""{"where":"1=1"}""");
        return new StudioPackageDraft
        {
            DraftId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            PackageKey = packageKey,
            WorkspaceId = "studio",
            OwnerId = $"owner-{packageKey}",
            TenantId = tenantId,
            Family = StudioPackageFamily.Query,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Query,
                SchemaVersion = "1.0",
                Format = "studio_query_package.v1",
                Body = body.RootElement.Clone(),
            },
            Generation = 1,
            CreatedBy = "alice",
            UpdatedBy = "alice",
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
