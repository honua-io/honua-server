// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Oracle.Features.FeatureStore.Services;

namespace Honua.Db.Oracle.Tests;

// An undeclared name must not be quoted through to an unpublished physical column.
public class OracleFeatureQueryBuilderUndeclaredFieldTests
{
    private static readonly IReadOnlyList<string> _attributeColumns = ["name", "AREA"];

    private static OracleLayerMapping BuildMapping()
        => OracleLayerMapping.FromStorage(7, new LayerStorageMapping(
            TableName: "PARCELS",
            SchemaName: "GIS",
            CatalogName: null,
            DatabaseName: null,
            PrimaryKeyColumn: "OBJECTID",
            GeometryColumn: "SHAPE",
            StorageSrid: 4326));

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void BuildSelectQuery_OrderByUndeclaredField_IsRefused()
    {
        var query = new FeatureQuery { OrderBy = [new OrderByClause("SSN", true)] };

        var act = () => OracleFeatureQueryBuilder.BuildSelectQuery(BuildMapping(), query, _attributeColumns);

        var error = Assert.Throws<ArgumentException>(act);
        Assert.Contains("not declared", error.Message, StringComparison.Ordinal);
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void BuildSelectQuery_WhereUndeclaredField_IsRefused()
    {
        var query = new FeatureQuery { Where = "SSN = 1" };

        var act = () => OracleFeatureQueryBuilder.BuildSelectQuery(BuildMapping(), query, _attributeColumns);

        Assert.Throws<ArgumentException>(act);
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void BuildSelectQuery_DeclaredFieldsInAnyCase_StillResolve()
    {
        var query = new FeatureQuery
        {
            Where = "area > 1",
            OrderBy = [new OrderByClause("NAME", false), new OrderByClause("objectid", true)]
        };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(BuildMapping(), query, _attributeColumns);

        Assert.Contains("\"AREA\" > :p", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"name\" DESC", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"OBJECTID\" ASC", result.Sql, StringComparison.Ordinal);
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void BuildSelectQuery_UndeclaredOutField_IsLeftOutOfTheProjection()
    {
        var query = new FeatureQuery { OutFields = ["name", "SSN"] };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(BuildMapping(), query, _attributeColumns);

        Assert.Contains("\"name\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SSN", result.Sql, StringComparison.Ordinal);
    }
}
