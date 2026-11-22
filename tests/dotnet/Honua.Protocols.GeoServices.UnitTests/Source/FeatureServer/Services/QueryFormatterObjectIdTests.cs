// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed partial class QueryFormatterTests
{
    private sealed record NullObjectIdCase(string Format, string Projection, string NullKind);

    public static IEnumerable<object[]> NullObjectIdCases()
    {
        foreach (var mode in new[] { "materialized", "streaming" })
        foreach (var format in new[] { "json", "geojson" })
        foreach (var projection in new[] { "*", "id,name", "name" })
        foreach (var nullKind in new[] { "clr", "json" })
        {
            yield return [new[] { mode, format, projection, nullKind }];
        }
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    [MemberData(nameof(NullObjectIdCases))]
    public async Task Query_NullDeclaredObjectId_UsesCanonicalIdentity(string[] values)
    {
        var testCase = new NullObjectIdCase(values[1], values[2], values[3]);
        var feature = Feature.Create(479, geometry: null,
            new Dictionary<string, object?>
            {
                ["id"] = testCase.NullKind == "json" ? JsonSerializer.SerializeToElement<object?>(null) : null,
                ["name"] = "nearby-row"
            }.ToImmutableDictionary());
        var json = values[0] == "streaming"
            ? await SerializeNullObjectIdStreamingAsync(testCase, feature)
            : await SerializeNullObjectIdMaterializedAsync(testCase, feature);

        using var document = JsonDocument.Parse(json);
        var row = document.RootElement.GetProperty("features")[0];
        var attributes = row.GetProperty(testCase.Format == "geojson" ? "properties" : "attributes");
        attributes.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Number);
        attributes.GetProperty("id").GetInt64().Should().Be(479);
        attributes.GetProperty("name").GetString().Should().Be("nearby-row");
        if (testCase.Format == "geojson")
        {
            row.GetProperty("id").GetInt64().Should().Be(479);
        }
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    [InlineData("*")]
    [InlineData("id,name")]
    [InlineData("name")]
    public async Task Query_ValidJsonObjectId_PreservesNumericWireType(string projection)
    {
        var feature = Feature.Create(479, geometry: null,
            new Dictionary<string, object?>
            {
                ["id"] = JsonSerializer.SerializeToElement(123L),
                ["name"] = "nearby-row"
            }.ToImmutableDictionary());
        var testCase = new NullObjectIdCase("json", projection, "json");
        var json = await SerializeNullObjectIdStreamingAsync(testCase, feature);
        using var document = JsonDocument.Parse(json);
        var attributes = document.RootElement.GetProperty("features")[0].GetProperty("attributes");
        attributes.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Number);
        attributes.GetProperty("id").GetInt64().Should().Be(123);
        attributes.GetProperty("name").GetString().Should().Be("nearby-row");
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    [InlineData("materialized", "json")]
    [InlineData("streaming", "json")]
    [InlineData("materialized", "geojson")]
    [InlineData("streaming", "geojson")]
    public async Task Query_LargeJsonObjectId_PreservesExactInteger(string mode, string format)
    {
        const long identifier = 9007199254740993L;
        var feature = Feature.Create(479, geometry: null,
            new Dictionary<string, object?>
            {
                ["id"] = JsonSerializer.SerializeToElement(identifier),
                ["name"] = "nearby-row"
            }.ToImmutableDictionary());
        var testCase = new NullObjectIdCase(format, "*", "json");
        var json = mode == "streaming"
            ? await SerializeNullObjectIdStreamingAsync(testCase, feature)
            : await SerializeNullObjectIdMaterializedAsync(testCase, feature);
        using var document = JsonDocument.Parse(json);
        var row = document.RootElement.GetProperty("features")[0];
        var attributes = row.GetProperty(format == "geojson" ? "properties" : "attributes");
        attributes.GetProperty("id").GetInt64().Should().Be(identifier);
        if (format == "geojson")
        {
            row.GetProperty("id").GetInt64().Should().Be(identifier);
        }
    }

    private static async Task<string> SerializeNullObjectIdMaterializedAsync(NullObjectIdCase testCase, Feature feature)
    {
        var limits = Options.Create(new LimitsOptions());
        var formatter = new QueryFormatter(limits, new PbfQueryFormatter(limits),
            NullLogger<QueryFormatter>.Instance);
        var (response, _) = await formatter.FormatQueryResultAsync(
            QueryResult<Feature>.Create(1, [feature]), CreateIdBackedPointLayer(), format: testCase.Format,
            returnGeometry: false, outputSrid: null, returnZ: false, returnM: false,
            geometryPrecision: null, maxAllowableOffset: null, outFields: testCase.Projection.Split(','));
        return testCase.Format == "geojson"
            ? JsonSerializer.Serialize((GeoJsonFeatureSet)response, FeatureServerJsonContext.Default.GeoJsonFeatureSet)
            : JsonSerializer.Serialize((QueryResponse)response, FeatureServerJsonContext.Default.QueryResponse);
    }

    private static async Task<string> SerializeNullObjectIdStreamingAsync(NullObjectIdCase testCase, Feature feature)
    {
        using var stream = new MemoryStream();
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        var formatter = new StreamingQueryFormatter(Options.Create(new LimitsOptions()));
        var resource = CreateIdBackedPointLayer();
        var outFields = testCase.Projection.Split(',');
        if (testCase.Format == "geojson")
        {
            await formatter.StreamAsGeoJsonAsync(
                Features(), resource, returnGeometry: false, returnZ: false, returnM: false,
                geometryPrecision: null, maxAllowableOffset: null, outFields: outFields,
                hasMoreResults: false, outputStream: writer);
        }
        else
        {
            await formatter.StreamAsGeoServicesJsonAsync(
                Features(), resource, returnGeometry: false, outputSrid: null,
                returnZ: false, returnM: false, geometryPrecision: null,
                maxAllowableOffset: null, outFields: outFields,
                hasMoreResults: false, outputStream: writer);
        }
        await writer.CompleteAsync();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());

        async IAsyncEnumerable<Feature> Features()
        {
            yield return feature;
            await Task.CompletedTask;
        }
    }
}
