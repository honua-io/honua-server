// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Snowflake.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;

namespace Honua.Db.Snowflake.Tests;

public partial class SnowflakeFeatureQueryBuilderTests
{
    [Fact]
    public void BuildQueries_EnforcedSqlFilter_ThrowsNotSupported()
    {
        var query = new FeatureQuery
        {
            EnforcedSqlFilter = new Honua.Core.Queries.Filters.SqlFragment("tenant_id = @p0", [42])
        };

        Assert.Throws<NotSupportedException>(() => SnowflakeFeatureQueryBuilder.BuildSelectQuery(BuildMapping(), query, ["NAME"]));
        Assert.Throws<NotSupportedException>(() => SnowflakeFeatureQueryBuilder.BuildCountQuery(BuildMapping(), query, ["NAME"]));
        Assert.Throws<NotSupportedException>(() => SnowflakeFeatureQueryBuilder.BuildObjectIdsQuery(BuildMapping(), query, ["NAME"]));
        Assert.Throws<NotSupportedException>(() => SnowflakeFeatureQueryBuilder.BuildExtentQuery(BuildMapping(), query, ["NAME"]));
    }

    [Theory]
    [InlineData("eo:cloud_cover")]
    [InlineData("owner.name")]
    [InlineData("cloud-cover")]
    public void BuildSelectQuery_ExtendedAttributeFieldName_QuotesAttributeIdentifier(string fieldName)
    {
        var query = new FeatureQuery
        {
            OrderBy = [OrderByClause.Asc(fieldName)],
            TextSearch = new FeatureTextSearch([fieldName], [new FeatureSearchTerm[] { new("clear", false) }])
        };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(BuildMapping(), query, [fieldName]);

        Assert.Contains($"\"{fieldName}\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains($"ORDER BY \"{fieldName}\" ASC", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQueries_UndeclaredFieldReference_ThrowsArgumentException()
    {
        string[] declared = ["NAME", "AREA"];

        Assert.Throws<ArgumentException>(() => SnowflakeFeatureQueryBuilder.BuildSelectQuery(
            BuildMapping(), new FeatureQuery { Where = "SECRET_COL = 'x'" }, declared));
        Assert.Throws<ArgumentException>(() => SnowflakeFeatureQueryBuilder.BuildSelectQuery(
            BuildMapping(), new FeatureQuery { OrderBy = [OrderByClause.Asc("SECRET_COL")] }, declared));
        Assert.Throws<ArgumentException>(() => SnowflakeFeatureQueryBuilder.BuildStatistics(
            BuildMapping(),
            new FeatureQuery
            {
                OutStatistics = [new StatisticDefinition { StatisticType = StatisticType.Sum, OnStatisticField = "SECRET_COL", OutStatisticFieldName = "S" }]
            },
            declared));
        Assert.Throws<ArgumentException>(() => SnowflakeFeatureQueryBuilder.BuildStatistics(
            BuildMapping(),
            new FeatureQuery
            {
                OutStatistics = [new StatisticDefinition { StatisticType = StatisticType.Count, OnStatisticField = "OBJECTID", OutStatisticFieldName = "N" }],
                GroupByFields = ["SECRET_COL"]
            },
            declared));
    }

    [UnitTest]
    public void BuildStatistics_OrderByGroupByFieldWithDifferentCasing_QuotesCanonicalDeclaredColumnName()
    {
        string[] declared = ["Region", "AREA"];
        var query = new FeatureQuery
        {
            GroupByFields = ["region"],
            OutStatistics = [new StatisticDefinition { StatisticType = StatisticType.Sum, OnStatisticField = "area", OutStatisticFieldName = "total_area" }],
            OrderBy = [OrderByClause.Asc("REGION")]
        };

        var result = SnowflakeFeatureQueryBuilder.BuildStatistics(BuildMapping(), query, declared);

        Assert.Contains("GROUP BY \"Region\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"Region\" ASC", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY \"region\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY \"REGION\"", result.Sql, StringComparison.Ordinal);
    }
}
