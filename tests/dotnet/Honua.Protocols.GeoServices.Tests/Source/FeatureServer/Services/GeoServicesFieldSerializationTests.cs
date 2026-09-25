// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Helpers;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
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

    [Theory]
    [InlineData(MetadataV2FieldType.Json)]
    [InlineData(MetadataV2FieldType.Time)]
    public void Metadata_StringProjectedFieldsHavePositiveLength(MetadataV2FieldType fieldType)
    {
        var field = new MetadataV2Field { Name = "value", Type = fieldType, Nullable = true };
        var mapped = FeatureServerEndpoints.MapFieldInfoV2(field, FieldNames.ObjectId);

        mapped.Type.Should().Be("esriFieldTypeString");
        mapped.Length.Should().Be(DefaultStringLength);
    }

    [Theory]
    [InlineData(MetadataV2FieldType.String, "sqlTypeNVarchar")]
    [InlineData(MetadataV2FieldType.Integer, "sqlTypeInteger")]
    [InlineData(MetadataV2FieldType.DateTime, "sqlTypeOther")]
    [InlineData(MetadataV2FieldType.Json, "sqlTypeNVarchar")]
    public void Metadata_UsesEsriSqlTypeEnumeration(MetadataV2FieldType fieldType, string expected)
    {
        var field = new MetadataV2Field { Name = "value", Type = fieldType, Nullable = true };

        FeatureServerEndpoints.MapFieldInfoV2(field, FieldNames.ObjectId).SqlType.Should().Be(expected);
    }

    [UnitTest]
    public void QueryField_JsonColumn_AdvertisesAnEsriStringSqlType()
    {
        var mapped = QueryFormatter.MapFieldInfo(
            new MetadataV2Field { Name = "tags", Type = MetadataV2FieldType.Json, SqlType = "JSONB", Nullable = true },
            FieldNames.ObjectId);

        mapped.Type.Should().Be("esriFieldTypeString");
        mapped.SqlType.Should().Be("sqlTypeNVarchar");
    }

    [UnitTest]
    public void QueryField_JsonObject_IsTextAClientCanReadAsAString()
    {
        using var document = JsonDocument.Parse("""{"a":1}""");

        FeatureAttributeValueNormalizer.Normalize(document.RootElement.Clone())
            .Should().Be("""{"a":1}""");
    }

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

    [UnitTheory]
    [InlineData("string")]
    [InlineData("time")]
    [InlineData("duration")]
    public async Task Json_RuntimeStringField_ReportsEsriSqlTypeAndPositiveLength(string kind)
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
                ["runtime_label"] = kind switch
                {
                    "time" => new TimeOnly(12, 30),
                    "duration" => TimeSpan.FromMinutes(90),
                    _ => (object)"hello",
                }
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
        runtimeField.SqlType.Should().Be("sqlTypeNVarchar");
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
            new MetadataV2Field { Name = "created_date", Type = MetadataV2FieldType.DateTime });

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
            new MetadataV2Field { Name = "created_date", Type = MetadataV2FieldType.DateTime });

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
            new MetadataV2Field { Name = "created_date", Type = MetadataV2FieldType.DateTime });

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

    [Theory]
    [InlineData("buffered")]
    [InlineData("streaming")]
    [InlineData("top-features")]
    public async Task Json_CalendarDatesRemainIsoAndTimestampsRemainEpochs(string path)
    {
        var fields = new[]
        {
            new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger },
            new MetadataV2Field
            {
                Name = "day", Type = MetadataV2FieldType.Date,
                DefaultValue = JsonSerializer.SerializeToElement(
                    new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds())
            },
            new MetadataV2Field { Name = "nullable_day", Type = MetadataV2FieldType.Date, Nullable = true },
            new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.DateTime }
        };
        FeatureServerEndpoints.MapFieldInfoV2(fields[1], "objectid").Type.Should().Be("esriFieldTypeDateOnly");
        FeatureServerEndpoints.MapFieldInfoV2(fields[1], "objectid").DefaultValue.Should().Be("2024-02-29");
        var resource = CreateResource(fields);
        var epoch = new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        object[] calendarValues =
        [
            "2024-02-29", new DateOnly(2024, 2, 29),
            new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.FromHours(14)),
            JsonSerializer.SerializeToElement("2024-02-29"),
            "2024-02-29T00:00:00+14:00", epoch
        ];
        foreach (var value in calendarValues)
        {
            var feature = Feature.Create(1, null, new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["day"] = value,
                ["nullable_day"] = null,
                ["timestamp"] = "2024-02-29T00:00:00Z"
            }.ToImmutableDictionary());
            string json;
            if (path == "top-features")
            {
                var response = FeatureServerEndpoints.BuildTopFeaturesJsonResponse(
                    QueryResult<Feature>.Create(1, [feature]), resource, false, null);
                json = JsonSerializer.Serialize(response, FeatureServerJsonContext.Default.QueryResponse);
            }
            else if (path == "streaming")
                json = await StreamGeoServicesJsonAsync(new StreamingQueryFormatter(Options.Create(new LimitsOptions())), feature, resource);
            else
            {
                var (formatter, _) = CreateFormatter();
                var (response, _) = await formatter.FormatQueryResultAsync(QueryResult<Feature>.Create(1, [feature]),
                    resource, "json", false, null, false, false, null, null);
                json = JsonSerializer.Serialize(response, FeatureServerJsonContext.Default.QueryResponse);
            }
            using var document = JsonDocument.Parse(json);
            var attributes = document.RootElement.GetProperty("features")[0].GetProperty("attributes");
            attributes.GetProperty("day").GetString().Should().Be("2024-02-29");
            attributes.GetProperty("nullable_day").ValueKind.Should().Be(JsonValueKind.Null);
            attributes.GetProperty("timestamp").GetInt64().Should().Be(epoch);
            document.RootElement.GetProperty("fields").EnumerateArray()
                .Single(field => field.GetProperty("name").GetString() == "day")
                .GetProperty("type").GetString().Should().Be("esriFieldTypeDateOnly");
            document.RootElement.GetProperty("fields").EnumerateArray()
                .Single(field => field.GetProperty("name").GetString() == "day")
                .GetProperty("defaultValue").GetString().Should().Be("2024-02-29");
        }
    }

    // ----- Bug 3: array-valued columns must agree with esriFieldTypeString (#5171) -----

    [UnitTheory]
    [InlineData("[\"red\",\"blue\"]")]
    [InlineData("[0,1,2]")]
    [InlineData("{\"a\":1}")]
    public async Task Json_ArrayOrObjectValue_IsEmittedAsStringNotRawJson(string rawJson)
    {
        var (formatter, _) = CreateFormatter();
        var resource = CreateResource(
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "tags", Type = MetadataV2FieldType.String });

        using var value = JsonDocument.Parse(rawJson);
        var feature = Feature.Create(
            1,
            geometry: null,
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["tags"] = value.RootElement.Clone()
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

        var field = response.Should().BeOfType<QueryResponse>().Subject
            .Fields!.Single(f => f.Name == "tags");
        field.Type.Should().Be("esriFieldTypeString");

        attributes.GetProperty("tags").ValueKind.Should().Be(JsonValueKind.String,
            "GeoServices has no array or object type, so a value published as "
            + "esriFieldTypeString must be a JSON string; an arcpy cursor selecting a "
            + "field whose value disagrees with its declared type returns zero rows and "
            + "raises nothing (#5171)");
        attributes.GetProperty("tags").GetString().Should().Be(rawJson);
    }

    [UnitTheory]
    [InlineData("buffered", "[1,2]")]
    [InlineData("streaming", "[1,2]")]
    [InlineData("top-features", "[1,2]")]
    [InlineData("buffered", "{\"key\":\"value\"}")]
    [InlineData("streaming", "{\"key\":\"value\"}")]
    [InlineData("top-features", "{\"key\":\"value\"}")]
    public async Task Json_ComplexValue_IsAStringOnEveryQueryPath(string path, string rawJson)
    {
        // The buffered path was fixed first and the streaming path was not, which hid the
        // defect: a probe issuing a plain query saw a string, while ArcGIS Pro - which
        // sends orderByFields and resultOffset, and so takes the streaming path - still
        // received an array and silently stopped reading at that row. Pin all three.
        var resource = CreateResource(
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "tags", Type = MetadataV2FieldType.String });

        using var value = JsonDocument.Parse(rawJson);
        var feature = Feature.Create(1, null, new Dictionary<string, object?>
        {
            ["objectid"] = 1L,
            ["tags"] = value.RootElement.Clone()
        }.ToImmutableDictionary());

        string json;
        if (path == "top-features")
        {
            var response = FeatureServerEndpoints.BuildTopFeaturesJsonResponse(
                QueryResult<Feature>.Create(1, [feature]), resource, false, null);
            json = JsonSerializer.Serialize(response, FeatureServerJsonContext.Default.QueryResponse);
        }
        else if (path == "streaming")
        {
            json = await StreamGeoServicesJsonAsync(
                new StreamingQueryFormatter(Options.Create(new LimitsOptions())), feature, resource);
        }
        else
        {
            var (formatter, _) = CreateFormatter();
            var (response, _) = await formatter.FormatQueryResultAsync(
                QueryResult<Feature>.Create(1, [feature]), resource, "json", false, null, false, false, null, null);
            json = JsonSerializer.Serialize(response, FeatureServerJsonContext.Default.QueryResponse);
        }

        using var document = JsonDocument.Parse(json);
        var attributes = document.RootElement.GetProperty("features")[0].GetProperty("attributes");
        attributes.GetProperty("tags").ValueKind.Should().Be(JsonValueKind.String,
            "an esriFieldTypeString value must be a JSON string on every query path; "
            + "ArcGIS Pro stops reading the feature array at the first row that is not (#5171)");
        attributes.GetProperty("tags").GetString().Should().Be(rawJson);
    }

    [UnitTheory]
    [InlineData("buffered", "[1,2]", JsonValueKind.Array)]
    [InlineData("streaming", "[1,2]", JsonValueKind.Array)]
    [InlineData("buffered", "{\"key\":\"value\"}", JsonValueKind.Object)]
    [InlineData("streaming", "{\"key\":\"value\"}", JsonValueKind.Object)]
    public async Task GeoJson_ComplexValue_PreservesItsJsonType(string path, string rawJson, JsonValueKind expectedKind)
    {
        var resource = CreateResource(
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "tags", Type = MetadataV2FieldType.Json });
        using var value = JsonDocument.Parse(rawJson);
        var feature = Feature.Create(1, null, new Dictionary<string, object?>
        {
            ["objectid"] = 1L,
            ["tags"] = value.RootElement.Clone()
        }.ToImmutableDictionary());

        string json;
        if (path == "streaming")
        {
            json = await StreamGeoJsonAsync(
                new StreamingQueryFormatter(Options.Create(new LimitsOptions())), feature, resource);
        }
        else
        {
            var (formatter, _) = CreateFormatter();
            var (response, _) = await formatter.FormatQueryResultAsync(
                QueryResult<Feature>.Create(1, [feature]), resource, "geojson", false, null, false, false, null, null);
            json = JsonSerializer.Serialize(response, FeatureServerJsonContext.Default.GeoJsonFeatureSet);
        }

        using var document = JsonDocument.Parse(json);
        var property = document.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("tags");
        property.ValueKind.Should().Be(expectedKind, "GeoJSON accepts arrays and objects; the Esri string-field restriction must not leak into it");
        property.GetRawText().Should().Be(rawJson);
    }

    // ----- Bug 4: the query response must describe a field as the layer resource does (#5197) -----

    [UnitTest]
    public async Task Json_QueryFieldBlock_AgreesWithTheLayerResource()
    {
        // These two descriptions of the same field disagreed: the layer resource reported
        // sqlTypeInteger / sqlTypeNVarchar and a length of 256, while /query reported the
        // PostgreSQL names INTEGER and JSONB and omitted length entirely. A client that
        // trusts the declaration cannot read a field it is told is an unknown SQL type,
        // and a null length maps to 0 and breaks inserts.
        var fields = new[]
        {
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "tags", Type = MetadataV2FieldType.Json },
            new MetadataV2Field { Name = "event_time", Type = MetadataV2FieldType.Time },
            new MetadataV2Field { Name = "count", Type = MetadataV2FieldType.Integer },
            new MetadataV2Field { Name = "ratio", Type = MetadataV2FieldType.Double },
        };
        var resource = CreateResource(fields);

        var (formatter, _) = CreateFormatter();
        var (response, _) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(0, []), resource, "json", false, null, false, false, null, null);
        var queried = response.Should().BeOfType<QueryResponse>().Subject
            .Fields!.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            var declared = FeatureServerEndpoints.MapFieldInfoV2(field, FieldNames.ObjectId);
            var actual = queried[field.Name];

            actual.Type.Should().Be(declared.Type,
                "the query response and the layer resource must agree on {0}'s type", field.Name);
            actual.SqlType.Should().Be(declared.SqlType,
                "the query response and the layer resource must agree on {0}'s sqlType", field.Name);
            actual.SqlType.Should().StartWith("sqlType",
                "{0} must carry an Esri sqlType enumeration member, not a provider type name", field.Name);
            actual.Length.Should().Be(declared.Length,
                "the query response and the layer resource must agree on {0}'s length", field.Name);
        }

        // The two that used to differ, stated explicitly so a regression is unambiguous.
        queried["tags"].SqlType.Should().Be("sqlTypeNVarchar");
        queried["tags"].Length.Should().Be(DefaultStringLength);
        queried[FieldNames.ObjectId].SqlType.Should().Be("sqlTypeInteger");
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
