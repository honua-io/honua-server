// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Catalog.Domain;
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
            FieldType.Double);

        expression.Should().Be("AVG(NULLIF((\"longitude\")::text, '')::numeric)");
    }

    [Fact]
    public void BuildAttributesExpressionText_WithWideOutFields_ChunksJsonbBuildObjectCalls()
    {
        var fields = Enumerable.Range(1, 51)
            .Select(index => new FieldDefinition($"field_{index}", FieldType.String))
            .ToArray();

        var method = typeof(PostgresStorageMappedFeatureReader).GetMethod(
            "BuildAttributesExpressionText",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var expression = (string)method!.Invoke(null, [fields])!;

        expression.Split("jsonb_build_object", StringSplitOptions.None).Length.Should().Be(3);
        expression.Should().StartWith("(");
        expression.Should().EndWith(")::text");
        expression.Should().Contain(" || ");
        expression.Should().Contain("'field_1', \"field_1\"");
        expression.Should().Contain("'field_51', \"field_51\"");
    }
}
