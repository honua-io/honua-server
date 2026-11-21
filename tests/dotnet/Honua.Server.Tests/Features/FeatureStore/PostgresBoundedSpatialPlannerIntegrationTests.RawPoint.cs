// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Server.Tests.Features.FeatureStore;

// OGC items and GeoServices point pages read through the raw-point reader, which
// must take the same scoped serial plan as the ordinary feature page.
public sealed partial class PostgresBoundedSpatialPlannerIntegrationTests
{
    [IntegrationTest]
    public async Task RawPointPage_EligibleBbox_AppliesScopedPlannerSetting()
    {
        var page = await CreateReader().QueryGeoServicesRawPointPageAsync(1, Bbox());
        page.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
        page.Items.Select(RawSetting).Should().OnlyContain(value => value == "0");
        page.Items[0].X.Should().Be(0);
        await AssertPoolSettingAsync();
    }

    [IntegrationTest]
    public async Task RawPointPage_OptedOutReader_RetainsOrdinaryPlannerSetting()
    {
        var page = await CreateReader(enabled: false).QueryGeoServicesRawPointPageAsync(1, Bbox());
        page.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
        page.Items.Select(RawSetting).Should().OnlyContain(value => value == "2");
        await AssertPoolSettingAsync();
    }

    [IntegrationTest]
    public async Task RawPointPage_FirstPageProbe_PreservesHasMoreAndRequestedPageSize()
    {
        var query = Bbox() with { Limit = 1 };
        var ordinary = await CreateReader(enabled: false).QueryGeoServicesRawPointPageAsync(1, query);
        var scoped = await CreateReader().QueryGeoServicesRawPointPageAsync(1, query);
        scoped.Items.Select(feature => feature.Id).Should().Equal(ordinary.Items.Select(feature => feature.Id));
        scoped.Items.Should().ContainSingle().Which.Id.Should().Be(1);
        scoped.HasMoreResults.Should().Be(ordinary.HasMoreResults).And.BeTrue();
        scoped.Items.Select(RawSetting).Should().OnlyContain(value => value == "0");
        await AssertPoolSettingAsync();
    }

    [IntegrationTest]
    public async Task RawPointPage_NoBbox_RetainsOrdinaryPlannerSetting()
    {
        var page = await CreateReader().QueryGeoServicesRawPointPageAsync(1, Bbox() with { SpatialFilter = null });
        page.Items.Should().HaveCount(3);
        page.Items.Select(RawSetting).Should().OnlyContain(value => value == "2");
        await AssertPoolSettingAsync();
    }

    [IntegrationTest]
    public async Task RawPointPage_BorrowedMutationTransaction_RetainsCallerPlannerSetting()
    {
        var provider = new SourceProvider(_source);
        var reader = CreateReader(provider: provider);
        var processId = await ReadPoolProcessIdAsync();
        await PostgresMutationTransaction.ExecuteAsync(provider, async () =>
        {
            await using var lease = await provider.OpenNpgsqlConnectionAsync();
            lease.Transaction.Should().NotBeNull();
            var page = await reader.QueryGeoServicesRawPointPageAsync(1, Bbox());
            page.Items.Select(RawSetting).Should().OnlyContain(value => value == "2");
            await using var command = new NpgsqlCommand("SHOW max_parallel_workers_per_gather", lease);
            (await command.ExecuteScalarAsync()).Should().Be("2");
            return true;
        }, _ => false, CancellationToken.None);
        await AssertPoolSettingAsync(processId);
    }

    private static string? RawSetting(RawGeoServicesFeature feature)
    {
        using var attributes = JsonDocument.Parse(feature.AttributesJson!);
        return attributes.RootElement.GetProperty("planner_setting").GetString();
    }
}
