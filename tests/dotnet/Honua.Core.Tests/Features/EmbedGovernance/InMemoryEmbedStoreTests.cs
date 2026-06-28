// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.EmbedGovernance;
using Honua.Core.Features.EmbedGovernance.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.EmbedGovernance;

/// <summary>
/// Unit tests for the in-memory embed key and analytics stores: lifecycle,
/// validation, rate accounting, and usage aggregation.
/// </summary>
public sealed class InMemoryEmbedStoreTests
{
    private static EmbedKeyScope Scope() => new()
    {
        AllowedEmbedOrigins = ["https://app.example.com"],
        IntegrationId = "site-7",
        RateLimitRequestsPerWindow = 3,
        RateLimitWindow = TimeSpan.FromMinutes(1),
    };

    [UnitTest]
    public async Task CreateAndValidate_RoundTripsKeyMaterial()
    {
        var store = new InMemoryEmbedKeyStore();

        var created = await store.CreateAsync("integration", Scope(), expiresAt: null, createdBy: "op", CancellationToken.None);

        created.Key.Should().StartWith(EmbedKeyMaterial.Prefix);
        created.Record.KeyPrefix.Should().StartWith(EmbedKeyMaterial.Prefix);

        var validated = await store.ValidateAsync(created.Key, CancellationToken.None);
        validated.Should().NotBeNull();
        validated!.Record.Id.Should().Be(created.Record.Id);
        validated.Record.LastUsedAt.Should().NotBeNull();
    }

    [UnitTest]
    public async Task Validate_WrongKey_ReturnsNull()
    {
        var store = new InMemoryEmbedKeyStore();
        await store.CreateAsync("integration", Scope(), null, null, CancellationToken.None);

        var validated = await store.ValidateAsync($"{EmbedKeyMaterial.Prefix}not-a-real-key", CancellationToken.None);

        validated.Should().BeNull();
    }

    [UnitTest]
    public async Task Revoke_PreventsValidation()
    {
        var store = new InMemoryEmbedKeyStore();
        var created = await store.CreateAsync("integration", Scope(), null, null, CancellationToken.None);

        await store.RevokeAsync(created.Record.Id, CancellationToken.None);

        var validated = await store.ValidateAsync(created.Key, CancellationToken.None);
        validated.Should().BeNull();
    }

    [UnitTest]
    public async Task Rotate_InvalidatesOldSecretAndIssuesNew()
    {
        var store = new InMemoryEmbedKeyStore();
        var created = await store.CreateAsync("integration", Scope(), null, null, CancellationToken.None);

        var rotated = await store.RotateAsync(created.Record.Id, CancellationToken.None);

        rotated.Should().NotBeNull();
        rotated!.Key.Should().NotBe(created.Key);

        (await store.ValidateAsync(created.Key, CancellationToken.None)).Should().BeNull();
        (await store.ValidateAsync(rotated.Key, CancellationToken.None)).Should().NotBeNull();
    }

    [UnitTest]
    public async Task RecordRequest_CountsWithinWindowAndResetsAfter()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 26, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryEmbedKeyStore(clock);
        var created = await store.CreateAsync("integration", Scope(), null, null, CancellationToken.None);
        var id = created.Record.Id;
        var window = TimeSpan.FromMinutes(1);

        (await store.RecordRequestAsync(id, window, CancellationToken.None)).Should().Be(1);
        (await store.RecordRequestAsync(id, window, CancellationToken.None)).Should().Be(2);
        (await store.RecordRequestAsync(id, window, CancellationToken.None)).Should().Be(3);

        clock.Advance(TimeSpan.FromMinutes(2));

        (await store.RecordRequestAsync(id, window, CancellationToken.None)).Should().Be(1);
    }

    [UnitTest]
    public async Task AnalyticsStore_AggregatesByDimensionAndFilters()
    {
        var store = new InMemoryEmbedAnalyticsStore();
        var now = new DateTimeOffset(2026, 6, 26, 0, 0, 0, TimeSpan.Zero);

        await store.IngestAsync(Event(EmbedAnalyticsEventType.View, "site-a", "https://a.test", now), CancellationToken.None);
        await store.IngestAsync(Event(EmbedAnalyticsEventType.View, "site-a", "https://a.test", now), CancellationToken.None);
        await store.IngestAsync(Event(EmbedAnalyticsEventType.Search, "site-b", "https://b.test", now), CancellationToken.None);

        var byEventType = await store.QueryAsync(new EmbedUsageQuery { GroupBy = EmbedUsageDimension.EventType }, CancellationToken.None);
        byEventType.Total.Should().Be(3);
        byEventType.Aggregates.Should().Contain(a => a.Key == "View" && a.Count == 2);
        byEventType.Aggregates.Should().Contain(a => a.Key == "Search" && a.Count == 1);

        var filtered = await store.QueryAsync(
            new EmbedUsageQuery { GroupBy = EmbedUsageDimension.Integration, IntegrationId = "site-a" },
            CancellationToken.None);
        filtered.Total.Should().Be(2);
        filtered.Aggregates.Should().ContainSingle().Which.Key.Should().Be("site-a");
    }

    private static EmbedAnalyticsEvent Event(EmbedAnalyticsEventType type, string integration, string origin, DateTimeOffset at) => new()
    {
        EventType = type,
        IntegrationId = integration,
        Origin = origin,
        OccurredAt = at,
    };

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
