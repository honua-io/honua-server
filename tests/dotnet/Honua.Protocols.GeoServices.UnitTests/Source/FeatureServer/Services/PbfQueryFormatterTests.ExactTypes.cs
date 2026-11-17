// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed partial class PbfQueryFormatterTests
{
    [Fact]
    public void FormatAsPbf_ExactDecimalAndOffsetTimestamp_EncodeDoubleAndEpoch()
    {
        var resource = CreateLayer([
            new MetadataV2Field { Name = "amount", Type = MetadataV2FieldType.Decimal },
            new MetadataV2Field { Name = "observed", Type = MetadataV2FieldType.TimestampOffset }
        ], MetadataV2GeometryType.Point);
        var feature = Feature.Create(1, null, new Dictionary<string, object?>
        {
            ["amount"] = "1234.50",
            ["observed"] = "2024-02-29T02:00:00+02:00"
        }.ToImmutableDictionary());

        var (payload, _) = _sut.FormatAsPbf(QueryResult<Feature>.Create(1, [feature]), resource,
            false, null, false, false, null, null, ["amount", "observed"]);

        var result = GetFirstLengthDelimitedField(GetFirstLengthDelimitedField(payload, 2), 1);
        GetFirstVarintField(GetFirstLengthDelimitedField(result, 13, 0), 2).Should().Be(3);
        GetFirstVarintField(GetFirstLengthDelimitedField(result, 13, 1), 2).Should().Be(5);
        var outputFeature = GetFirstLengthDelimitedField(result, 15);
        ReadDoubleField(GetFirstLengthDelimitedField(outputFeature, 1, 0), 3).Should().Be(1234.5);
        GetFirstVarintField(GetFirstLengthDelimitedField(outputFeature, 1, 1), 6)
            .Should().Be((ulong)new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }
}
