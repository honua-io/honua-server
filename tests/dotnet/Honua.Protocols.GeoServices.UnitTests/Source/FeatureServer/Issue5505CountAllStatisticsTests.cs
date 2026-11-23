// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class Issue5505CountAllStatisticsTests
{
    private static readonly MetadataV2Object Layer = new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "layer", Name = "layer" },
        SchemaFields =
        [
            new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
        ]
    };

    [UnitTest]
    public void Issue5505_CountOnWildcard_UsesNonNullableObjectIdForRowCount()
    {
        const string json =
            """[{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"ROW_COUNT"}]""";

        var parsed = FeatureServerQueryHandler.TryParseStatisticsDefinitions(
            json, Layer, out var definitions, out var error);

        parsed.Should().BeTrue(error);
        definitions.Should().ContainSingle().Which.Should().BeEquivalentTo(new StatisticDefinition
        {
            StatisticType = StatisticType.Count,
            OnStatisticField = "objectid",
            OutStatisticFieldName = "ROW_COUNT",
            FieldType = MetadataV2FieldType.BigInteger
        });
    }

    [UnitTheory]
    [InlineData("sum")]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("avg")]
    [InlineData("stddev")]
    [InlineData("var")]
    public void Issue5505_NonCountStatisticOnWildcard_IsRejected(string statisticType)
    {
        var json =
            $$"""[{"statisticType":"{{statisticType}}","onStatisticField":"*","outStatisticFieldName":"value"}]""";

        var parsed = FeatureServerQueryHandler.TryParseStatisticsDefinitions(
            json, Layer, out _, out var error);

        parsed.Should().BeFalse();
        error.Should().Contain("Field '*'");
    }

    [UnitTest]
    public void Issue5505_CountOnWildcard_UsesDefaultIdColumnWhenObjectIdIsAbsent()
    {
        // DuckDB ObjectIdColumn and Databricks PrimaryKeyColumn both default to "id".
        var layer = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "layer", Name = "layer" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.BigInteger, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
            ]
        };

        const string json =
            """[{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"ROW_COUNT"}]""";

        var parsed = FeatureServerQueryHandler.TryParseStatisticsDefinitions(
            json, layer, out var definitions, out var error);

        parsed.Should().BeTrue(error);
        definitions.Should().ContainSingle().Which.Should().BeEquivalentTo(new StatisticDefinition
        {
            StatisticType = StatisticType.Count,
            OnStatisticField = "id",
            OutStatisticFieldName = "ROW_COUNT",
            FieldType = MetadataV2FieldType.BigInteger
        });
    }

    [UnitTest]
    public void Issue5505_CountOnWildcard_UsesDeclaredPrimaryKeyInsteadOfCanonicalObjectId()
    {
        var layer = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "layer", Name = "layer" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger },
                new MetadataV2Field
                {
                    Name = "parcel_key",
                    Type = MetadataV2FieldType.Integer,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
            ]
        };

        const string json =
            """[{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"ROW_COUNT"}]""";

        var parsed = FeatureServerQueryHandler.TryParseStatisticsDefinitions(
            json, layer, out var definitions, out var error);

        parsed.Should().BeTrue(error);
        definitions.Should().ContainSingle().Which.Should().BeEquivalentTo(new StatisticDefinition
        {
            StatisticType = StatisticType.Count,
            OnStatisticField = "parcel_key",
            OutStatisticFieldName = "ROW_COUNT",
            FieldType = MetadataV2FieldType.Integer
        });
    }

    [UnitTest]
    public void CustomObjectIdLayer_WithoutCanonicalObjectId_AcceptsCustomOidAndRejectsHardcodedObjectId()
    {
        var layer = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "layer", Name = "layer" },
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "parcel_key",
                    Type = MetadataV2FieldType.Integer,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
            ]
        };

        FeatureServerQueryHandler.TryParseStatisticsDefinitions(
            """[{"statisticType":"count","onStatisticField":"parcel_key","outStatisticFieldName":"n"}]""",
            layer,
            out var defs,
            out var statsError).Should().BeTrue(statsError);
        defs.Should().ContainSingle().Which.OnStatisticField.Should().Be("parcel_key");

        FeatureServerQueryHandler.TryParseStatisticsDefinitions(
            """[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"n"}]""",
            layer,
            out _,
            out var badStatsError).Should().BeFalse();
        badStatsError.Should().Contain("objectid");

        FeatureServerQueryHandler.TryParseGroupByFields(
            "parcel_key",
            layer,
            out var groupBy,
            out var groupError).Should().BeTrue(groupError);
        groupBy.Should().Equal("parcel_key");

        FeatureServerQueryHandler.TryParseGroupByFields(
            "objectid",
            layer,
            out _,
            out var badGroupError).Should().BeFalse();
        badGroupError.Should().Contain("objectid");

        FeatureServerQueryHandler.TryParseHavingConditions(
            "COUNT(parcel_key) > 1",
            defs,
            layer,
            out var having,
            out var havingError).Should().BeTrue(havingError);
        having.Should().ContainSingle().Which.OnStatisticField.Should().Be("parcel_key");
    }

    [UnitTheory]
    [InlineData(null, 1000, 1000)]
    [InlineData(5, 1000, 5)]
    [InlineData(5000, 1000, 1000)]
    [InlineData(0, 1000, 1000)]
    public void StatisticsPageSize_HonorsClientResultRecordCountCappedByMaxRecordCount(
        int? requestedRecordCount,
        int maxRecordCount,
        int expectedLimit)
    {
        var limits = new Honua.Core.Configuration.QueryLimits
        {
            DefaultRecordCount = 100,
            MaxRecordCount = maxRecordCount
        };

        FeatureServerQueryHandler.StatisticsPageSize(requestedRecordCount, limits)
            .Should().Be(expectedLimit);
    }
}
