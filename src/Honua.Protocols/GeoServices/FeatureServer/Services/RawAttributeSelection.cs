// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

// Chooses which stored JSON attributes a raw GeoServices point query writes. JsonProperty.Name decodes a
// new string on every access, so unescaped names are compared as characters decoded from the raw UTF-8.
internal sealed class RawAttributeSelection
{
    internal const int MaxDecodedNameLength = 256;

    private readonly HashSet<string> _allowedAttributeNames;
    private readonly Dictionary<string, MetadataV2FieldType> _temporalFieldTypes;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _allowedLookup;
    private readonly Dictionary<string, MetadataV2FieldType>.AlternateLookup<ReadOnlySpan<char>> _temporalLookup;
    private readonly char[] _nameBuffer = new char[MaxDecodedNameLength];

    internal RawAttributeSelection(
        HashSet<string> allowedAttributeNames,
        Dictionary<string, MetadataV2FieldType> temporalFieldTypes,
        string objectIdFieldName)
    {
        _allowedAttributeNames = allowedAttributeNames;
        _temporalFieldTypes = temporalFieldTypes;
        _allowedLookup = allowedAttributeNames.GetAlternateLookup<ReadOnlySpan<char>>();
        _temporalLookup = temporalFieldTypes.GetAlternateLookup<ReadOnlySpan<char>>();
        ObjectIdFieldName = objectIdFieldName;
    }

    internal string ObjectIdFieldName { get; }

    internal RawAttributeChoice? Choose(JsonProperty property)
    {
        var rawName = JsonMarshal.GetRawUtf8PropertyName(property);
        if (rawName.Length > _nameBuffer.Length || rawName.Contains((byte)'\\'))
        {
            // Escaped or unusually long names are decoded; the written name is the same.
            var name = property.Name;
            if (IsReserved(name) || !_allowedAttributeNames.Contains(name))
            {
                return null;
            }

            return new RawAttributeChoice(_temporalFieldTypes.TryGetValue(name, out var type) ? type : null, name);
        }

        ReadOnlySpan<char> chars = _nameBuffer.AsSpan(0, Encoding.UTF8.GetChars(rawName, _nameBuffer));
        if (IsReserved(chars) || !_allowedLookup.Contains(chars))
        {
            return null;
        }

        return new RawAttributeChoice(_temporalLookup.TryGetValue(chars, out var spanType) ? spanType : null, null);
    }

    internal static void WriteName(Utf8JsonWriter writer, JsonProperty property, RawAttributeChoice choice)
    {
        if (choice.DecodedName is { } name)
        {
            writer.WritePropertyName(name);
            return;
        }

        writer.WritePropertyName(JsonMarshal.GetRawUtf8PropertyName(property));
    }

    private bool IsReserved(ReadOnlySpan<char> name)
        => name.StartsWith("__", StringComparison.Ordinal)
            || name.Equals(ObjectIdFieldName, StringComparison.OrdinalIgnoreCase);
}

// DecodedName is set only for names that had to be decoded; other names are written from their raw UTF-8.
internal readonly record struct RawAttributeChoice(MetadataV2FieldType? TemporalType, string? DecodedName);
