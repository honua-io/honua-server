// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed class PublishFieldTypePolicyTests
{
    [UnitTheory]
    [InlineData("numeric", MetadataV2FieldType.Double)]
    [InlineData("decimal", MetadataV2FieldType.Double)]
    [InlineData("timestamp with time zone", MetadataV2FieldType.DateTime)]
    [InlineData("timestamp without time zone", MetadataV2FieldType.DateTime)]
    [InlineData("double precision", MetadataV2FieldType.Double)]
    [InlineData("character varying", MetadataV2FieldType.String)]
    public void MapPostgresType_Default_KeepsLegacyTypes(string dataType, MetadataV2FieldType expected)
    {
        PublishFieldTypePolicy.MapPostgresType(dataType).Should().Be(expected);
        PublishFieldTypePolicy.Default.Map("column", dataType).Should().Be(expected);
    }

    [UnitTheory]
    [InlineData("numeric", MetadataV2FieldType.Decimal)]
    [InlineData(" DECIMAL ", MetadataV2FieldType.Decimal)]
    [InlineData("timestamp with time zone", MetadataV2FieldType.TimestampOffset)]
    [InlineData("timestamp without time zone", MetadataV2FieldType.DateTime)]
    [InlineData("double precision", MetadataV2FieldType.Double)]
    [InlineData("integer", MetadataV2FieldType.Integer)]
    public void Map_ExactTypes_PublishesDecimalAndTimestampOffset(string dataType, MetadataV2FieldType expected)
    {
        PublishFieldTypePolicy.MapExactPostgresType(dataType).Should().Be(expected);
        PublishFieldTypePolicy.Exact([]).Map("column", dataType).Should().Be(expected);
    }

    [UnitTest]
    public void Map_ExactTypes_DefaultTypedColumnKeepsDateTime()
    {
        var policy = PublishFieldTypePolicy.Exact(["Observed_At", null, " "]);

        policy.Map("observed_at", "timestamp with time zone").Should().Be(MetadataV2FieldType.DateTime);
        policy.Map("recorded_at", "timestamp with time zone").Should().Be(MetadataV2FieldType.TimestampOffset);
    }

    [UnitTest]
    public void ForPublish_ExactRequest_KeepsPrimaryKeyAndTemporalColumnDefault()
    {
        var request = new LayerPublishRequest
        {
            Schema = "public", Table = "ledger", LayerName = "Ledger", ExactTypes = true, TemporalColumn = "observed_at"
        };

        var policy = PublishFieldTypePolicy.ForPublish(request, "amount_id");

        policy.Map("observed_at", "timestamp with time zone").Should().Be(MetadataV2FieldType.DateTime);
        policy.Map("amount_id", "numeric").Should().Be(MetadataV2FieldType.Double);
        policy.Map("amount", "numeric").Should().Be(MetadataV2FieldType.Decimal);
        PublishFieldTypePolicy.ForPublish(new LayerPublishRequest { Schema = "public", Table = "ledger", LayerName = "Ledger" }, null)
            .Should().BeSameAs(PublishFieldTypePolicy.Default);
    }

    [UnitTest]
    public void ForRewrite_LayerWithExactFields_StaysExactWithoutTheFlag()
    {
        var request = new LayerPublishRequest { Schema = "public", Table = "ledger", LayerName = "Ledger" };

        var policy = PublishFieldTypePolicy.ForRewrite(request, ["observed_at"], "objectid");

        policy.Map("observed_at", "timestamp with time zone").Should().Be(MetadataV2FieldType.DateTime);
        policy.Map("recorded_at", "timestamp with time zone").Should().Be(MetadataV2FieldType.TimestampOffset);
        PublishFieldTypePolicy.ForRewrite(request, null, "objectid").Should().BeSameAs(PublishFieldTypePolicy.Default);
    }

    [UnitTest]
    public void RewriteFieldTypePolicySql_DetectsExactFieldsAndKeptDateTimeFields()
    {
        var sql = PostgreSqlLayerPublishingService.RewriteFieldTypePolicySql;

        sql.Should().Contain("bool_or(field_type IN ('Decimal', 'TimestampOffset'))");
        sql.Should().Contain("FILTER (WHERE field_type = 'DateTime')");
        sql.Should().Contain("WHERE layer_id = @layerId");
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "numeric")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "timestamp with time zone")]
    [InlineData(MetadataV2FieldType.Double, "double precision")]
    [InlineData(MetadataV2FieldType.DateTime, "timestamp with time zone")]
    public void FeatureLayerCopySqlType_MapsExactTypes(MetadataV2FieldType type, string expected)
    {
        var sqlType = typeof(PostgresFeatureLayerCopyService)
            .GetMethod("SqlType", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new MetadataV2Field { Name = "value", Type = type }]);

        sqlType.Should().Be(expected);
    }
}
