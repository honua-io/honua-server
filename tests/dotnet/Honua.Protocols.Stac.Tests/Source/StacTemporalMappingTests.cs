// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Stac.Services;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Stac;

[Trait("Tier", "Fast")]
public sealed class StacTemporalMappingTests
{
    [Fact]
    public void StringTimestamp_IsNotPresentedAsAResolvedAcquisitionDate()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields = [new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.String }]
        };
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("timestamp", "2024-01-01T00:00:00Z"));

        var item = StacMappingService.MapFeatureToItem(feature, resource, new MetadataV2Publication(), 0, "https://example.test");

        item.Properties.Should().NotBeNull();
        item.Properties!["honua:datetime_source"].Should().Be("unknown");
        StacFilterHelpers.ParseDatetime("2024-01-01T00:00:00Z", resource).Should().BeNull();
    }

    [Fact]
    public async Task TypedFallback_UsesSamePhysicalFieldForMappingFilterAndExtent()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields = [new MetadataV2Field { Name = "TIMESTAMP", Type = MetadataV2FieldType.DateTime }]
        };
        var instant = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("TIMESTAMP", instant));
        var reader = Substitute.For<IFeatureReader>();
        reader.GetTemporalExtentAsync(0, "TIMESTAMP", Arg.Any<TemporalPropertyType>(), Arg.Any<CancellationToken>())
            .Returns(TemporalExtentResult.Create(instant, instant));

        var item = StacMappingService.MapFeatureToItem(feature, resource, new MetadataV2Publication(), 0, "https://example.test");
        var filter = StacFilterHelpers.ParseDatetime("2024-01-01T00:00:00Z", resource);
        var collection = await StacMappingService.MapResourceToCollectionAsync(
            resource, new MetadataV2Publication(), new MetadataV2Service(), 0, reader, "https://example.test", null, CancellationToken.None);

        item.Properties.Should().NotBeNull();
        item.Properties!["datetime"].Should().Be(instant.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        filter!.Value.PropertyName.Should().Be("TIMESTAMP");
        collection.Extent.Temporal.Interval.Single().Should().OnlyContain(value => value != null);
    }
}
