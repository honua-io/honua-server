// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.FeatureStore.Services;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class PostgresStorageMappedFeatureReaderSqlTests
{
    [Fact]
    public void RewriteAttributeTextAccessExpressions_WithCanonicalAttributeFilter_UsesMappedSourceColumn()
    {
        var rewritten = PostgresStorageMappedFeatureReader.RewriteAttributeTextAccessExpressions(
            "\"attributes\" ->> 'dam_name' IN ($1, $2)",
            field =>
            {
                field.Should().Be("dam_name");
                return "\"dam_name\"";
            });

        rewritten.Should().Be("NULLIF((\"dam_name\")::text, '') IN ($1, $2)");
    }

    [Fact]
    public void BuildStatisticsAggregateExpression_WithNumericAggregate_CastsMappedSourceColumn()
    {
        var expression = PostgresStorageMappedFeatureReader.BuildStatisticsAggregateExpression(
            StatisticType.Avg,
            "\"longitude\"",
            Honua.Core.Features.Metadata.Domain.V2.MetadataV2FieldType.Double);

        expression.Should().Be("AVG(NULLIF((\"longitude\")::text, '')::numeric)");
    }

    [Fact]
    public void BuildAttributesExpressionText_WithWideOutFields_ChunksJsonbBuildObjectCalls()
    {
        var fields = Enumerable.Range(1, 51)
            .Select(index => new MetadataV2Field { Name = $"field_{index}", Type = MetadataV2FieldType.String })
            .ToArray();

        var method = typeof(PostgresStorageMappedFeatureReader).GetMethod(
            "BuildAttributesExpressionText",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(MetadataV2Field[])],
            modifiers: null);

        method.Should().NotBeNull();

        var expression = (string)method!.Invoke(null, [fields])!;

        expression.Split("jsonb_build_object", StringSplitOptions.None).Length.Should().Be(3);
        expression.Should().StartWith("(");
        expression.Should().EndWith(")::text");
        expression.Should().Contain(" || ");
        expression.Should().Contain("'field_1', \"field_1\"");
        expression.Should().Contain("'field_51', \"field_51\"");
    }

    [Fact]
    public void BuildAttributesExpressionText_WithJsonbColumn_PreservesNumericTypeButStringifiesText()
    {
        var fields = new[]
        {
            new MetadataV2Field { Name = "site_name", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "sync_version", Type = MetadataV2FieldType.Integer },
            new MetadataV2Field { Name = "elevation", Type = MetadataV2FieldType.Double },
            new MetadataV2Field { Name = "serviceable", Type = MetadataV2FieldType.Boolean },
            new MetadataV2Field { Name = "inspected", Type = MetadataV2FieldType.Date },
        };

        var method = typeof(PostgresStorageMappedFeatureReader).GetMethod(
            "BuildAttributesExpressionText",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(MetadataV2Field[]), typeof(string)],
            modifiers: null);

        method.Should().NotBeNull();

        var expression = (string)method!.Invoke(null, [fields, "attributes"])!;

        // Numeric/boolean fields use the jsonb-preserving accessor (->) so they
        // round-trip as JSON numbers/booleans, not strings.
        expression.Should().Contain("'sync_version', \"attributes\" -> 'sync_version'");
        expression.Should().Contain("'elevation', \"attributes\" -> 'elevation'");
        expression.Should().Contain("'serviceable', \"attributes\" -> 'serviceable'");

        // Text/date fields keep the text accessor (->>) — formatting unchanged.
        expression.Should().Contain("'site_name', \"attributes\" ->> 'site_name'");
        expression.Should().Contain("'inspected', \"attributes\" ->> 'inspected'");
    }
}
