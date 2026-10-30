// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    [IntegrationTest]
    public async Task QueryGeoServicesRawPointPageAsync_PhysicalColumns_ReturnsPointPageAndHonorsFilters()
    {
        var raw = CreateReader().Should().BeAssignableTo<IPagedRawGeoServicesFeatureStore>().Subject;

        var page = await raw.QueryGeoServicesRawPointPageAsync(1, new FeatureQuery
        {
            Limit = 1,
            PublicIdAttributeName = "objectid",
        });

        page.HasMoreResults.Should().BeTrue();
        page.Items.Should().ContainSingle();
        var feature = page.Items[0];
        feature.Id.Should().Be(1);
        feature.PublicIdJson.Should().BeNull();
        feature.X.Should().BeApproximately(-157.8583, 1e-6);
        feature.Y.Should().BeApproximately(21.3069, 1e-6);
        using (var document = JsonDocument.Parse(feature.AttributesJson!))
        {
            document.RootElement.GetProperty("name").GetString().Should().Be("Honolulu");
            document.RootElement.GetProperty("population").GetInt32().Should().Be(350000);
        }

        var filtered = await raw.QueryGeoServicesRawPointPageAsync(1, new FeatureQuery
        {
            Limit = 10,
            ObjectIds = ImmutableArray.Create(2L),
        });
        filtered.HasMoreResults.Should().BeFalse();
        filtered.Items.Should().ContainSingle();
        filtered.Items[0].Id.Should().Be(2);
        filtered.Items[0].X.Should().BeApproximately(-156.3319, 1e-6);
        filtered.Items[0].Y.Should().BeApproximately(20.7984, 1e-6);

        var masked = await raw.QueryGeoServicesRawPointPageAsync(1, new FeatureQuery
        {
            Limit = 1,
            ObjectIds = ImmutableArray.Create(1L),
            EnforcedMaskedFields = ImmutableArray.Create("population"),
        });
        using var maskedDocument = JsonDocument.Parse(masked.Items[0].AttributesJson!);
        maskedDocument.RootElement.TryGetProperty("population", out _).Should().BeFalse();
        maskedDocument.RootElement.GetProperty("name").GetString().Should().Be("Honolulu");
    }

    [IntegrationTest]
    public async Task QueryGeoServicesRawPointPageAsync_JsonbAttributes_PreservesNumericJson()
    {
        var raw = CreateReader(attributesColumn: "attributes", includeNamespacedField: true)
            .Should().BeAssignableTo<IPagedRawGeoServicesFeatureStore>().Subject;

        var page = await raw.QueryGeoServicesRawPointPageAsync(1, new FeatureQuery
        {
            Limit = 10,
            ObjectIds = ImmutableArray.Create(1L),
        });

        page.HasMoreResults.Should().BeFalse();
        var feature = page.Items.Should().ContainSingle().Subject;
        feature.X.Should().BeApproximately(-157.8583, 1e-6);
        feature.Y.Should().BeApproximately(21.3069, 1e-6);
        using var document = JsonDocument.Parse(feature.AttributesJson!);
        document.RootElement.GetProperty("eo:cloud_cover").GetDouble().Should().Be(12.5);
    }

    [IntegrationTest]
    public async Task QueryGeoServicesRawPointPageAsync_Distinct_IsRefused()
    {
        var raw = CreateReader().Should().BeAssignableTo<IPagedRawGeoServicesFeatureStore>().Subject;

        var act = () => raw.QueryGeoServicesRawPointPageAsync(1, new FeatureQuery { Distinct = true, Limit = 1 });

        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
