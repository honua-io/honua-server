// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Grpc.Core;
using Honua.Core.Features.Metadata.Domain.V2;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal static class GrpcMetadataProjector
{
    internal static Proto.LayerFieldDescriptor Field(MetadataV2Field field)
    {
        var result = new Proto.LayerFieldDescriptor
        {
            Definition = GrpcConversionHelpers.ToProtoField(field),
            Editable = field.Editable
        };
        if (field.DefaultValue is { } value)
        {
            result.DefaultValue = Value(value, field.Type);
        }
        if (field.Domain is { } domain)
        {
            result.Domain = Domain(domain, field.Type);
        }
        return result;
    }

    internal static Proto.AttributeValue Value(JsonElement value, MetadataV2FieldType? fieldType = null)
    {
        if (value.ValueKind != JsonValueKind.Null &&
            fieldType is MetadataV2FieldType.Decimal or MetadataV2FieldType.TimestampOffset)
        {
            return ExactValue(value, fieldType.Value);
        }
        return value.ValueKind switch
        {
            JsonValueKind.Null => new() { NullValue = Proto.NullValue.NullValue },
            JsonValueKind.String => new() { StringValue = value.GetString()! },
            JsonValueKind.True => new() { BoolValue = true },
            JsonValueKind.False => new() { BoolValue = false },
            JsonValueKind.Number when value.TryGetInt32(out var integer) => new() { Int32Value = integer },
            JsonValueKind.Number when value.TryGetInt64(out var integer) => new() { Int64Value = integer },
            JsonValueKind.Number => new() { DoubleValue = value.GetDouble() },
            JsonValueKind.Object or JsonValueKind.Array => new() { StringValue = value.GetRawText() },
            _ => throw new InvalidOperationException("Native attribute defaults and domain codes must be defined JSON values.")
        };
    }

    private static Proto.AttributeValue ExactValue(JsonElement value, MetadataV2FieldType fieldType)
    {
        try
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!,
                JsonValueKind.Number when fieldType == MetadataV2FieldType.Decimal => value.GetRawText(),
                _ => throw new ArgumentException("Exact metadata values must match their declared type.")
            };
            if (fieldType == MetadataV2FieldType.Decimal)
            {
                _ = ExactAttributeValues.ParseDecimal(text);
                return new() { DecimalValue = text };
            }
            _ = ExactAttributeValues.ParseTimestampOffset(text);
            return new() { TimestampOffsetValue = text };
        }
        catch (Exception exception) when (exception is ArgumentException or ExactValueRangeException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "Exact metadata value does not match its declared type."));
        }
    }

    internal static Proto.FieldDomain Domain(MetadataV2FieldDomain domain, MetadataV2FieldType? fieldType = null)
    {
        var result = new Proto.FieldDomain
        {
            Name = domain.Name ?? string.Empty,
            MergePolicy = domain.MergePolicy ?? string.Empty,
            SplitPolicy = domain.SplitPolicy ?? string.Empty
        };
        if (domain.Type.Equals("range", StringComparison.OrdinalIgnoreCase) && domain.Range is { Count: 2 } range)
        {
            result.Range = new() { Minimum = Value(range[0], fieldType), Maximum = Value(range[1], fieldType) };
        }
        else if (domain.Type.Equals("codedValue", StringComparison.OrdinalIgnoreCase) ||
                 domain.Type.Equals("codedValues", StringComparison.OrdinalIgnoreCase))
        {
            result.CodedValues = new();
            result.CodedValues.Values.AddRange(domain.CodedValues.Select(value => new Proto.DomainCodedValue
            {
                Code = Value(value.Code, fieldType),
                Name = value.Name
            }));
        }
        return result;
    }

    internal static Proto.SubtypeDefinition Subtypes(MetadataV2Subtypes subtypes, IReadOnlyList<MetadataV2Field> fields)
    {
        var result = new Proto.SubtypeDefinition { Field = subtypes.SubtypeField };
        var fieldType = fields.FirstOrDefault(field =>
            field.Name.Equals(subtypes.SubtypeField, StringComparison.OrdinalIgnoreCase))?.Type;
        if (subtypes.DefaultSubtypeCode is { } defaultCode)
        {
            result.DefaultCode = Value(defaultCode, fieldType);
        }
        foreach (var subtype in subtypes.Subtypes)
        {
            var projected = new Proto.LayerSubtype { Code = Value(subtype.Code, fieldType), Name = subtype.Name };
            var template = Template(subtype.Name, fields);
            template.DefaultAttributes[subtypes.SubtypeField] = projected.Code.Clone();
            AddSubtypeOverrides(projected, template, new(subtype, fields));
            projected.Templates.Add(template);
            result.Subtypes.Add(projected);
        }
        return result;
    }

    private sealed record SubtypeProjection(MetadataV2Subtype Subtype, IReadOnlyList<MetadataV2Field> Fields);

    private static void AddSubtypeOverrides(Proto.LayerSubtype projected, Proto.FeatureTemplate template,
        SubtypeProjection projection)
    {
        foreach (var (name, fieldOverride) in projection.Subtype.FieldOverrides)
        {
            if (projection.Fields.FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } field)
            {
                continue;
            }
            var canonicalName = field.Name;
            var entry = new Proto.SubtypeFieldOverride();
            if (fieldOverride.DefaultValue is { } value)
            {
                entry.DefaultValue = Value(value, field.Type);
                if (field.Editable)
                {
                    template.DefaultAttributes[canonicalName] = entry.DefaultValue.Clone();
                }
            }
            if (fieldOverride.Domain is { } domain)
            {
                entry.Domain = Domain(domain, field.Type);
            }
            projected.FieldOverrides[canonicalName] = entry;
        }
    }

    internal static Proto.FeatureTemplate Template(string name, IReadOnlyList<MetadataV2Field> fields)
    {
        var template = new Proto.FeatureTemplate { Name = name };
        foreach (var field in fields.Where(field => field.Editable && field.DefaultValue.HasValue))
        {
            template.DefaultAttributes[field.Name] = Value(field.DefaultValue!.Value, field.Type);
        }
        return template;
    }

    internal static Proto.LayerTimeInfo Time(MetadataV2ObjectTemporal time, ISet<string> visible)
    {
        var result = new Proto.LayerTimeInfo
        {
            StartField = Visible(time.StartTimeField, visible),
            EndField = Visible(time.EndTimeField, visible)
        };
        if (time.Extent?.Start is { } start)
        {
            result.ExtentStartUtcMs = start.ToUnixTimeMilliseconds();
        }
        if (time.Extent?.End is { } end)
        {
            result.ExtentEndUtcMs = end.ToUnixTimeMilliseconds();
        }
        return result;
    }

    internal static string Visible(string? name, ISet<string> visible)
        => name is not null && visible.Contains(name) ? name : string.Empty;
}
