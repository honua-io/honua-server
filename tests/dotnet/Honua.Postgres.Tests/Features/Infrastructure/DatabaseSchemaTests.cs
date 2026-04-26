// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Infrastructure;

namespace Honua.Postgres.Tests.Features.Infrastructure;

public sealed class DatabaseSchemaTests
{
    [Fact]
    public void GetFeaturesTableName_WithValidSchema_QuotesSchemaAndTable()
    {
        var tableName = DatabaseSchema.GetFeaturesTableName("tenant_01");

        tableName.Should().Be("\"tenant_01\".\"features\"");
    }

    [Theory]
    [InlineData("tenant;DROP TABLE features")]
    [InlineData("tenant-name")]
    [InlineData("\"tenant\"")]
    public void GetFeaturesTableName_WithUnsafeSchema_Throws(string schemaName)
    {
        var action = () => DatabaseSchema.GetFeaturesTableName(schemaName);

        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("features;DROP TABLE services")]
    [InlineData("layer-fields")]
    [InlineData("\"features\"")]
    public void GetQualifiedTableName_WithUnsafeTableName_Throws(string tableName)
    {
        var action = () => DatabaseSchema.GetQualifiedTableName(tableName);

        action.Should().Throw<InvalidOperationException>();
    }
}
