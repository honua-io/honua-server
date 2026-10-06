// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class Issue5505CountAllStatisticsTests
{
    private static readonly MetadataV2Resource Layer = new()
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
        var layer = new MetadataV2Resource
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
        var layer = new MetadataV2Resource
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
}
