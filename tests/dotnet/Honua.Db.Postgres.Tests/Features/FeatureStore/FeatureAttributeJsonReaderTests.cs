// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Xunit.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureAttributeJsonReaderTests(ITestOutputHelper output)
{
    [Fact]
    public void ReadInto_ScalarRow_AllocatesLessThanDictionaryDeserialization()
    {
        // Measure thread-local allocations, not elapsed time: this remains useful on a
        // shared host. Include the unchanged immutable copy performed by both readers.
        const string json = """{"id":12345,"feature_name":"Point 12345","category":"A","temperature":23.5,"humidity":64.2,"elevation":125.5,"population":1000,"active":true,"status":"open","region":"west","timestamp":"2026-09-26T00:00:00Z"}""";
        var destination = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        void ReadPrevious()
        {
            var temporary = JsonSerializer.Deserialize(json, FeatureAttributesJsonContext.Default.DictionaryStringObject)!;
            foreach (var (key, value) in temporary)
            {
                destination[key] = value is JsonElement element
                    ? JsonElementConverter.ConvertToScalar(element)
                    : value;
            }
        }

        long Measure(Action read)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 1000; iteration++)
            {
                read();
                GC.KeepAlive(destination.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
                destination.Clear();
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
        }

        Action readCurrent = () => FeatureAttributeJsonReader.ReadInto(json, destination);
        _ = Measure(ReadPrevious);
        _ = Measure(readCurrent);
        var previousBytes = Measure(ReadPrevious);
        var currentBytes = Measure(readCurrent);

        output.WriteLine($"Allocated bytes per 11-field row, including immutable copy: previous={previousBytes}, current={currentBytes}");
        currentBytes.Should().BeLessThan(previousBytes * 7 / 10,
            "scalar attributes should avoid the temporary dictionary and per-value JSON documents");
    }

    [Fact]
    public void ReadInto_Scalars_PreservesTypesAndValues()
    {
        var attributes = new Dictionary<string, object?>();

        FeatureAttributeJsonReader.ReadInto(
            """{"id":9223372036854775807,"temperature":12.5,"active":true,"retired":false,"missing":null,"name":"","date":"2026-09-26T00:00:00Z"}""",
            attributes);

        attributes["id"].Should().Be(long.MaxValue);
        attributes["temperature"].Should().Be(12.5d);
        attributes["active"].Should().Be(true);
        attributes["retired"].Should().Be(false);
        attributes["missing"].Should().BeNull();
        attributes["name"].Should().Be("");
        attributes["date"].Should().Be("2026-09-26T00:00:00Z");
    }

    [Fact]
    public void ReadInto_NestedValues_RemainUsableAfterDocumentIsDisposed()
    {
        var attributes = new Dictionary<string, object?>();

        FeatureAttributeJsonReader.ReadInto("""{"object":{"name":"Honolulu"},"array":[1,null,{"ok":true}]}""", attributes);

        var nested = attributes["object"].Should().BeOfType<JsonElement>().Subject;
        nested.GetProperty("name").GetString().Should().Be("Honolulu");
        var array = attributes["array"].Should().BeOfType<JsonElement>().Subject;
        array[2].GetProperty("ok").GetBoolean().Should().BeTrue();
        array.GetRawText().Should().Be("""[1,null,{"ok":true}]""");
    }

    [Fact]
    public void ReadInto_ReusedDictionary_PreservesEscapesAndDoesNotRetainPreviousRows()
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        FeatureAttributeJsonReader.ReadInto("""{"Name":"Kāneʻohe","nested":{"id":1}}""", attributes);
        var firstRow = attributes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        attributes.Clear();

        FeatureAttributeJsonReader.ReadInto("""{"na\u006de":"quote\" and newline\n"}""", attributes);

        attributes.Should().ContainSingle().Which.Value.Should().Be("quote\" and newline\n");
        attributes["NAME"].Should().Be("quote\" and newline\n");
        firstRow["name"].Should().Be("Kāneʻohe");
        ((JsonElement)firstRow["nested"]!).GetProperty("id").GetInt64().Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("null")]
    [InlineData("{}")]
    public void ReadInto_AbsentAttributes_LeavesDestinationEmpty(string? json)
    {
        var attributes = new Dictionary<string, object?>();
        FeatureAttributeJsonReader.ReadInto(json, attributes);
        attributes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{invalid}")]
    public void ReadInto_InvalidAttributes_ThrowsJsonException(string json)
    {
        var read = () => FeatureAttributeJsonReader.ReadInto(json, new Dictionary<string, object?>());
        read.Should().Throw<JsonException>();
    }
}
