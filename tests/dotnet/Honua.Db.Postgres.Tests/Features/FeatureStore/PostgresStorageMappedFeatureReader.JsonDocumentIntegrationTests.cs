// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    [Theory]
    [InlineData(false, "jsonb")]
    [InlineData(true, "text")]
    public async Task BuildFeatureSelect_AttributeWireType_PreservesDistinctTextSemantics(bool distinct, string expectedType)
    {
        var query = new FeatureQuery { OutFields = ["name"], Limit = 100, Distinct = distinct };
        var sql = typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(CreateReader(), [query, false])!;
        var parameters = (IReadOnlyList<object?>)sql.GetType().GetProperty("Parameters")!.GetValue(sql)!;
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        foreach (var value in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetDataTypeName(2).Should().Be(expectedType,
            "ordinary reads should avoid a UTF-16 round trip while distinct comparison/order semantics remain text-based");
    }
}
