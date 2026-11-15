// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Db.Postgres;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
[Collection("Database")]
public sealed class OpsAlarmIncidentIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private string _schema = null!;
    private readonly DateTimeOffset _start = DateTimeOffset.FromUnixTimeMilliseconds(
        DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        _schema = await _postgres.CreateIsolatedSchemaAsync(nameof(OpsAlarmIncidentIntegrationTests));
        foreach (var migration in new[] { "161_CreateOpsExternalEvents.sql", "169_CreateOpsIncidentObservations.sql", "172_CreateOpsIncidentEpisodes.sql" })
        {
            var sql = await File.ReadAllTextAsync(MigrationPath(migration));
            await using var command = _postgres.DataSource.CreateCommand(
                sql.Replace("$HonuaSchema$", $"\"{_schema}\"", StringComparison.Ordinal));
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _postgres.DropSchemaAsync(_schema);
        await _postgres.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProcessAsync_AlarmUnknownRecoveryAndRecurrence_PersistsHistoryAndDistinctEpisodes()
    {
        var queue = new RecordingQueue();
        await using (var provider = CreateProvider())
        {
            using var ingestion = CreateIngestion(provider, queue);
            await ingestion.ProcessAsync(new("active", Envelope(0, "ALARM")), CancellationToken.None);
            await ingestion.ProcessAsync(new("duplicate", Envelope(0, "OK")), CancellationToken.None);
            await ingestion.ProcessAsync(new("unknown", Envelope(1, "INSUFFICIENT_DATA")), CancellationToken.None);
            var open = await ReadObservationsAsync(provider);
            open.Observations.Should().HaveCount(2);
            open.Observations.Should().ContainSingle(o => o.State == OpsIncidentObservationState.Unknown);
            open.Observations.Select(o => o.FindingId).Distinct().Should().ContainSingle();
            (await ReadEpisodesAsync()).Should().ContainSingle().Which.State.Should().Be(OpsIncidentEpisodeState.Unknown);
            await ingestion.ProcessAsync(new("recovered", Envelope(2, "OK")), CancellationToken.None);
        }

        await using var restarted = CreateProvider();
        using var nextIngestion = CreateIngestion(restarted, queue);
        await nextIngestion.ProcessAsync(new("recurrence", Envelope(3, "ALARM")), CancellationToken.None);
        var history = await ReadObservationsAsync(restarted);
        history.Observations.Should().HaveCount(4).And.OnlyContain(o => o.AuthorityScope == "platform");
        history.Observations.Select(o => o.FindingId).Distinct().Should().ContainSingle();
        history.Observations.Should().ContainSingle(o => o.State == OpsIncidentObservationState.Recovered);
        var episodes = await ReadEpisodesAsync();
        episodes.Should().HaveCount(2);
        episodes[0].State.Should().Be(OpsIncidentEpisodeState.Recovered);
        episodes[0].ClosedAt.Should().NotBeNull();
        episodes[1].State.Should().Be(OpsIncidentEpisodeState.Active);
        episodes[1].Id.Should().NotBe(episodes[0].Id);
        queue.Deleted.Should().Equal("active", "duplicate", "unknown", "recovered", "recurrence");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProcessAsync_LateActivationAfterRecovery_RetainsEvidenceWithoutReopeningIncident()
    {
        await using var provider = CreateProvider();
        using var ingestion = CreateIngestion(provider, new RecordingQueue());
        await ingestion.ProcessAsync(new("recovery", Envelope(2, "OK")), CancellationToken.None);
        await ingestion.ProcessAsync(new("late", Envelope(0, "ALARM")), CancellationToken.None);
        (await ReadObservationsAsync(provider)).Observations.Should().HaveCount(2);
        (await ReadEpisodesAsync()).Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProcessAsync_AcknowledgementFails_RetryPreservesOneObservationAndEpisode()
    {
        var queue = new RecordingQueue { FailuresRemaining = 1 };
        await using var provider = CreateProvider();
        using var ingestion = CreateIngestion(provider, queue);
        var message = new OpsEventQueueMessage("retry", Envelope(0, "ALARM"));
        var firstAttempt = () => ingestion.ProcessAsync(message, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<IOException>();
        queue.Deleted.Should().BeEmpty();
        await ingestion.ProcessAsync(message, CancellationToken.None);
        (await ReadObservationsAsync(provider)).Observations.Should().ContainSingle();
        (await ReadEpisodesAsync()).Should().ContainSingle().Which.State.Should().Be(OpsIncidentEpisodeState.Active);
        queue.Deleted.Should().Equal("retry");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProcessAsync_EpisodeWriteFails_ReplayCompletesThePersistedObservation()
    {
        var queue = new RecordingQueue();
        await using var provider = CreateProvider(EpisodeStoreMode.FailFirstWrite);
        using var ingestion = CreateIngestion(provider, queue);
        var message = new OpsEventQueueMessage("retry", Envelope(0, "ALARM"));
        var firstAttempt = () => ingestion.ProcessAsync(message, CancellationToken.None);

        await firstAttempt.Should().ThrowAsync<IOException>();
        (await ReadObservationsAsync(provider)).Observations.Should().ContainSingle();
        (await ReadEpisodesAsync()).Should().BeEmpty();
        queue.Deleted.Should().BeEmpty();

        await ingestion.ProcessAsync(message with { Body = Envelope(0, "OK") }, CancellationToken.None);

        (await ReadObservationsAsync(provider)).Observations.Should().ContainSingle()
            .Which.State.Should().Be(OpsIncidentObservationState.Active);
        (await ReadEpisodesAsync()).Should().ContainSingle().Which.State.Should().Be(OpsIncidentEpisodeState.Active);
        queue.Deleted.Should().Equal("retry");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProcessAsync_TwoHostsReceiveSameEvent_StoresOneObservationAndEpisode()
    {
        await using var firstProvider = CreateProvider();
        await using var secondProvider = CreateProvider();
        var firstQueue = new RecordingQueue();
        var secondQueue = new RecordingQueue();
        using var first = CreateIngestion(firstProvider, firstQueue);
        using var second = CreateIngestion(secondProvider, secondQueue);
        await Task.WhenAll(
            first.ProcessAsync(new("first", Envelope(0, "ALARM")), CancellationToken.None),
            second.ProcessAsync(new("second", Envelope(0, "ALARM")), CancellationToken.None));

        (await ReadObservationsAsync(firstProvider)).Observations.Should().ContainSingle();
        (await ReadEpisodesAsync()).Should().ContainSingle().Which.State.Should().Be(OpsIncidentEpisodeState.Active);
        firstQueue.Deleted.Should().Equal("first");
        secondQueue.Deleted.Should().Equal("second");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProcessAsync_AlarmHasNoResource_RejectsWithoutAcknowledgingOrInventingIdentity()
    {
        var queue = new RecordingQueue();
        await using var provider = CreateProvider();
        using var ingestion = CreateIngestion(provider, queue);
        var malformed = Envelope(0, "ALARM").Replace(
            "\"arn:aws:cloudwatch:eu-north-1:000000000000:alarm:server-health\"", "", StringComparison.Ordinal);
        var process = () => ingestion.ProcessAsync(new("malformed", malformed), CancellationToken.None);
        await process.Should().ThrowAsync<JsonException>();
        (await ReadObservationsAsync(provider)).Observations.Should().BeEmpty();
        (await ReadEpisodesAsync()).Should().BeEmpty();
        queue.Deleted.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ProcessAsync_UnrelatedEvent_AcknowledgesWithoutChangingAlarmIncidents()
    {
        var queue = new RecordingQueue();
        await using var provider = CreateProvider();
        using var ingestion = CreateIngestion(provider, queue);
        var unrelated = Envelope(0, "ALARM").Replace("aws.cloudwatch", "aws.ecs", StringComparison.Ordinal);
        await ingestion.ProcessAsync(new("unrelated", unrelated), CancellationToken.None);
        (await ReadObservationsAsync(provider)).Observations.Should().BeEmpty();
        (await ReadEpisodesAsync()).Should().BeEmpty();
        queue.Deleted.Should().Equal("unrelated");
    }

    [IntegrationTheory]
    [Operation(Operations.TestInfrastructure)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task ProcessAsync_SubsecondTransitions_OrderByAlarmStateTimestamp(int first, int second)
    {
        await using var provider = CreateProvider();
        using var ingestion = CreateIngestion(provider, new RecordingQueue());
        foreach (var index in new[] { first, second })
        {
            var envelope = JsonNode.Parse(Envelope(index, index == 0 ? "ALARM" : "OK"))!;
            envelope["time"] = _start.ToString("O", CultureInfo.InvariantCulture);
            envelope["detail"]!["state"]!["timestamp"] = _start.AddMilliseconds(index * 100)
                .ToString("O", CultureInfo.InvariantCulture).Replace("+00:00", "+0000", StringComparison.Ordinal);
            await ingestion.ProcessAsync(new($"receipt-{index}", envelope.ToJsonString()), CancellationToken.None);
        }
        var observations = (await ReadObservationsAsync(provider)).Observations;
        observations.Should().HaveCount(2);
        observations.Select(o => o.ObservedAt).Distinct().Should().HaveCount(2);
        (await ReadEpisodesAsync()).Should().HaveCount(first == 0 ? 1 : 0)
            .And.OnlyContain(e => e.State == OpsIncidentEpisodeState.Recovered);
    }

    [IntegrationTheory]
    [Operation(Operations.TestInfrastructure)]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("2026-11-15T06:00:00")]
    public async Task ProcessAsync_InvalidStateTimestamp_DoesNotAcknowledge(string? timestamp)
    {
        var queue = new RecordingQueue();
        await using var provider = CreateProvider();
        using var ingestion = CreateIngestion(provider, queue);
        var envelope = JsonNode.Parse(Envelope(0, "ALARM"))!;
        envelope["detail"]!["state"]!["timestamp"] = timestamp;
        var process = () => ingestion.ProcessAsync(new("invalid-time", envelope.ToJsonString()), CancellationToken.None);
        await process.Should().ThrowAsync<JsonException>();
        (await ReadObservationsAsync(provider)).Observations.Should().BeEmpty();
        queue.Deleted.Should().BeEmpty();
    }

    private ServiceProvider CreateProvider(EpisodeStoreMode episodeStoreMode = EpisodeStoreMode.Standard)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _postgres.ConnectionString,
            ["Database:Schema"] = _schema
        }).Build();
        var services = new ServiceCollection();
        services.AddPostgreSqlServices(configuration, ServerCoreSchemaMigrations.Manifest);
        services.RemoveAll<IAdoNetDatabaseConnectionProvider>();
        services.AddSingleton<IAdoNetDatabaseConnectionProvider>(new TestDatabaseConnectionProvider(_postgres.DataSource, () => _schema));
        services.AddObservability(configuration);
        if (episodeStoreMode == EpisodeStoreMode.FailFirstWrite)
        {
            var episodeStore = services.Last(descriptor => descriptor.ServiceType == typeof(IOpsIncidentEpisodeStore));
            services.Remove(episodeStore);
            services.AddSingleton(new EpisodeWriteFailure());
            services.AddScoped<IOpsIncidentEpisodeStore>(provider => new FailingEpisodeStore(
                (IOpsIncidentEpisodeStore)episodeStore.ImplementationFactory!(provider),
                provider.GetRequiredService<EpisodeWriteFailure>()));
        }
        return services.BuildServiceProvider();
    }

    private enum EpisodeStoreMode { Standard, FailFirstWrite }

    private sealed class EpisodeWriteFailure
    {
        public bool Pending { get; set; } = true;
    }

    private sealed class FailingEpisodeStore(IOpsIncidentEpisodeStore inner, EpisodeWriteFailure failure) : IOpsIncidentEpisodeStore
    {
        public Task<OpsIncidentEpisode?> ObserveAsync(OpsIncidentEpisodeSignal signal, CancellationToken cancellationToken = default)
        {
            if (failure.Pending)
            {
                failure.Pending = false;
                throw new IOException("Episode store unavailable.");
            }
            return inner.ObserveAsync(signal, cancellationToken);
        }

        public Task<OpsIncidentEpisode?> ReadAsync(OpsIncidentEpisodeIdentity identity, CancellationToken cancellationToken = default)
            => inner.ReadAsync(identity, cancellationToken);
    }

    private async Task<OpsIncidentObservationPage> ReadObservationsAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOpsIncidentObservationStore>()
            .ReadAsync(new("platform", _start.AddMinutes(-1)));
    }

    private sealed record EpisodeRow(Guid Id, OpsIncidentEpisodeState State, DateTimeOffset? ClosedAt);

    private async Task<List<EpisodeRow>> ReadEpisodesAsync()
    {
        await using var command = _postgres.DataSource.CreateCommand(
            $"SELECT episode_id, state, closed_at FROM \"{_schema}\".ops_incident_episodes ORDER BY started_at");
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<EpisodeRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new(reader.GetGuid(0), (OpsIncidentEpisodeState)reader.GetInt16(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
        }
        return rows;
    }

    private string Envelope(int index, string state) =>
        $$$$"""{"id":"event-{{{{index}}}}","source":"aws.cloudwatch","detail-type":"CloudWatch Alarm State Change","time":"{{{{_start.AddSeconds(index).ToString("O", CultureInfo.InvariantCulture)}}}}","resources":["arn:aws:cloudwatch:eu-north-1:000000000000:alarm:server-health"],"detail":{"authorityScope":"untrusted","state":{"value":"{{{{state}}}}","timestamp":"{{{{_start.AddSeconds(index).ToString("O", CultureInfo.InvariantCulture)}}}}"}}} """;

    private static OpsEventIngestionService CreateIngestion(ServiceProvider provider, RecordingQueue queue) =>
        new(queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<OpsEventIngestionService>.Instance);

    private sealed class RecordingQueue : IOpsEventQueueClient
    {
        internal List<string> Deleted { get; } = [];
        internal int FailuresRemaining { get; set; }
        public Task<IReadOnlyList<OpsEventQueueMessage>> ReceiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OpsEventQueueMessage>>([]);
        public Task DeleteAsync(string receiptHandle, CancellationToken cancellationToken)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new IOException("Queue acknowledgement failed.");
            }
            Deleted.Add(receiptHandle);
            return Task.CompletedTask;
        }
    }

    private static string MigrationPath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }
        return Path.Join(directory!.FullName, "src", "Honua.Server", "Migrations", name);
    }
}
