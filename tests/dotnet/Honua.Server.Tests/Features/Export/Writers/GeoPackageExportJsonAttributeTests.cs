// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using Microsoft.Data.Sqlite;

namespace Honua.Server.Tests.Features.Export.Writers;

public sealed class GeoPackageExportJsonAttributeTests
{
    [Theory]
    [InlineData("String", "\"alpha\"", "alpha", "text")]
    [InlineData("String", "\"\"", "", "text")]
    [InlineData("Integer", "42", 42L, "integer")]
    [InlineData("BigInteger", "9007199254740993", 9007199254740993L, "integer")]
    [InlineData("Double", "1.25", 1.25, "real")]
    [InlineData("Float", "-2.5", -2.5, "real")]
    [InlineData("Boolean", "true", 1L, "integer")]
    [InlineData("Boolean", "false", 0L, "integer")]
    [InlineData("Integer", "0", 0L, "integer")]
    [InlineData("String", "null", null, "null")]
    [InlineData("String", null, null, "null")]
    [InlineData("Json", "{\"nested\":[1,true]}", "{\"nested\":[1,true]}", "text")]
    [InlineData("Json", "[1,2]", "[1,2]", "text")]
    [InlineData("Json", "true", "true", "text")]
    [InlineData("Json", "\"quoted\"", "\"quoted\"", "text")]
    [InlineData("Date", "\"2026-11-15\"", "2026-11-15", "text")]
    [InlineData("DateTime", "0", "1970-01-01T00:00:00.0000000Z", "text")]
    [InlineData("Binary", "\"AAEC/w==\"", "000102FF", "blob")]
    [InlineData("Binary", "\"not base64!\"", "not base64!", "text")]
    public async Task WriteAsync_WithJsonAttribute_PreservesSqliteValue(
        string fieldType, string? json, object? expected, string expectedStorageType)
    {
        var type = Enum.Parse<ExportFieldType>(fieldType);
        var value = ParseAttribute(json);
        var feature = Feature.Create(1, null,
            ImmutableDictionary<string, object?>.Empty.Add("value", value));
        // False positive: the generated relative filename cannot replace the temporary directory.
        var path = Path.Join(Path.GetTempPath(), $"gpkg-json-{Guid.NewGuid():N}.gpkg");
        try
        {
            var count = await GeoPackageExportWriter.WriteAsync(path, Enumerate(feature),
                [new ExportField("value", type, true)], ExportGeometryType.None,
                4326, "EPSG:4326", null, CancellationToken.None);
            count.Should().Be(1);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value, typeof(value) FROM features";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(1).Should().Be(expectedStorageType);
            var actual = reader.GetValue(0);
            if (expectedStorageType == "blob")
                Convert.ToHexString((byte[])actual).Should().Be((string)expected!);
            else
                actual.Should().Be(expected ?? DBNull.Value);
            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static JsonElement ParseAttribute(string? json)
    {
        if (json is null)
            return default;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async IAsyncEnumerable<Feature> Enumerate(Feature feature)
    {
        yield return feature;
        await Task.Yield();
    }
}
