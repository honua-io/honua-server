// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Db.Postgres.Tests.Features.Observability;

[Collection("Database")]
public sealed class PostgresOpsIncidentEpisodeStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task ObserveAsync_ConcurrentActivationAndRedelivery_ResolvesOneDurableEpisode()
    {
        await WithStoresAsync(async (store, resolver) =>
        {
            var signal = Signal();
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ObserveAsync(signal)));
            results.Should().OnlyContain(item => item != null);
            results.Select(item => item!.EpisodeId).Distinct().Should().ContainSingle();
            var episode = results[0]!;
            episode.EpisodeId.Should().NotBe(Guid.Empty);
            episode.LifecycleVersion.Should().Be(1);
            var duplicate = await store.ObserveAsync(signal with { State = OpsIncidentEpisodeState.Recovered });
            duplicate.Should().BeEquivalentTo(episode);
            (await resolver.ResolveAsync(Reference(episode))).Should().BeEquivalentTo(episode);
        });
    }

    [IntegrationTest]
    public async Task ObserveAsync_UnknownThenRecoveryAndRecurrence_PreservesThenReplacesEpisode()
    {
        await WithStoresAsync(async (store, resolver) =>
        {
            var signal = Signal();
            var active = (await store.ObserveAsync(signal))!;
            var unknown = (await store.ObserveAsync(Next(signal, 1, OpsIncidentEpisodeState.Unknown)))!;
            unknown.EpisodeId.Should().Be(active.EpisodeId);
            unknown.LifecycleVersion.Should().Be(active.LifecycleVersion + 1);
            unknown.ClosedAt.Should().BeNull();
            (await resolver.ResolveAsync(Reference(unknown))).Should().BeNull();
            var recovered = (await store.ObserveAsync(Next(signal, 2, OpsIncidentEpisodeState.Recovered)))!;
            recovered.EpisodeId.Should().Be(active.EpisodeId);
            recovered.ClosedAt.Should().NotBeNull();
            (await resolver.ResolveAsync(Reference(recovered))).Should().BeNull();
            var recurrence = (await store.ObserveAsync(Next(signal, 3, OpsIncidentEpisodeState.Active)))!;
            recurrence.EpisodeId.Should().NotBe(active.EpisodeId);
            recurrence.LifecycleVersion.Should().Be(1);
            (await store.ReadAsync(new(active.AuthorityScope, active.EpisodeId))).Should().BeEquivalentTo(recovered);
            (await resolver.ResolveAsync(Reference(active))).Should().BeNull();
            (await resolver.ResolveAsync(Reference(recurrence))).Should().BeEquivalentTo(recurrence);
        });
    }

    [IntegrationTest]
    public async Task ObserveAsync_RepeatedActiveEvidence_RefreshesWithoutChangingLifecycleVersion()
    {
        await WithStoresAsync(async (store, resolver) =>
        {
            var signal = Signal();
            var active = (await store.ObserveAsync(signal))!;
            var refreshed = (await store.ObserveAsync(Next(signal, 1, OpsIncidentEpisodeState.Active)))!;
            refreshed.EpisodeId.Should().Be(active.EpisodeId);
            refreshed.LifecycleVersion.Should().Be(active.LifecycleVersion);
            refreshed.ObservedAt.Should().BeAfter(active.ObservedAt);
            (await resolver.ResolveAsync(Reference(active))).Should().BeEquivalentTo(refreshed);
        });
    }

    [IntegrationTest]
    public async Task ObserveAsync_RecoveryOrUnknownWithoutActivation_DoesNotMintIncidentIdentity()
    {
        await WithStoresAsync(async (store, _) =>
        {
            var signal = Signal() with { State = OpsIncidentEpisodeState.Unknown };
            (await store.ObserveAsync(signal)).Should().BeNull();
            (await store.ObserveAsync(Next(signal, 1, OpsIncidentEpisodeState.Recovered))).Should().BeNull();
            (await store.ObserveAsync(Next(signal, 2, OpsIncidentEpisodeState.Active))).Should().NotBeNull();
        });
    }

    [IntegrationTest]
    public async Task ObserveAsync_LateEvidence_CannotReopenOrRefreshClosedIncident()
    {
        await WithStoresAsync(async (store, resolver) =>
        {
            var signal = Signal();
            await store.ObserveAsync(signal);
            var recovered = (await store.ObserveAsync(Next(signal, 3, OpsIncidentEpisodeState.Recovered)))!;
            (await store.ObserveAsync(Next(signal, 2, OpsIncidentEpisodeState.Active))).Should().BeEquivalentTo(recovered);
            (await store.ObserveAsync(Next(signal, 1, OpsIncidentEpisodeState.Unknown))).Should().BeEquivalentTo(recovered);
            (await resolver.ResolveAsync(Reference(recovered))).Should().BeNull();
        });
    }

    [IntegrationTest]
    public async Task ResolveAsync_WrongAuthorityVersionOrStaleEvidence_FailsClosed()
    {
        await WithStoresAsync(async (store, resolver) =>
        {
            var active = (await store.ObserveAsync(Signal()))!;
            var reference = Reference(active);
            (await resolver.ResolveAsync(reference with { AuthorityScope = "other" })).Should().BeNull();
            (await resolver.ResolveAsync(reference with { LifecycleVersion = 2 })).Should().BeNull();
            (await resolver.ResolveAsync(reference with { EpisodeId = Guid.NewGuid() })).Should().BeNull();
            (await store.ReadAsync(new("other", active.EpisodeId))).Should().BeNull();
            var stale = (await store.ObserveAsync(Signal() with
            {
                FindingId = "stale-finding", ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            }))!;
            (await resolver.ResolveAsync(Reference(stale))).Should().BeNull();
        });
    }

    [IntegrationTest]
    public async Task ObserveAsync_InvalidSignal_RejectsUnscopedAndFutureEvidence()
    {
        await WithStoresAsync(async (store, _) =>
        {
            var valid = Signal();
            foreach (var signal in new[]
            {
                valid with { AuthorityScope = " " }, valid with { FindingId = "" },
                valid with { ObservationId = "" }, valid with { AuthorityScope = new string('é', 129) },
                valid with { State = (OpsIncidentEpisodeState)99 },
                valid with { ObservedAt = DateTimeOffset.UtcNow.AddHours(1) }
            })
            {
                Func<Task> observe = () => store.ObserveAsync(signal);
                await observe.Should().ThrowAsync<ArgumentException>();
            }
        });
    }

    private async Task WithStoresAsync(Func<IOpsIncidentEpisodeStore, IOpsIncidentEpisodeResolver, Task> action)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsIncidentEpisodeStoreTests));
        try
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString,
                ["Database:Schema"] = schema
            }).Build();
            services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);
            services.RemoveAll<IAdoNetDatabaseConnectionProvider>();
            services.AddSingleton<IAdoNetDatabaseConnectionProvider>(new TestDatabaseConnectionProvider(fixture.DataSource, () => schema));
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetService<IOpsIncidentEpisodeStore>();
            store.Should().NotBeNull("configured PostgreSQL must persist verified incident episodes");
            await ApplyMigrationAsync(schema);
            await using var restartedProvider = services.BuildServiceProvider();
            await using var restartedScope = restartedProvider.CreateAsyncScope();
            var restarted = restartedScope.ServiceProvider.GetRequiredService<IOpsIncidentEpisodeResolver>();
            await action(store!, restarted);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "172_CreateOpsIncidentEpisodes.sql"));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal);
        await command.ExecuteNonQueryAsync();
    }

    private static OpsIncidentEpisodeReference Reference(OpsIncidentEpisode episode) =>
        new(episode.AuthorityScope, episode.EpisodeId, episode.LifecycleVersion);

    private static OpsIncidentEpisodeSignal Signal() => new()
    {
        AuthorityScope = "platform", FindingId = "finding-one", ObservationId = "observation-zero",
        ObservedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()),
        State = OpsIncidentEpisodeState.Active
    };

    private static OpsIncidentEpisodeSignal Next(OpsIncidentEpisodeSignal signal, int index, OpsIncidentEpisodeState state) =>
        signal with { ObservationId = $"observation-{index}", ObservedAt = signal.ObservedAt.AddSeconds(index), State = state };
}
