// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Esri GeoServices f=json serialization-compatibility regression tests:
/// (1) esriFieldTypeString fields must report a positive <c>length</c>;
/// (2) esriFieldTypeDate values must serialize as epoch-millisecond integers (not ISO strings).
/// </summary>
public sealed class GeoServicesFieldSerializationTests
{
    private const int DefaultStringLength = 256;

    // ----- Bug 1: string field length -----

    [Fact]
    public async Task Json_StringFieldWithoutDeclaredLength_ReportsDefaultPositiveLength()
    {
        var (formatter, _) = CreateFormatter();
        var resource = CreateResource(
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "description", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "category", Type = MetadataV2FieldType.String });

        var (response, _) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(0, []),
            resource,
            format: "json",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        foreach (var fieldName in new[] { "name", "description", "category" })
        {
            var field = queryResponse.Fields!.Single(f => f.Name == fieldName);
            field.Type.Should().Be("esriFieldTypeString");
            field.Length.Should().Be(DefaultStringLength,
                "string fields must report a positive length so arcpy does not map null -> 0");
        }
    }

    [Fact]
    public async Task Json_StringFieldWithDeclaredLength_PreservesDeclaredLength()
    {
        var (formatter, _) = CreateFormatter();
        var resource = CreateResource(
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "code", Type = MetadataV2FieldType.String, Length = 32 });

        var (response, _) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(0, []),
            resource,
            format: "json",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        queryResponse.Fields!.Single(f => f.Name == "code").Length.Should().Be(32);
    }

    [Fact]
    public async Task Json_RuntimeStringField_ReportsPositiveLength()
    {
        var (formatter, _) = CreateFormatter();
        var resource = CreateResource(
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false });

        var feature = Feature.Create(
            1,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                // Undeclared runtime string attribute -> inferred field metadata.
                ["runtime_label"] = "hello"
            }.ToImmutableDictionary());

        var (response, _) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            resource,
            format: "json",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        var queryResponse = response.Should().BeOfType<QueryResponse>().Subject;
        var runtimeField = queryResponse.Fields!.Single(f => f.Name == "runtime_label");
        runtimeField.Type.Should().Be("esriFieldTypeString");
        runtimeField.Length.Should().Be(DefaultStringLength);
    }

    // ----- Bug 2: date as epoch-ms (streaming f=json path) -----

    [Fact]
    public async Task Streaming_Json_DateFields_AreEmittedAsEpochMilliseconds()
    {
        var formatter = new StreamingQueryFormatter(Options.Create(new LimitsOptions()));
        var resource = CreateResource(
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.DateTime },
            new MetadataV2Field { Name = "created_date", Type = MetadataV2FieldType.Date });

        var expectedTimestamp = new DateTimeOffset(2023, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var expectedCreated = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        // Arrives the way the canonical attribute pipeline delivers it after a JSON round-trip:
        // the datetime as an ISO string, the date as a date-only string.
        var feature = Feature.Create(
            1,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["timestamp"] = "2023-01-02T00:00:00Z",
                ["created_date"] = "2024-06-15"
            }.ToImmutableDictionary());

        var json = await StreamGeoServicesJsonAsync(formatter, feature, resource);
        using var document = JsonDocument.Parse(json);
        var attributes = document.RootElement.GetProperty("features")[0].GetProperty("attributes");

        attributes.GetProperty("timestamp").ValueKind.Should().Be(JsonValueKind.Number);
        attributes.GetProperty("timestamp").GetInt64().Should().Be(expectedTimestamp);
        attributes.GetProperty("created_date").ValueKind.Should().Be(JsonValueKind.Number);
        attributes.GetProperty("created_date").GetInt64().Should().Be(expectedCreated);
    }

    [Fact]
    public async Task Streaming_Json_DateValues_HandleDateTimeAndDateOnlyClrTypes()
    {
        var formatter = new StreamingQueryFormatter(Options.Create(new LimitsOptions()));
        var resource = CreateResource(
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.DateTime },
            new MetadataV2Field { Name = "created_date", Type = MetadataV2FieldType.Date });

        var dt = new DateTime(2023, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var dateOnly = new DateOnly(2024, 6, 15);
        var expectedTimestamp = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        var expectedCreated = new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        var feature = Feature.Create(
            1,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["timestamp"] = dt,
                ["created_date"] = dateOnly
            }.ToImmutableDictionary());

        var json = await StreamGeoServicesJsonAsync(formatter, feature, resource);
        using var document = JsonDocument.Parse(json);
        var attributes = document.RootElement.GetProperty("features")[0].GetProperty("attributes");

        attributes.GetProperty("timestamp").GetInt64().Should().Be(expectedTimestamp);
        attributes.GetProperty("created_date").GetInt64().Should().Be(expectedCreated);
    }

    [Fact]
    public async Task Streaming_Json_AlreadyEpochValue_IsNotDoubleConverted()
    {
        var formatter = new StreamingQueryFormatter(Options.Create(new LimitsOptions()));
        var resource = CreateResource(
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.DateTime });

        var epoch = new DateTimeOffset(2023, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var feature = Feature.Create(
            1,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["timestamp"] = epoch
            }.ToImmutableDictionary());

        var json = await StreamGeoServicesJsonAsync(formatter, feature, resource);
        using var document = JsonDocument.Parse(json);
        var attributes = document.RootElement.GetProperty("features")[0].GetProperty("attributes");

        attributes.GetProperty("timestamp").GetInt64().Should().Be(epoch);
    }

    // ----- Bug 2: object (non-streaming) f=json path -----

    [Fact]
    public async Task Json_ObjectPath_DateFields_AreEmittedAsEpochMilliseconds()
    {
        var (formatter, _) = CreateFormatter();
        var resource = CreateResource(
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.DateTime },
            new MetadataV2Field { Name = "created_date", Type = MetadataV2FieldType.Date });

        var expectedTimestamp = new DateTimeOffset(2023, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var expectedCreated = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var feature = Feature.Create(
            1,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["timestamp"] = "2023-01-02T00:00:00Z",
                ["created_date"] = "2024-06-15"
            }.ToImmutableDictionary());

        var (response, _) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]),
            resource,
            format: "json",
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null);

        var json = JsonSerializer.Serialize(response, FeatureServerJsonContext.Default.QueryResponse);
        using var document = JsonDocument.Parse(json);
        var attributes = document.RootElement.GetProperty("features")[0].GetProperty("attributes");

        attributes.GetProperty("timestamp").ValueKind.Should().Be(JsonValueKind.Number);
        attributes.GetProperty("timestamp").GetInt64().Should().Be(expectedTimestamp);
        attributes.GetProperty("created_date").GetInt64().Should().Be(expectedCreated);
    }

    // ----- Bug 2 negative: GeoServices f=geojson keeps ISO date strings -----

    [Fact]
    public async Task Streaming_GeoJson_DateStrings_RemainIso()
    {
        var formatter = new StreamingQueryFormatter(Options.Create(new LimitsOptions()));
        var resource = CreateResource(
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.DateTime });

        var feature = Feature.Create(
            1,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["timestamp"] = "2023-01-02T00:00:00Z"
            }.ToImmutableDictionary());

        var json = await StreamGeoJsonAsync(formatter, feature, resource);
        using var document = JsonDocument.Parse(json);
        var properties = document.RootElement.GetProperty("features")[0].GetProperty("properties");

        properties.GetProperty("timestamp").ValueKind.Should().Be(JsonValueKind.String);
        properties.GetProperty("timestamp").GetString().Should().Be("2023-01-02T00:00:00Z");
    }

    private static (QueryFormatter Formatter, LimitsOptions Limits) CreateFormatter()
    {
        var limitsOptions = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(
            limitsOptions,
            new PbfQueryFormatter(limitsOptions),
            NullLogger<QueryFormatter>.Instance);
        return (formatter, limitsOptions.Value);
    }

    private static async Task<string> StreamGeoServicesJsonAsync(
        StreamingQueryFormatter formatter,
        Feature feature,
        MetadataV2Resource resource)
    {
        using var stream = new MemoryStream();
        var pipe = PipeWriter.Create(stream);
        await formatter.StreamAsGeoServicesJsonAsync(
            ToAsyncEnumerable(feature),
            resource,
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: null,
            hasMoreResults: false,
            outputStream: pipe);
        await pipe.FlushAsync();
        await pipe.CompleteAsync();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<string> StreamGeoJsonAsync(
        StreamingQueryFormatter formatter,
        Feature feature,
        MetadataV2Resource resource)
    {
        using var stream = new MemoryStream();
        var pipe = PipeWriter.Create(stream);
        await formatter.StreamAsGeoJsonAsync(
            ToAsyncEnumerable(feature),
            resource,
            returnGeometry: false,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: null,
            hasMoreResults: false,
            outputStream: pipe);
        await pipe.FlushAsync();
        await pipe.CompleteAsync();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async IAsyncEnumerable<Feature> ToAsyncEnumerable(Feature feature)
    {
        yield return feature;
        await Task.CompletedTask;
    }

    private static MetadataV2Resource CreateResource(params MetadataV2Field[] fields)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "test-layer", Name = "test-layer" },
            SchemaFields = [.. fields]
        };
}
