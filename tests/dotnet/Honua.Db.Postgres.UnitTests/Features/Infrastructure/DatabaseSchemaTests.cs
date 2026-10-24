// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

[global::Xunit.Trait("Tier", "Fast")]
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
