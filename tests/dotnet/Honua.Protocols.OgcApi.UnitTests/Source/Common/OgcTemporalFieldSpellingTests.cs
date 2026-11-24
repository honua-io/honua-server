// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Common;

// Configured time fields match the schema case-insensitively, but attribute keys and storage columns are
// case-sensitive: every OGC temporal path must read the field under the schema's spelling.
[Trait("Category", "Unit")]
[Trait("Tier", "Fast")]
public sealed class OgcTemporalFieldSpellingTests
{
    private const int LayerIndex = 3;

    private static readonly MetadataV2Object Resource = new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res-layer-3", Name = "observations" },
        Type = MetadataV2ObjectType.FeatureDataset,
        SchemaFields =
        [
            new() { Name = "Observed_At", Type = MetadataV2FieldType.TimestampOffset },
            new() { Name = "Closed_At", Type = MetadataV2FieldType.TimestampOffset },
        ],
        Temporal = new MetadataV2ObjectTemporal { StartTimeField = "observed_at", EndTimeField = "CLOSED_AT" },
    };

    [UnitTest]
    public void TryParse_ConfiguredNamesDifferInCase_FiltersOnSchemaSpelling()
    {
        var parsed = OgcTemporalFilterParser.TryParse(
            "2024-01-01T00:00:00Z/2024-12-31T00:00:00Z", Resource, out var filter, out var error);

        parsed.Should().BeTrue(error);
        filter!.Value.PropertyName.Should().Be("Observed_At");
        filter.Value.EndPropertyName.Should().Be("Closed_At");
    }

    [UnitTest]
    public void TryResolveTemporalFieldsV2_ConfiguredNamesDifferInCase_ReturnsSchemaSpelling()
    {
        var resolved = OgcQueryablesUtilities.TryResolveTemporalFieldsV2(
            Resource, out var start, out _, out var end, out _);

        resolved.Should().BeTrue();
        start.Should().Be("Observed_At");
        end.Should().Be("Closed_At");
    }

    [UnitTest]
    public async Task BuildTemporalExtentAsync_ConfiguredNamesDifferInCase_ReadsSchemaSpelling()
    {
        var min = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var max = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var reader = Substitute.For<IFeatureReader>();
        reader.GetTemporalExtentAsync(LayerIndex, "Observed_At", TemporalPropertyType.DateTime, Arg.Any<CancellationToken>())
            .Returns(TemporalExtentResult.Create(min, min));
        reader.GetTemporalExtentAsync(LayerIndex, "Closed_At", TemporalPropertyType.DateTime, Arg.Any<CancellationToken>())
            .Returns(TemporalExtentResult.Create(min, max));

        var extent = await OgcQueryablesUtilities.BuildTemporalExtentAsync(
            Resource, LayerIndex, reader, CancellationToken.None);

        extent.Should().NotBeNull();
        extent!.Interval[0][0].Should().Be("2024-01-01T00:00:00Z");
        extent.Interval[0][1].Should().Be("2024-12-31T00:00:00Z");
    }
}
