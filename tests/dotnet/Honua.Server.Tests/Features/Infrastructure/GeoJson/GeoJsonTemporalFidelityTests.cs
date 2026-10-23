// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.GeoJson;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.GeoJson;

public sealed class GeoJsonTemporalFidelityTests
{
    [UnitTheory]
    [InlineData("iso")]
    [InlineData("offset-iso")]
    [InlineData("offset")]
    [InlineData("utc")]
    [InlineData("epoch")]
    [InlineData("epoch-text")]
    [InlineData("epoch-double")]
    [InlineData("json-number")]
    [InlineData("json-string")]
    public void Create_DateTimeStorageRepresentations_PreserveMilliseconds(string representation)
    {
        var expected = new DateTimeOffset(2024, 2, 29, 12, 34, 56, 789, TimeSpan.Zero);
        object value = representation switch
        {
            "iso" => "2024-02-29T12:34:56.789Z",
            "offset-iso" => "2024-02-29T02:34:56.789-10:00",
            "offset" => expected.ToOffset(TimeSpan.FromHours(-10)),
            "utc" => expected.UtcDateTime,
            "epoch" => expected.ToUnixTimeMilliseconds(),
            "epoch-text" => expected.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            "epoch-double" => (double)expected.ToUnixTimeMilliseconds(),
            "json-number" => JsonDocument.Parse("1709210096789").RootElement.Clone(),
            "json-string" => JsonDocument.Parse("\"2024-02-29T12:34:56.789Z\"").RootElement.Clone(),
            _ => throw new ArgumentOutOfRangeException(nameof(representation))
        };

        AssertBothPaths(value, MetadataV2FieldType.DateTime, "2024-02-29T12:34:56.7890000Z");
    }

    [UnitTheory]
    [InlineData("2024-02-29T12:34:56.0000001Z", "2024-02-29T12:34:56.0000001Z")]
    [InlineData("2024-02-29T02:34:56.1234567-10:00", "2024-02-29T12:34:56.1234567Z")]
    [InlineData("2024-02-29T23:59:59.9999999Z", "2024-02-29T23:59:59.9999999Z")]
    [InlineData("2024-02-29T12:34:56Z", "2024-02-29T12:34:56Z")]
    [InlineData(null, null)]
    public void Create_DateTimePrecisionBoundaries_PreserveInstantAndNull(string? value, string? expected)
        => AssertBothPaths(value, MetadataV2FieldType.DateTime, expected);

    [UnitTest]
    public void Create_DateOnly_DoesNotIntroduceTime()
        => AssertBothPaths("2024-02-29T12:34:56.789Z", MetadataV2FieldType.Date, "2024-02-29");

    [UnitTest]
    public void Create_StringField_DoesNotReformatDateLikeText()
        => AssertBothPaths("2024-02-29T02:34:56.789-10:00", MetadataV2FieldType.String,
            "2024-02-29T02:34:56.789-10:00");

    private static void AssertBothPaths(object? value, MetadataV2FieldType type, string? expected)
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "temporal", Name = "temporal" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields = [new MetadataV2Field { Name = "observed_at", Type = type, Nullable = true }]
        };
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("observed_at", value);
        var ordinary = GeoJsonFeatureBaseBuilder.Create(Feature.Create(702, null, attributes), resource);
        var encoded = GeoJsonFeatureBaseBuilder.Create(EncodedGeoJsonFeature.Create(702, null, attributes), resource);
        Assert.Equal(expected, ordinary.Properties["observed_at"]);
        Assert.Equal(expected, encoded.Properties["observed_at"]);
    }
}
