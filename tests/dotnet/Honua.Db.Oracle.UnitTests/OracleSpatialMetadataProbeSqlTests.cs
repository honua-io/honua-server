// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Oracle.Features.FeatureStore.Services;

namespace Honua.Db.Oracle.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public class OracleSpatialMetadataProbeSqlTests
{
    [Fact]
    public void BuildGeometryColumnTypeSql_NoOwner_ComparesAgainstConfiguredCaseExactly()
    {
        var sql = OracleSpatialMetadataProbe.BuildGeometryColumnTypeSql(includeOwner: false);

        Assert.Contains("TABLE_NAME = :p_table", sql, StringComparison.Ordinal);
        Assert.Contains("COLUMN_NAME = :p_column", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPPER(:p_table)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPPER(:p_column)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OWNER", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGeometryColumnTypeSql_WithOwner_IncludesOwnerWithoutUpperFolding()
    {
        var sql = OracleSpatialMetadataProbe.BuildGeometryColumnTypeSql(includeOwner: true);

        Assert.Contains("OWNER = :p_owner", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPPER(:p_owner)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArcSdeVersioningSql_NoOwner_ComparesTableByConfiguredCaseExactly()
    {
        var sql = OracleSpatialMetadataProbe.BuildArcSdeVersioningSql(includeOwner: false);

        Assert.Contains("TABLE_NAME = :p_table", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPPER(:p_table)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OWNER", sql, StringComparison.Ordinal);
        // The SDE versioning literals stay upper-case because Esri/SDE always create those
        // columns unquoted; Oracle therefore stores them upper-case in ALL_TAB_COLUMNS.
        Assert.Contains("'GDB_FROM_DATE'", sql, StringComparison.Ordinal);
        Assert.Contains("'GDB_TO_DATE'", sql, StringComparison.Ordinal);
        Assert.Contains("'SDE_STATE_ID'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArcSdeVersioningSql_WithOwner_IncludesOwnerWithoutUpperFolding()
    {
        var sql = OracleSpatialMetadataProbe.BuildArcSdeVersioningSql(includeOwner: true);

        Assert.Contains("OWNER = :p_owner", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPPER(:p_owner)", sql, StringComparison.Ordinal);
    }
}
