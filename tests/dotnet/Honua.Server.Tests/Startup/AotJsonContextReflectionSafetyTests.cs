// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.DuckDB.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.OData.Models;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Console;
using Honua.Server.Features.Streaming;
using Honua.Server.Features.Styling;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Startup;

[Protocol(TestProtocols.TestQuality)]
public sealed class AotJsonContextReflectionSafetyTests
{
    public static TheoryData<string, JsonSerializerContext> AttributeSerializationContexts => new()
    {
        { nameof(OgcJsonContext), OgcJsonContext.Default },
        { nameof(ODataJsonContext), ODataJsonContext.Default },
        { nameof(FeatureServerJsonContext), FeatureServerJsonContext.Default },
        { nameof(FeatureAttributesJsonContext), FeatureAttributesJsonContext.Default },
        { nameof(FeatureStreamJsonContext), FeatureStreamJsonContext.Default },
        { nameof(FeatureChangeEventsJsonContext), FeatureChangeEventsJsonContext.Default },
        { nameof(DuckDbFeatureAttributesJsonContext), DuckDbFeatureAttributesJsonContext.Default },
        { nameof(ImageServerJsonContext), ImageServerJsonContext.Default },
    };

    [UnitTest]
    public void StyleJsonContext_SerializesCanonicalScalarsWithoutReflection()
    {
        using var sampleJson = JsonDocument.Parse("""{"nested":true}""");
        var payload = new Dictionary<string, object?>
        {
            ["string"] = "roads",
            ["bool"] = true,
            ["int"] = 2,
            ["long"] = 5L,
            ["float"] = 1.5f,
            ["double"] = 2.75d,
            ["decimal"] = 3.25m,
            ["ints"] = new[] { 1, 2, 3 },
            ["doubles"] = new[] { 0.5d, 1.5d },
            ["strings"] = new[] { "a", "b" },
            ["list"] = new List<object?> { "get", "width", 2L, false },
            ["element"] = sampleJson.RootElement.Clone(),
        };

        var json = JsonSerializer.Serialize(payload, StyleJsonContext.Default.DictionaryStringObject);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        root.GetProperty("string").GetString().Should().Be("roads");
        root.GetProperty("bool").GetBoolean().Should().BeTrue();
        root.GetProperty("int").GetInt32().Should().Be(2);
        root.GetProperty("long").GetInt64().Should().Be(5L);
        root.GetProperty("float").GetSingle().Should().Be(1.5f);
        root.GetProperty("double").GetDouble().Should().Be(2.75d);
        root.GetProperty("decimal").GetDecimal().Should().Be(3.25m);
    }

    [UnitTest]
    public void AdminSldStyleEndpoints_BuildMapLibreStyleElement_SerializesIntegersAndBooleans()
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res.roads", Name = "roads" },
            Spatial = new MetadataV2ObjectSpatial { GeometryType = MetadataV2GeometryType.LineString },
        };
        var layers = new[]
        {
            new MapLibreStyleLayer
            {
                Id = "roads-line",
                Type = "line",
                Paint = new Dictionary<string, MapLibreExpression>
                {
                    ["line-color"] = new("#ff0000"),
                    ["line-width"] = new(2d),
                    ["circle-radius"] = new(5d),
                },
                Layout = new Dictionary<string, MapLibreExpression>
                {
                    ["line-cap"] = new("round"),
                    ["symbol-avoid-edges"] = new(true),
                },
            },
        };

        var styleElement = AdminSldStyleEndpoints.BuildMapLibreStyleElement(resource, 7, layers);

        styleElement.GetProperty("version").GetInt32().Should().Be(8);
        var layer = styleElement.GetProperty("layers")[0];
        layer.GetProperty("source").GetString().Should().Be("layer-7");
        layer.GetProperty("source-layer").GetString().Should().Be(StyleDefaults.SourceLayerName);
        layer.GetProperty("paint").GetProperty("line-width").GetDouble().Should().Be(2d);
        layer.GetProperty("paint").GetProperty("circle-radius").GetDouble().Should().Be(5d);
        layer.GetProperty("layout").GetProperty("symbol-avoid-edges").GetBoolean().Should().BeTrue();
    }

    [UnitTest]
    public void ProblemJsonContext_ResolvesProblemDetailsIndependentlyOfFeatureServer()
    {
        var problemTypeInfo = ProblemJsonContext.Default.GetTypeInfo(typeof(ProblemDetails))
            as JsonTypeInfo<ProblemDetails>;
        problemTypeInfo.Should().NotBeNull();

        var traceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var problem = new ProblemDetails
        {
            Type = "https://httpstatuses.com/400",
            Title = "Bad Request",
            Status = StatusCodes.Status400BadRequest,
            Detail = "Invalid query parameter",
            Extensions =
            {
                ["traceId"] = traceId,
                ["retryable"] = false,
                ["attempt"] = 1,
                ["elapsedMs"] = 15L,
                ["tags"] = new[] { "ogc", "validation" },
                ["fieldErrors"] = new Dictionary<string, string[]> { ["limit"] = ["Must be positive"] },
            },
        };

        var problemJson = JsonSerializer.Serialize(problem, problemTypeInfo!);
        using var problemDoc = JsonDocument.Parse(problemJson);
        problemDoc.RootElement.GetProperty("status").GetInt32().Should().Be(400);
        problemDoc.RootElement.GetProperty("traceId").GetGuid().Should().Be(traceId);
        problemDoc.RootElement.GetProperty("retryable").GetBoolean().Should().BeFalse();
    }

    [UnitTest]
    public void ProblemJsonContext_ResolvesValidationProblemDetailsIndependentlyOfFeatureServer()
    {
        var validationTypeInfo = ProblemJsonContext.Default.GetTypeInfo(typeof(HttpValidationProblemDetails))
            as JsonTypeInfo<HttpValidationProblemDetails>;
        validationTypeInfo.Should().NotBeNull();

        var validation = new HttpValidationProblemDetails(
            new Dictionary<string, string[]> { ["bbox"] = ["Must contain 4 or 6 numbers"] })
        {
            Title = "Validation failed",
            Status = StatusCodes.Status400BadRequest,
        };

        var validationJson = JsonSerializer.Serialize(validation, validationTypeInfo!);
        validationJson.Should().Contain("\"bbox\"").And.Contain("Must contain 4 or 6 numbers");
    }

    [UnitTheory]
    [MemberData(nameof(AttributeSerializationContexts))]
    public void FeatureAttributeContext_SerializesAllCanonicalAttributeClrTypesWithoutReflection(
        string contextName,
        JsonSerializerContext context)
    {
        var typeInfo = context.GetTypeInfo(typeof(Dictionary<string, object?>))
            as JsonTypeInfo<Dictionary<string, object?>>;
        typeInfo.Should().NotBeNull($"{contextName} must provide source-generated Dictionary<string, object?> metadata");

        using var nestedJson = JsonDocument.Parse("""{"code":42}""");
        var attributes = BuildCanonicalAttributeBag(nestedJson.RootElement.Clone());

        var json = JsonSerializer.Serialize(attributes, typeInfo!);

        using var parsed = JsonDocument.Parse(json);
        AssertCanonicalAttributeBag(parsed.RootElement, contextName);
    }

    [UnitTest]
    public void MapLibreExpressionConverter_DeserializesNestedArraysAndSkipsObjectsWithoutReflection()
    {
        const string json = """["match",["get","type"],"road",2,true,null,["literal",{"ignored":1}]]""";

        var expression = JsonSerializer.Deserialize(json, MapLibreStyleJsonContext.Default.MapLibreExpression);

        expression.Kind.Should().Be(MapLibreExpressionKind.Array);
        expression.Items.Should().HaveCount(7);
        expression.Items![0].StringValue.Should().Be("match");
        expression.Items[1].Items![1].StringValue.Should().Be("type");
        expression.Items[3].NumberValue.Should().Be(2d);
        expression.Items[4].BoolValue.Should().BeTrue();
        expression.Items[5].Kind.Should().Be(MapLibreExpressionKind.Null);
        expression.Items[6].Items![1].Kind.Should().Be(MapLibreExpressionKind.Null);
    }

    [UnitTest]
    public async Task OgcFeaturePayloadReader_ReadGeoJsonFeatureAsync_DeserializesViaSourceGeneratedTypeInfo()
    {
        const string body = """{"type":"Feature","id":"feat-1","geometry":{"type":"Point","coordinates":[-157.85,21.3]},"properties":{"name":"Honolulu","count":4}}""";
        using var services = new ServiceCollection()
            .AddOptions<LimitsOptions>()
            .Services
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.ContentType = "application/geo+json";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        httpContext.Request.ContentLength = body.Length;

        var (feature, errorResult, error) = await OgcFeaturePayloadReader.ReadGeoJsonFeatureAsync(
            httpContext,
            CancellationToken.None);

        errorResult.Should().BeNull();
        error.Should().BeNull();
        feature.Should().NotBeNull();
        feature!.Type.Should().Be("Feature");
        feature.Properties.Should().ContainKey("name");
    }

    [UnitTest]
    public void ConsoleRequestValidationHelpers_Validate_ValidatesAnnotatedDto()
    {
        ConsoleRequestValidationHelpers.Validate(new SampleValidatedDto { Name = "valid" }).Should().BeNull();

        var error = ConsoleRequestValidationHelpers.Validate(new SampleValidatedDto { Name = string.Empty });
        error.Should().NotBeNullOrWhiteSpace();
    }

    private static Dictionary<string, object?> BuildCanonicalAttributeBag(JsonElement jsonElement)
        => new(StringComparer.Ordinal)
        {
            ["string"] = "honua",
            ["bool"] = true,
            ["byte"] = (byte)7,
            ["sbyte"] = (sbyte)-7,
            ["short"] = (short)-123,
            ["ushort"] = (ushort)123,
            ["int"] = -42,
            ["uint"] = 42u,
            ["long"] = 9_000_000_000L,
            ["ulong"] = 18_000_000_000UL,
            ["float"] = 1.5f,
            ["double"] = 2.25d,
            ["decimal"] = 3.75m,
            ["dateTime"] = new DateTime(2026, 6, 15, 10, 30, 0, DateTimeKind.Utc),
            ["dateTimeOffset"] = new DateTimeOffset(2026, 6, 15, 10, 30, 0, TimeSpan.Zero),
            ["dateOnly"] = new DateOnly(2026, 6, 15),
            ["timeOnly"] = new TimeOnly(10, 30, 45),
            ["guid"] = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            ["bytes"] = new byte[] { 1, 2, 3 },
            ["jsonElement"] = jsonElement,
            ["objectArray"] = new object[] { "x", 1, true },
            ["stringArray"] = new[] { "a", "b" },
            ["intArray"] = new[] { 1, 2 },
            ["longArray"] = new[] { 10L, 20L },
            ["doubleArray"] = new[] { 1.5d, 2.5d },
            ["list"] = new List<object?> { "item", 99, false },
            ["nested"] = new Dictionary<string, object?> { ["flag"] = true, ["count"] = 3L },
        };

    private static void AssertCanonicalAttributeBag(JsonElement root, string contextName)
    {
        root.GetProperty("string").GetString().Should().Be("honua", contextName);
        root.GetProperty("bool").GetBoolean().Should().BeTrue(contextName);
        root.GetProperty("byte").GetByte().Should().Be(7, contextName);
        root.GetProperty("sbyte").GetSByte().Should().Be(-7, contextName);
        root.GetProperty("short").GetInt16().Should().Be(-123, contextName);
        root.GetProperty("ushort").GetUInt16().Should().Be(123, contextName);
        root.GetProperty("int").GetInt32().Should().Be(-42, contextName);
        root.GetProperty("uint").GetUInt32().Should().Be(42u, contextName);
        root.GetProperty("long").GetInt64().Should().Be(9_000_000_000L, contextName);
        root.GetProperty("ulong").GetUInt64().Should().Be(18_000_000_000UL, contextName);
        root.GetProperty("float").GetSingle().Should().Be(1.5f, contextName);
        root.GetProperty("double").GetDouble().Should().Be(2.25d, contextName);
        root.GetProperty("decimal").GetDecimal().Should().Be(3.75m, contextName);
        root.GetProperty("dateOnly").GetString().Should().Be("2026-06-15", contextName);
        root.GetProperty("timeOnly").GetString().Should().Be("10:30:45", contextName);
        root.GetProperty("guid").GetGuid().Should().Be(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), contextName);
        root.GetProperty("bytes").GetBytesFromBase64().Should().Equal([1, 2, 3]);
        root.GetProperty("jsonElement").GetProperty("code").GetInt32().Should().Be(42, contextName);
        root.GetProperty("stringArray").GetArrayLength().Should().Be(2, contextName);
        root.GetProperty("intArray").GetArrayLength().Should().Be(2, contextName);
        root.GetProperty("longArray").GetArrayLength().Should().Be(2, contextName);
        root.GetProperty("doubleArray").GetArrayLength().Should().Be(2, contextName);
        root.GetProperty("list").GetArrayLength().Should().Be(3, contextName);
        root.GetProperty("nested").GetProperty("count").GetInt64().Should().Be(3L, contextName);
    }

    private sealed class SampleValidatedDto
    {
        [Required]
        public string Name { get; set; } = string.Empty;
    }
}
