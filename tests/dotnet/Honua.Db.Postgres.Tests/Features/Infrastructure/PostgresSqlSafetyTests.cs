// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Infrastructure;

namespace Honua.Postgres.Tests.Features.Infrastructure;

public sealed class PostgresSqlSafetyTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("  SELECT $1 AS value")]
    [InlineData("WITH rows AS (SELECT 1) SELECT * FROM rows")]
    [InlineData("SELECT ';'::text AS semicolon_literal")]
    [InlineData("SELECT \"semi;colon\" FROM \"features\"")]
    public void ValidateReadOnlySingleStatement_WithSafeReadSql_DoesNotThrow(string sql)
    {
        var action = () => PostgresSqlSafety.ValidateReadOnlySingleStatement(sql);

        action.Should().NotThrow();
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE features")]
    [InlineData("SELECT 1 -- trailing comment")]
    [InlineData("SELECT /* hidden */ 1")]
    [InlineData("WITH deleted AS (DELETE FROM features RETURNING *) SELECT * FROM deleted")]
    [InlineData("UPDATE features SET attributes = '{}'")]
    [InlineData("DROP TABLE features")]
    public void ValidateReadOnlySingleStatement_WithUnsafeSql_Throws(string sql)
    {
        var action = () => PostgresSqlSafety.ValidateReadOnlySingleStatement(sql);

        action.Should().Throw<ArgumentException>();
    }
}
