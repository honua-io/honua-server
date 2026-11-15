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
public sealed partial class PostgresOpsIncidentObservationStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task AppendAsync_ConcurrentRedeliveryAndNewInstance_PreservesOriginalEvidence()
    {
        await WithStoresAsync(async (store, restarted) =>
        {
            var original = Observation("authority-a", "one");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.AppendAsync(original)));
            var canonical = await store.AppendAsync(original with
            {
                State = OpsIncidentObservationState.Recovered,
                EvidenceJson = "{\"replacement\":true}",
                ObservedAt = original.ObservedAt.AddMinutes(1)
            });
            canonical.Should().BeEquivalentTo(original);
            var page = await restarted.ReadAsync(new("authority-a", original.ObservedAt.AddMinutes(-1)));
            page.Observations.Should().ContainSingle().Which.Should().BeEquivalentTo(original);
            page.Truncated.Should().BeFalse();
        });
    }

    [IntegrationTest]
    public async Task ReadAsync_SameSourceIdentityInDifferentAuthorities_RemainsIsolated()
    {
        await WithStoreAsync(async store =>
        {
            await store.AppendAsync(Observation("authority-a", "one"));
            await store.AppendAsync(Observation("authority-b", "one"));
            var page = await store.ReadAsync(new("authority-a", DateTimeOffset.UnixEpoch));
            page.Observations.Should().ContainSingle().Which.AuthorityScope.Should().Be("authority-a");
            (await store.ReadAsync(new("authority-c", DateTimeOffset.UnixEpoch))).Observations.Should().BeEmpty();
        });
    }

    [IntegrationTest]
    public async Task ReadAsync_LimitAndSince_AreBoundedAndReportTruncation()
    {
        await WithStoreAsync(async store =>
        {
            var first = Observation("authority-a", "one");
            await store.AppendAsync(first);
            await store.AppendAsync(first with { ObservationId = "two", ObservedAt = first.ObservedAt.AddSeconds(1) });
            var page = await store.ReadAsync(new("authority-a", DateTimeOffset.UnixEpoch, 1));
            page.Observations.Should().ContainSingle().Which.ObservationId.Should().Be("two");
            page.Truncated.Should().BeTrue();
            var recent = await store.ReadAsync(new("authority-a", first.ObservedAt.AddMilliseconds(1), 1));
            recent.Observations.Should().ContainSingle();
            recent.Truncated.Should().BeFalse();
        });
    }

    [IntegrationTest]
    public async Task ReadAsync_TruncatedHistory_ReportsFullScopedCount()
    {
        await WithStoreAsync(async store =>
        {
            var original = Observation("authority-a", "one");
            await store.AppendAsync(original);
            await store.AppendAsync(original with { ObservationId = "two", State = OpsIncidentObservationState.Unknown });
            await store.AppendAsync(original with { ObservationId = "three", State = OpsIncidentObservationState.Recovered });
            await store.AppendAsync(original with { AuthorityScope = "authority-b", ObservationId = "four" });
            var page = await store.ReadAsync(new("authority-a", DateTimeOffset.UnixEpoch, 1));
            page.Observations.Should().ContainSingle();
            page.TotalCount.Should().Be(3);
            page.Truncated.Should().BeTrue();
            var missing = await store.ReadAsync(new("authority-c", DateTimeOffset.UnixEpoch));
            missing.TotalCount.Should().Be(0);
            missing.Truncated.Should().BeFalse();
        });
    }

    [IntegrationTest]
    public async Task ReadAsync_FindingAndTimeWindow_CountsOnlyMatchingEvidence()
    {
        await WithStoreAsync(async store =>
        {
            var original = Observation("authority-a", "one");
            await store.AppendAsync(original);
            await store.AppendAsync(original with { ObservationId = "two", ObservedAt = original.ObservedAt.AddSeconds(1) });
            await store.AppendAsync(original with { ObservationId = "three", FindingId = "finding-two" });
            var finding = await store.ReadAsync(new("authority-a", DateTimeOffset.UnixEpoch, 1) { FindingId = "finding-one" });
            finding.TotalCount.Should().Be(2);
            finding.Observations.Should().OnlyContain(item => item.FindingId == "finding-one");
            var recent = await store.ReadAsync(new("authority-a", original.ObservedAt.AddMilliseconds(1), 1) { FindingId = "finding-one" });
            recent.TotalCount.Should().Be(1);
            recent.Truncated.Should().BeFalse();
            var absent = await store.ReadAsync(new("authority-a", DateTimeOffset.UnixEpoch) { FindingId = "absent" });
            absent.TotalCount.Should().Be(0);
            absent.Observations.Should().BeEmpty();
        });
    }

    [IntegrationTest]
    public async Task ReadAsync_ClosedIncidentWindow_ExcludesRecurrenceFromEvidenceAndCount()
    {
        await WithStoreAsync(async store =>
        {
            var original = Observation("authority-a", "active");
            var recoveredAt = original.ObservedAt.AddSeconds(1);
            await store.AppendAsync(original);
            await store.AppendAsync(original with
            {
                ObservationId = "recovered", ObservedAt = recoveredAt,
                State = OpsIncidentObservationState.Recovered
            });
            await store.AppendAsync(original with { ObservationId = "recurrence", ObservedAt = recoveredAt.AddSeconds(1) });
            var page = await store.ReadAsync(new("authority-a", original.ObservedAt, 1)
            {
                FindingId = original.FindingId, Until = recoveredAt
            });
            page.TotalCount.Should().Be(2);
            page.Truncated.Should().BeTrue();
            page.Observations.Should().ContainSingle().Which.ObservationId.Should().Be("recovered");
            var boundary = await store.ReadAsync(new("authority-a", recoveredAt) { Until = recoveredAt });
            boundary.TotalCount.Should().Be(1);
            boundary.Observations.Should().ContainSingle().Which.ObservationId.Should().Be("recovered");
        });
    }

    [IntegrationTest]
    public async Task AppendAsync_RecoveryAndUnknown_AreRetainedAsSeparateObservations()
    {
        await WithStoreAsync(async store =>
        {
            var active = Observation("authority-a", "one");
            await store.AppendAsync(active);
            await store.AppendAsync(active with { ObservationId = "two", State = OpsIncidentObservationState.Recovered });
            await store.AppendAsync(active with { ObservationId = "three", State = OpsIncidentObservationState.Unknown });
            var page = await store.ReadAsync(new("authority-a", DateTimeOffset.UnixEpoch));
            page.Observations.Select(item => item.State).Should().BeEquivalentTo(
                new[] { OpsIncidentObservationState.Active, OpsIncidentObservationState.Recovered, OpsIncidentObservationState.Unknown });
        });
    }

    [IntegrationTest]
    public async Task ReadAsync_InvalidAuthorityOrLimit_RejectsUnscopedOrUnboundedReads()
    {
        await WithStoreAsync(async store =>
        {
            foreach (var query in new OpsIncidentObservationQuery[]
            {
                new("", DateTimeOffset.UnixEpoch), new(new string('a', 257), DateTimeOffset.UnixEpoch), new(" ", DateTimeOffset.UnixEpoch),
                new("authority-a", DateTimeOffset.UnixEpoch, 0), new("authority-a", DateTimeOffset.UnixEpoch, 1001),
                new("authority-a", DateTimeOffset.UnixEpoch) { FindingId = " " },
                new("authority-a", DateTimeOffset.UnixEpoch) { FindingId = new string('é', 129) },
                new("authority-a", DateTimeOffset.UnixEpoch) { Until = DateTimeOffset.UnixEpoch.AddTicks(-1) }
            })
            {
                Func<Task> read = () => store.ReadAsync(query);
                await read.Should().ThrowAsync<ArgumentException>();
            }
        });
    }

    [IntegrationTest]
    public async Task AppendAsync_InvalidIdentityStateOrEvidence_RejectsInvalidObservations()
    {
        await WithStoreAsync(async store =>
        {
            var valid = Observation("authority-a", "one");
            foreach (var observation in new[]
            {
                valid with { AuthorityScope = " " }, valid with { SourceId = "" },
                valid with { AuthorityScope = new string('a', 257) },
                valid with { SourceId = new string('é', 129) },
                valid with { ObservationId = " " }, valid with { FindingId = "" },
                valid with { State = (OpsIncidentObservationState)99 },
                valid with { EvidenceJson = "broken-json" },
                valid with { EvidenceJson = "\"" + new string('a', 65536) + "\"" }
            })
            {
                Func<Task> append = () => store.AppendAsync(observation);
                await append.Should().ThrowAsync<ArgumentException>();
            }
            (await store.ReadAsync(new("authority-a", DateTimeOffset.UnixEpoch))).Observations.Should().BeEmpty();
        });
    }

    private Task WithStoreAsync(Func<IOpsIncidentObservationStore, Task> action) =>
        WithStoresAsync((store, _) => action(store));

    private async Task WithStoresAsync(Func<IOpsIncidentObservationStore, IOpsIncidentObservationStore, Task> action)
        => await WithProvidersAsync(async (provider, restarted) =>
            await action(provider.GetRequiredService<IOpsIncidentObservationStore>(),
                restarted.GetRequiredService<IOpsIncidentObservationStore>()));

    private async Task WithProvidersAsync(Func<IServiceProvider, IServiceProvider, Task> action)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsIncidentObservationStoreTests));
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
            var store = scope.ServiceProvider.GetService<IOpsIncidentObservationStore>();
            store.Should().NotBeNull("configured PostgreSQL must retain operational observations without an inbox read");
            await ApplyMigrationAsync(schema, "169_CreateOpsIncidentObservations.sql");
            await ApplyMigrationAsync(schema, "172_CreateOpsIncidentEpisodes.sql");
            await using var restartedProvider = services.BuildServiceProvider();
            await using var restartedScope = restartedProvider.CreateAsyncScope();
            await action(scope.ServiceProvider, restartedScope.ServiceProvider);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema, string migration)
    {
        var sql = await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "Migrations", migration));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal);
        await command.ExecuteNonQueryAsync();
    }

    private static OpsIncidentObservation Observation(string authority, string id) => new()
    {
        AuthorityScope = authority,
        SourceId = "ops-findings",
        ObservationId = id,
        FindingId = "finding-one",
        ObservedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        State = OpsIncidentObservationState.Active,
        EvidenceJson = "{\"original\":true}"
    };
}
