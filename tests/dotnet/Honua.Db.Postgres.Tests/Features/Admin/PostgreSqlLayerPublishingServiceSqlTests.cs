// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Admin.Domain;
using Honua.Postgres.Features.Admin;

namespace Honua.Postgres.Tests.Features.Admin;

public sealed class PostgreSqlLayerPublishingServiceSqlTests
{
    [Fact]
    public void BuildAttributesExpression_WithWideTables_ChunksJsonbBuildObjectCalls()
    {
        var columns = Enumerable.Range(1, 51)
            .Select(index => new ColumnInfo
            {
                Name = $"field_{index}",
                DataType = "text"
            })
            .ToArray();

        var method = typeof(PostgreSqlLayerPublishingService).GetMethod(
            "BuildAttributesExpression",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var expression = (string)method!.Invoke(null, [columns])!;

        expression.Split("jsonb_build_object", StringSplitOptions.None).Length.Should().Be(3);
        expression.Should().Contain(" || ");
        expression.Should().Contain("'field_1', src.\"field_1\"");
        expression.Should().Contain("'field_51', src.\"field_51\"");
    }
}
