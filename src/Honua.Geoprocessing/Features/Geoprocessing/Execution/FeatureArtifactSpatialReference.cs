// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using NetTopologySuite.Geometries;

namespace Honua.Geoprocessing.Execution;

/// <summary>Producer-owned output coordinate frame. A feature property is not this frame.</summary>
internal sealed record FeatureArtifactSpatialReference(string State, int? Srid)
{
    internal const string MemberName = "honua:spatialReference";
    internal const string SchemaVersion = "honua.feature-crs/v1";

    internal bool IsValid => State == "known" && Srid is > 0 || State is "unknown" or "mixed" && Srid is null;

    internal void Write(Utf8JsonWriter writer)
    {
        if (!IsValid)
        {
            throw new InvalidDataException("The output coordinate frame is invalid.");
        }

        writer.WritePropertyName(MemberName);
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", SchemaVersion);
        writer.WriteString("state", State);
        if (Srid is { } srid)
        {
            writer.WriteNumber("srid", srid);
        }

        writer.WriteEndObject();
    }

    internal static FeatureArtifactSpatialReference? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A feature artifact object is required.");
        }

        var members = root.EnumerateObject().Where(property => property.NameEquals(MemberName)).ToArray();
        if (members.Length == 0)
        {
            return null;
        }

        if (members.Length != 1 || members[0].Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The output coordinate frame is ambiguous.");
        }

        var value = members[0].Value;
        var properties = value.EnumerateObject().ToArray();
        if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length
            || properties.Any(property => property.Name is not ("schemaVersion" or "state" or "srid"))
            || !value.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.String
            || schema.GetString() != SchemaVersion
            || !value.TryGetProperty("state", out var state)
            || state.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("The output coordinate frame contract is unsupported.");
        }

        int? srid = null;
        if (value.TryGetProperty("srid", out var number))
        {
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var parsed))
            {
                throw new InvalidDataException("The output coordinate frame is invalid.");
            }

            srid = parsed;
        }

        var result = new FeatureArtifactSpatialReference(state.GetString()!, srid);
        return result.IsValid ? result : throw new InvalidDataException("The output coordinate frame is invalid.");
    }
}

/// <summary>Tracks one output frame without treating a GeoJSON reader's default as producer evidence.</summary>
internal sealed class FeatureArtifactSpatialReferenceAccumulator
{
    private int? _srid;
    private bool _unknown;
    private bool _mixed;

    internal void Add(Geometry? geometry, bool hasProducerSpatialReference = true)
    {
        if (geometry is null || geometry.IsEmpty)
        {
            return;
        }

        if (!hasProducerSpatialReference || geometry.SRID <= 0)
        {
            _unknown = true;
            return;
        }

        if (_srid is { } original && original != geometry.SRID)
        {
            _mixed = true;
        }

        _srid ??= geometry.SRID;
    }

    internal FeatureArtifactSpatialReference Value => _mixed
        ? new("mixed", null)
        : _unknown || _srid is null ? new("unknown", null) : new("known", _srid);
}
