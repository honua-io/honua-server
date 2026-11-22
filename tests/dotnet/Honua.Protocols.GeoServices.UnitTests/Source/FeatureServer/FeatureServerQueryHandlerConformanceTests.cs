// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class FeatureServerQueryHandlerConformanceTests
{
    [Fact]
    public void TryParseStatisticsDefinitions_CustomObjectIdField_AcceptsResolvedOidAndRejectsHardcodedObjectId()
    {
        var resource = CreateCustomOidResource("fid");

        var accepted = FeatureServerQueryHandler.TryParseStatisticsDefinitions(
            """[{"statisticType":"count","onStatisticField":"fid","outStatisticFieldName":"fid_count"}]""",
            resource,
            out var definitions,
            out var acceptedError);

        accepted.Should().BeTrue(acceptedError);
        definitions.Should().ContainSingle();
        definitions[0].OnStatisticField.Should().Be("fid");
        definitions[0].FieldType.Should().Be(MetadataV2FieldType.BigInteger);

        var rejected = FeatureServerQueryHandler.TryParseStatisticsDefinitions(
            """[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"oid_count"}]""",
            resource,
            out _,
            out var rejectedError);

        rejected.Should().BeFalse();
        rejectedError.Should().Contain("objectid");
    }

    [Fact]
    public void TryParseGroupByFields_CustomObjectIdField_AcceptsResolvedOidAndRejectsHardcodedObjectId()
    {
        var resource = CreateCustomOidResource("fid");

        var accepted = FeatureServerQueryHandler.TryParseGroupByFields(
            "fid,name",
            resource,
            out var groupByFields,
            out var acceptedError);

        accepted.Should().BeTrue(acceptedError);
        groupByFields.Should().Equal("fid", "name");

        var rejected = FeatureServerQueryHandler.TryParseGroupByFields(
            "objectid",
            resource,
            out _,
            out var rejectedError);

        rejected.Should().BeFalse();
        rejectedError.Should().Contain("objectid");
    }

    [Theory]
    [InlineData("x y")]
    [InlineData("1count")]
    [InlineData("count\"; --")]
    [InlineData("a_name_that_is_exactly_sixty_four_characters_long_for_postgresql")]
    public void TryParseStatisticsDefinitions_AliasIsNotAPortableIdentifier_RejectsAsClientError(string alias)
    {
        var accepted = FeatureServerQueryHandler.TryParseStatisticsDefinitions(
            $$"""[{"statisticType":"count","onStatisticField":"fid","outStatisticFieldName":{{System.Text.Json.JsonSerializer.Serialize(alias)}}}]""",
            CreateCustomOidResource("fid"),
            out _,
            out var error);

        accepted.Should().BeFalse();
        error.Should().Contain("outStatisticFieldName");
    }

    [Fact]
    public void TryParseStatisticsDefinitions_AliasAtPostgreSqlIdentifierLimit_IsAccepted()
    {
        var alias = "_" + new string('a', 62);

        var accepted = FeatureServerQueryHandler.TryParseStatisticsDefinitions(
            $$"""[{"statisticType":"count","onStatisticField":"fid","outStatisticFieldName":"{{alias}}"}]""",
            CreateCustomOidResource("fid"),
            out var definitions,
            out var error);

        accepted.Should().BeTrue(error);
        definitions[0].OutStatisticFieldName.Should().Be(alias);
    }

    private static MetadataV2Object CreateCustomOidResource(string objectIdFieldName) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "custom-oid", Name = "custom-oid" },
        Type = MetadataV2ObjectType.FeatureDataset,
        SchemaFields =
        [
            new MetadataV2Field
            {
                Name = objectIdFieldName,
                Type = MetadataV2FieldType.BigInteger,
                SemanticRoles = ["id.primary"]
            },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
        ]
    };
}
