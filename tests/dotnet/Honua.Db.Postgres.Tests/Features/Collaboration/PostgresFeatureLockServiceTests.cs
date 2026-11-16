// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Collaboration;

[Collection("Database")]
public sealed class PostgresFeatureLockServiceTests(PostgresFixture fixture)
{
    private static readonly FeatureRef Feature = new("roads", 1, "42");
    private static readonly LockHolder Alice = new("alice", SessionId: "browser-a", TenantId: "tenant");
    private static readonly LockHolder Bob = new("bob", SessionId: "browser-b", TenantId: "tenant");
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

    [IntegrationTest]
    public async Task ClaimAsync_IndependentHosts_OnlyOneEditorOwnsFeature()
    {
        await WithHostsAsync(async (first, second, _) =>
        {
            var claims = await Task.WhenAll(
                first.ClaimAsync(Feature, Alice, Duration, FeatureLockAccessContext.AuthorizedWrite).AsTask(),
                second.ClaimAsync(Feature, Bob, Duration, FeatureLockAccessContext.AuthorizedWrite).AsTask());

            claims.Count(claim => claim.Status == FeatureLockClaimStatus.Claimed).Should().Be(1);
            claims.Count(claim => claim.Status == FeatureLockClaimStatus.HeldByOther).Should().Be(1);
            (await first.GetActiveLeaseAsync(Feature)).Should().BeEquivalentTo(await second.GetActiveLeaseAsync(Feature));
            (await second.HasAnyActiveLeasesAsync()).Should().BeTrue();
        });
    }

    [IntegrationTest]
    public async Task RenewAndReleaseAsync_AnotherHost_UsesPersistedOwnership()
    {
        await WithHostsAsync(async (first, second, _) =>
        {
            var claim = await first.ClaimAsync(Feature, Alice, Duration, FeatureLockAccessContext.AuthorizedWrite);
            var blocked = await second.ReleaseAsync(Feature, Bob, FeatureLockAccessContext.AuthorizedWrite);
            blocked.Status.Should().Be(FeatureLockReleaseStatus.HeldByOther);
            var renewal = await second.RenewAsync(Feature, Alice, Duration, FeatureLockAccessContext.AuthorizedWrite);
            renewal.Status.Should().Be(FeatureLockRenewStatus.Renewed);
            renewal.Lease!.LockId.Should().Be(claim.Lease!.LockId);
            renewal.Lease.AcquiredAt.Should().Be(claim.Lease.AcquiredAt);
            var release = await second.ReleaseAsync(Feature, Alice, FeatureLockAccessContext.AuthorizedWrite);
            release.Status.Should().Be(FeatureLockReleaseStatus.Released);
            (await first.GetActiveLeaseAsync(Feature)).Should().BeNull();
            (await first.HasAnyActiveLeasesAsync()).Should().BeFalse();
        });
    }

    [IntegrationTest]
    public async Task ClaimAsync_ExpiredLease_AllowsAnotherHostAndRejectsOldOwner()
    {
        await WithHostsAsync(async (first, second, schema) =>
        {
            var original = await first.ClaimAsync(Feature, Alice, Duration, FeatureLockAccessContext.AuthorizedWrite);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var expire = new NpgsqlCommand(
                $"UPDATE \"{schema}\".feature_edit_locks SET expires_at = clock_timestamp() - interval '1 second'", connection);
            await expire.ExecuteNonQueryAsync();
            (await second.GetActiveLeaseAsync(Feature)).Should().BeNull();
            var replacement = await second.ClaimAsync(Feature, Bob, Duration, FeatureLockAccessContext.AuthorizedWrite);
            replacement.Status.Should().Be(FeatureLockClaimStatus.Claimed);
            replacement.Lease!.LockId.Should().NotBe(original.Lease!.LockId);
            (await first.RenewAsync(Feature, Alice, Duration, FeatureLockAccessContext.AuthorizedWrite))
                .Status.Should().Be(FeatureLockRenewStatus.HeldByOther);
        });
    }

    [IntegrationTest]
    public async Task ClaimAsync_DeniedAndDifferentSession_DoNotChangeLease()
    {
        await WithHostsAsync(async (first, second, _) =>
        {
            (await first.ClaimAsync(Feature, Alice, Duration, FeatureLockAccessContext.ReadOnly))
                .Status.Should().Be(FeatureLockClaimStatus.Denied);
            (await second.HasAnyActiveLeasesAsync()).Should().BeFalse();
            await first.ClaimAsync(Feature, Alice, Duration, FeatureLockAccessContext.AuthorizedWrite);
            var otherSession = Alice with { SessionId = "other-browser" };
            (await second.ClaimAsync(Feature, otherSession, Duration, FeatureLockAccessContext.AuthorizedWrite))
                .Status.Should().Be(FeatureLockClaimStatus.HeldByOther);
        });
    }

    [IntegrationTest]
    public async Task GetActiveLeaseAsync_RecreatedHost_RetainsOwnership()
    {
        await WithHostsAsync(async (first, _, schema) =>
        {
            var claim = await first.ClaimAsync(Feature, Alice, Duration, FeatureLockAccessContext.AuthorizedWrite);
            await using var restarted = CreateHost(schema);
            await using var scope = restarted.CreateAsyncScope();
            var locks = scope.ServiceProvider.GetRequiredService<IFeatureLockService>();
            (await locks.GetActiveLeaseAsync(Feature)).Should().BeEquivalentTo(claim.Lease);
            (await locks.ClaimAsync(Feature, Bob, Duration, FeatureLockAccessContext.AuthorizedWrite))
                .Status.Should().Be(FeatureLockClaimStatus.HeldByOther);
        });
    }

    [IntegrationTest]
    public async Task PruneExpiredAsync_AnotherHost_RemovesExpiredLeasesOnly()
    {
        await WithHostsAsync(async (first, second, schema) =>
        {
            await first.ClaimAsync(Feature, Alice, Duration, FeatureLockAccessContext.AuthorizedWrite);
            var active = Feature with { FeatureId = "43" };
            await second.ClaimAsync(active, Bob, Duration, FeatureLockAccessContext.AuthorizedWrite);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var expire = new NpgsqlCommand(
                $"UPDATE \"{schema}\".feature_edit_locks SET expires_at = clock_timestamp() - interval '1 second' WHERE feature_id = '42'", connection);
            await expire.ExecuteNonQueryAsync();
            (await second.PruneExpiredAsync()).Should().Be(1);
            (await first.GetActiveLeaseAsync(Feature)).Should().BeNull();
            (await first.GetActiveLeaseAsync(active)).Should().NotBeNull();
        });
    }

    [IntegrationTest]
    public async Task MutationsAsync_DifferentPrincipal_PreserveOriginalLease()
    {
        await WithHostsAsync(async (first, second, _) =>
        {
            var owner = Alice with { PrincipalName = "editor-a" };
            var other = owner with { PrincipalName = "editor-b" };
            var original = await first.ClaimAsync(Feature, owner, Duration, FeatureLockAccessContext.AuthorizedWrite);
            (await second.ClaimAsync(Feature, other, Duration, FeatureLockAccessContext.AuthorizedWrite))
                .Status.Should().Be(FeatureLockClaimStatus.HeldByOther);
            (await second.RenewAsync(Feature, other, Duration, FeatureLockAccessContext.AuthorizedWrite))
                .Status.Should().Be(FeatureLockRenewStatus.HeldByOther);
            (await second.ReleaseAsync(Feature, other, FeatureLockAccessContext.AuthorizedWrite))
                .Status.Should().Be(FeatureLockReleaseStatus.HeldByOther);
            (await first.GetActiveLeaseAsync(Feature)).Should().BeEquivalentTo(original.Lease);
        });
    }

    [IntegrationTest]
    public async Task ClaimAsync_ResolvedTenantSchema_IsolatesIdenticalFeatures()
    {
        await WithHostsAsync(async (first, _, rootSchema) =>
        {
            await WithHostsAsync(async (_, _, tenantSchema) =>
            {
                await first.ClaimAsync(Feature, Alice, Duration, FeatureLockAccessContext.AuthorizedWrite);
                await using var host = CreateHost(rootSchema, tenantSchema);
                await using var scope = host.CreateAsyncScope();
                var tenant = scope.ServiceProvider.GetRequiredService<IFeatureLockService>();
                (await tenant.GetActiveLeaseAsync(Feature)).Should().BeNull();
                (await tenant.ClaimAsync(Feature, Bob, Duration, FeatureLockAccessContext.AuthorizedWrite))
                    .Status.Should().Be(FeatureLockClaimStatus.Claimed);
                (await first.GetActiveLeaseAsync(Feature))!.Holder.Should().Be(Alice);
                (await tenant.GetActiveLeaseAsync(Feature))!.Holder.Should().Be(Bob);
                (await tenant.ReleaseAsync(Feature, Bob, FeatureLockAccessContext.AuthorizedWrite))
                    .Status.Should().Be(FeatureLockReleaseStatus.Released);
                (await first.GetActiveLeaseAsync(Feature)).Should().NotBeNull();
            });
        });
    }

    private async Task WithHostsAsync(Func<IFeatureLockService, IFeatureLockService, string, Task> verify)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresFeatureLockServiceTests));
        try
        {
            var path = RepositoryPaths.Resolve("src", "Honua.Server", "Migrations", "207_CreateFeatureEditLocks.sql");
            var sql = (await File.ReadAllTextAsync(path)).Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            await using var first = CreateHost(schema);
            await using var second = CreateHost(schema);
            await using var firstScope = first.CreateAsyncScope();
            await using var secondScope = second.CreateAsyncScope();
            await verify(firstScope.ServiceProvider.GetRequiredService<IFeatureLockService>(),
                secondScope.ServiceProvider.GetRequiredService<IFeatureLockService>(), schema);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private ServiceProvider CreateHost(string schema, string? tenantSchema = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString,
            ["Database:Schema"] = schema
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (tenantSchema is not null)
        {
            services.AddScoped<ISchemaContext>(_ => new TestSchemaContext(tenantSchema));
        }
        services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);
        services.TryAddSingleton<IFeatureLockService, InMemoryFeatureLockService>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
    private sealed record TestSchemaContext(string? CurrentSchema) : ISchemaContext;
}
