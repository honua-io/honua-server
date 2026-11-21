// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class RawAttributeSelectionTests
{
    private const string ObjectIdFieldName = "OBJECTID";

    private static readonly string LongName = new('n', 300);

    private static readonly string[] AllowedNames =
        ["name", "Höhe", "event_date", "created_date", "Alpha", "quote\"d", LongName, "objectid", "__internal"];

    private static readonly Dictionary<string, MetadataV2FieldType> TemporalFieldTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["EVENT_DATE"] = MetadataV2FieldType.DateTime,
            ["created_date"] = MetadataV2FieldType.Date,
            [LongName] = MetadataV2FieldType.TimestampOffset,
        };

    [UnitTest]
    public void Choose_SelectsAndWritesTheNamesAndTypesThePropertyNamePathSelected()
    {
        var json = $$"""
            {"name":1,"NAME":2,"extra":3,"objectid":4,"ObjectId":5,"__internal":6,"__other":7,
             "Höhe":8,"höhe":9,"Alpha":10,"quote\"d":11,"Event_Date":12,"created_date":13,
             "{{LongName}}":14,"东京":15,"":16}
            """;

        var (actualBytes, actualTypes) = Write(json, WriteWithSelection);
        var (expectedBytes, expectedTypes) = Write(json, WriteWithPropertyName);

        actualTypes.Should().Equal(expectedTypes);
        System.Text.Encoding.UTF8.GetString(actualBytes).Should().Be(System.Text.Encoding.UTF8.GetString(expectedBytes));
        actualTypes.Should().HaveCount(9);
    }

    private static (byte[] Bytes, List<MetadataV2FieldType?> Types) Write(
        string json,
        Func<Utf8JsonWriter, JsonProperty, MetadataV2FieldType?[], bool> writeName)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        var types = new List<MetadataV2FieldType?>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var type = new MetadataV2FieldType?[1];
                if (writeName(writer, property, type))
                {
                    types.Add(type[0]);
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return (buffer.WrittenSpan.ToArray(), types);
    }

    // One instance for every property, as one response reuses it (and its name buffer) for all features.
    private static readonly RawAttributeSelection Selection = new(
        new HashSet<string>(AllowedNames, StringComparer.OrdinalIgnoreCase), TemporalFieldTypes, ObjectIdFieldName);

    private static bool WriteWithSelection(Utf8JsonWriter writer, JsonProperty property, MetadataV2FieldType?[] type)
    {
        if (Selection.Choose(property) is not { } choice)
        {
            return false;
        }

        RawAttributeSelection.WriteName(writer, property, choice);
        type[0] = choice.TemporalType;
        return true;
    }

    // The previous FeatureServerQueryExecutor logic, which read property.Name for each check.
    private static bool WriteWithPropertyName(Utf8JsonWriter writer, JsonProperty property, MetadataV2FieldType?[] type)
    {
        var allowed = new HashSet<string>(AllowedNames, StringComparer.OrdinalIgnoreCase);
        if (property.Name.StartsWith("__", StringComparison.Ordinal)
            || property.Name.Equals(ObjectIdFieldName, StringComparison.OrdinalIgnoreCase)
            || !allowed.Contains(property.Name))
        {
            return false;
        }

        writer.WritePropertyName(property.Name);
        type[0] = TemporalFieldTypes.TryGetValue(property.Name, out var temporalType) ? temporalType : null;
        return true;
    }
}
