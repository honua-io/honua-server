// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Db.Postgres.Tests.Features.Observability;

public sealed partial class PostgresOpsIncidentObservationStoreTests
{
    [IntegrationTest]
    public async Task ReadContextAsync_RestartAndRecurrence_RetainsClosedEpisodeAndScopedEvidence()
    {
        await WithProvidersAsync(async (provider, restarted) =>
        {
            var original = Observation("authority-a", "active");
            var active = await AppendSignalAsync(provider, original);
            var recovered = await AppendSignalAsync(provider, original with
            {
                ObservationId = "recovered",
                ObservedAt = original.ObservedAt.AddSeconds(1),
                State = OpsIncidentObservationState.Recovered
            });
            await AppendSignalAsync(provider, original with { ObservationId = "recurrence", ObservedAt = original.ObservedAt.AddSeconds(2) });
            await AppendSignalAsync(provider, original with { AuthorityScope = "authority-b" });
            await AppendSignalAsync(provider, original with { FindingId = "other-finding" });
            var reader = restarted.GetRequiredService<IOpsIncidentContextReader>();
            var context = await reader.ReadAsync(new(new("authority-a", active!.EpisodeId)));
            context.Should().NotBeNull();
            context!.Episode.Should().BeEquivalentTo(recovered);
            context.EvidenceAvailability.Should().Be(OpsIncidentEvidenceAvailability.Available);
            context.Evidence.TotalCount.Should().Be(2);
            context.Evidence.Observations.Select(item => item.ObservationId).Should().BeEquivalentTo("active", "recovered");
            (await reader.ReadAsync(new(new("authority-b", active.EpisodeId)))).Should().BeNull();
            (await reader.ReadAsync(new(new("authority-a", Guid.NewGuid())))).Should().BeNull();
        });
    }

    [IntegrationTest]
    public async Task ReadContextAsync_TruncatedEvidence_RetainsFullCountAndUnknownState()
    {
        await WithProvidersAsync(async (provider, restarted) =>
        {
            var original = Observation("authority-a", "active");
            var active = await AppendSignalAsync(provider, original);
            await AppendSignalAsync(provider, original with
            {
                ObservationId = "unknown",
                ObservedAt = original.ObservedAt.AddSeconds(1),
                State = OpsIncidentObservationState.Unknown
            });
            var context = await restarted.GetRequiredService<IOpsIncidentContextReader>()
                .ReadAsync(new(new("authority-a", active!.EpisodeId), 1));
            context.Should().NotBeNull();
            context!.Episode.State.Should().Be(OpsIncidentEpisodeState.Unknown);
            context.Episode.ClosedAt.Should().BeNull();
            context.EvidenceAvailability.Should().Be(OpsIncidentEvidenceAvailability.Truncated);
            context.Evidence.TotalCount.Should().Be(2);
            context.Evidence.Observations.Should().ContainSingle().Which.State.Should().Be(OpsIncidentObservationState.Unknown);
        });
    }

    [IntegrationTest]
    public async Task ReadContextAsync_MissingEvidence_PreservesStaleRecordedStateWithoutClaimingRecovery()
    {
        await WithProvidersAsync(async (provider, restarted) =>
        {
            var signal = ToSignal(Observation("authority-a", "active") with { ObservedAt = DateTimeOffset.UtcNow.AddDays(-1) });
            var episode = await provider.GetRequiredService<IOpsIncidentEpisodeStore>().ObserveAsync(signal);
            var context = await restarted.GetRequiredService<IOpsIncidentContextReader>()
                .ReadAsync(new(new("authority-a", episode!.EpisodeId)));
            context.Should().NotBeNull();
            context!.Episode.Should().BeEquivalentTo(episode);
            context.Episode.State.Should().Be(OpsIncidentEpisodeState.Active);
            context.EvidenceAvailability.Should().Be(OpsIncidentEvidenceAvailability.Missing);
            context.Evidence.TotalCount.Should().Be(0);
            context.Evidence.Observations.Should().BeEmpty();
        });
    }

    [IntegrationTest]
    public async Task ReadContextAsync_InvalidLimitEvenForAbsentEpisode_RejectsUnboundedReads()
    {
        await WithProvidersAsync(async (_, restarted) =>
        {
            var reader = restarted.GetRequiredService<IOpsIncidentContextReader>();
            foreach (var limit in new[] { 0, 1001 })
            {
                Func<Task> read = () => reader.ReadAsync(new(new("authority-a", Guid.NewGuid()), limit));
                await read.Should().ThrowAsync<ArgumentOutOfRangeException>();
            }
        });
    }

    private static async Task<OpsIncidentEpisode?> AppendSignalAsync(
        IServiceProvider provider, OpsIncidentObservation observation)
    {
        var canonical = await provider.GetRequiredService<IOpsIncidentObservationStore>().AppendAsync(observation);
        return await provider.GetRequiredService<IOpsIncidentEpisodeStore>().ObserveAsync(ToSignal(canonical));
    }

    private static OpsIncidentEpisodeSignal ToSignal(OpsIncidentObservation observation) => new()
    {
        AuthorityScope = observation.AuthorityScope,
        FindingId = observation.FindingId,
        ObservationId = observation.ObservationId,
        ObservedAt = observation.ObservedAt,
        State = observation.State switch
        {
            OpsIncidentObservationState.Active => OpsIncidentEpisodeState.Active,
            OpsIncidentObservationState.Recovered => OpsIncidentEpisodeState.Recovered,
            _ => OpsIncidentEpisodeState.Unknown
        }
    };
}
