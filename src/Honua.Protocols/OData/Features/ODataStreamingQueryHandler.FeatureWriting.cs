// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;

namespace Honua.Protocols.OData;

internal sealed partial class ODataStreamingQueryHandler
{
    private sealed class ODataFeatureWriteContext
    {
        public required int LayerId { get; init; }
        public int? LayerSrid { get; init; }
        public required AxisOrder AxisOrder { get; init; }
        public HashSet<string>? Select { get; init; }
        public required IReadOnlySet<string> HiddenFields { get; init; }
        public ImmutableArray<ODataComputeExpression> ComputeExpressions { get; init; }
        public required IGeometryService GeometryService { get; init; }
        public CancellationToken CancellationToken { get; init; }
    }

    private static void WriteODataFeature(
        Utf8JsonWriter writer,
        Feature feature,
        ODataFeatureWriteContext context)
    {
        writer.WriteStartObject();
        var requiresCompute = !context.ComputeExpressions.IsDefaultOrEmpty;
        HashSet<string>? writtenProperties = requiresCompute
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;

        writer.WriteNumber("ObjectId", feature.Id);
        writer.WriteNumber("LayerId", context.LayerId);
        if (writtenProperties is not null)
        {
            _ = writtenProperties.Add("ObjectId");
            _ = writtenProperties.Add("LayerId");
        }

        WriteODataFeatureGeometry(writer, feature, (context, writtenProperties));
        var computeSource = WriteODataFeatureAttributes(writer, feature, (context, writtenProperties));
        if (computeSource is not null)
        {
            WriteODataComputedProperties(writer, computeSource, (context, writtenProperties));
        }

        writer.WriteEndObject();
        context.CancellationToken.ThrowIfCancellationRequested();
    }

    private static void WriteODataFeatureGeometry(
        Utf8JsonWriter writer,
        Feature feature,
        (ODataFeatureWriteContext Context, HashSet<string>? WrittenProperties) state)
    {
        var (context, writtenProperties) = state;
        if (context.Select is not null && !context.Select.Contains("Geometry"))
        {
            return;
        }

        var geometry = ODataGeometryConverter.ConvertWkbToGeometry(
            context.GeometryService,
            feature.Geometry,
            context.LayerSrid,
            context.AxisOrder);
        if (geometry is not null)
        {
            writer.WritePropertyName("Geometry");
            JsonSerializer.Serialize(writer, geometry, ODataJsonContext.Default.ODataSpatialGeometry);
        }
        else
        {
            writer.WriteNull("Geometry");
        }

        writtenProperties?.Add("Geometry");
    }

    private static Dictionary<string, object?>? WriteODataFeatureAttributes(
        Utf8JsonWriter writer,
        Feature feature,
        (ODataFeatureWriteContext Context, HashSet<string>? WrittenProperties) state)
    {
        var (context, writtenProperties) = state;
        var requiresCompute = !context.ComputeExpressions.IsDefaultOrEmpty;
        var computeSource = requiresCompute
            ? CreateComputeSource(feature, context.LayerId)
            : null;
        if (feature.Attributes is null)
        {
            return computeSource;
        }

        foreach (var kvp in feature.Attributes)
        {
            if (ODataUtilityService.IsReservedFeatureProperty(kvp.Key) || context.HiddenFields.Contains(kvp.Key))
            {
                continue;
            }

            var normalizedValue = ODataAttributeSerializer.NormalizeODataValue(kvp.Value);
            if (computeSource is not null)
            {
                computeSource[kvp.Key] = normalizedValue;
            }

            if ((context.Select is null || context.Select.Contains(kvp.Key)) &&
                (writtenProperties is null || writtenProperties.Add(kvp.Key)))
            {
                WriteODataJsonValue(writer, kvp.Key, normalizedValue);
            }
        }

        return computeSource;
    }

    private static Dictionary<string, object?> CreateComputeSource(Feature feature, int layerId)
        => new((feature.Attributes?.Count ?? 0) + 2, StringComparer.OrdinalIgnoreCase)
        {
            ["ObjectId"] = feature.Id,
            ["LayerId"] = layerId
        };

    private static void WriteODataComputedProperties(
        Utf8JsonWriter writer,
        Dictionary<string, object?> computeSource,
        (ODataFeatureWriteContext Context, HashSet<string>? WrittenProperties) state)
    {
        var (context, writtenProperties) = state;
        ODataComputeService.ApplyCompute(computeSource, context.ComputeExpressions);
        foreach (var expression in context.ComputeExpressions)
        {
            if (context.Select is not null && !context.Select.Contains(expression.Alias))
            {
                continue;
            }

            if (writtenProperties is not null && !writtenProperties.Add(expression.Alias))
            {
                continue;
            }

            if (computeSource.TryGetValue(expression.Alias, out var computedValue))
            {
                WriteODataJsonValue(writer, expression.Alias, computedValue);
            }
            else
            {
                writer.WriteNull(expression.Alias);
            }
        }
    }

    private static void WriteODataJsonValue(Utf8JsonWriter writer, string propertyName, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNull(propertyName); break;
            case string s: writer.WriteString(propertyName, s); break;
            case int i: writer.WriteNumber(propertyName, i); break;
            case long l: writer.WriteNumber(propertyName, l); break;
            case double d: writer.WriteNumber(propertyName, d); break;
            case float f: writer.WriteNumber(propertyName, f); break;
            case decimal dec: writer.WriteNumber(propertyName, dec); break;
            case bool b: writer.WriteBoolean(propertyName, b); break;
            case DateTime dt: WriteFormattedDateTime(writer, propertyName, dt); break;
            case DateTimeOffset dto: WriteFormattedDateTimeOffset(writer, propertyName, dto); break;
            default:
                writer.WritePropertyName(propertyName);
                JsonSerializer.Serialize(writer, value, ODataJsonContext.Default.Object);
                break;
        }
    }

    private static void WriteFormattedDateTime(Utf8JsonWriter writer, string propertyName, DateTime value)
    {
        Span<char> buffer = stackalloc char[33];
        if (value.TryFormat(buffer, out var written, "O", CultureInfo.InvariantCulture))
        {
            writer.WriteString(propertyName, buffer[..written]);
            return;
        }

        writer.WriteString(propertyName, value.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void WriteFormattedDateTimeOffset(Utf8JsonWriter writer, string propertyName, DateTimeOffset value)
    {
        Span<char> buffer = stackalloc char[33];
        if (value.TryFormat(buffer, out var written, "O", CultureInfo.InvariantCulture))
        {
            writer.WriteString(propertyName, buffer[..written]);
            return;
        }

        writer.WriteString(propertyName, value.ToString("O", CultureInfo.InvariantCulture));
    }
}
