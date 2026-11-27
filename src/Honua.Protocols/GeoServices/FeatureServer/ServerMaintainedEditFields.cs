// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static class ServerMaintainedEditFields
{
    internal static IReadOnlyDictionary<string, object?> OmitClientValues(
        MetadataV2Object resource,
        IReadOnlyDictionary<string, object?> attributes,
        bool isUpdate,
        bool useGlobalIds)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(attributes);

        var omit = CollectOmittedFieldNames(resource, isUpdate, useGlobalIds);
        var nullObjectIdField = isUpdate
            ? null
            : GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);

        if (!HasOmittedAttribute(attributes, omit, nullObjectIdField))
        {
            return attributes;
        }

        var kept = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in attributes)
        {
            if (!ShouldOmitAttribute(entry, omit, nullObjectIdField))
            {
                kept[entry.Key] = entry.Value;
            }
        }

        return kept;
    }

    private static HashSet<string> CollectOmittedFieldNames(
        MetadataV2Object resource,
        bool isUpdate,
        bool useGlobalIds)
    {
        var omit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var editing = resource.Editing;
        if (editing is not null)
        {
            omit.UnionWith(EditorTrackingFields.Names(editing));
            if (isUpdate || !useGlobalIds)
            {
                Add(omit, editing.GlobalIdField);
            }
        }

        if (isUpdate)
        {
            return omit;
        }

        foreach (var field in resource.SchemaFields)
        {
            if (!field.Editable && !IsPreservedGlobalId(editing, field.Name, useGlobalIds))
            {
                omit.Add(field.Name);
            }
        }

        return omit;
    }

    private static bool HasOmittedAttribute(
        IReadOnlyDictionary<string, object?> attributes,
        HashSet<string> omit,
        string? nullObjectIdField)
    {
        foreach (var entry in attributes)
        {
            if (ShouldOmitAttribute(entry, omit, nullObjectIdField))
            {
                return true;
            }
        }

        return false;
    }

    // Real GeoServices clients (such as QGIS QgsAfsProvider) include `"objectid": null`
    // in addFeatures/applyEdits payloads for server-generated OID fields.
    private static bool ShouldOmitAttribute(
        KeyValuePair<string, object?> entry,
        HashSet<string> omit,
        string? nullObjectIdField)
        => omit.Contains(entry.Key) ||
           (nullObjectIdField is not null &&
            entry.Key.Equals(nullObjectIdField, StringComparison.OrdinalIgnoreCase) &&
            IsNullAttributeValue(entry.Value));

    private static bool IsNullAttributeValue(object? value)
        => value is null or JsonElement { ValueKind: JsonValueKind.Null };

    internal static void Stamp(
        MetadataV2Object resource,
        IDictionary<string, object?> attributes,
        bool isInsert,
        EditPrincipal principal,
        DateTimeOffset utcNow,
        bool useGlobalIds,
        IReadOnlyDictionary<string, object?>? existingAttributes)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(attributes);

        var editing = resource.Editing;
        if (editing is null)
        {
            return;
        }

        var stamp = new EditorTrackingStamp(principal.Name, field => TimeValue(field, utcNow));
        if (isInsert)
        {
            StampInsertGlobalId(resource, attributes, editing.GlobalIdField, useGlobalIds);
            EditorTrackingFields.StampInsert(resource, attributes, stamp);
            return;
        }

        Restore(resource, attributes, existingAttributes, editing.GlobalIdField);
        Restore(resource, attributes, existingAttributes, editing.CreatorField);
        Restore(resource, attributes, existingAttributes, editing.CreatedAtField);
        EditorTrackingFields.StampUpdate(resource, attributes, stamp);
    }

    private static void StampInsertGlobalId(
        MetadataV2Object resource,
        IDictionary<string, object?> attributes,
        string? fieldName,
        bool useGlobalIds)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        var name = EditorTrackingFields.CanonicalName(resource, fieldName);
        var supplied = useGlobalIds && TryGet(attributes, name, out var value) ? value : null;
        attributes[name] = InsertGlobalIds.Resolve(supplied);
    }

    private static object TimeValue(MetadataV2Field? field, DateTimeOffset utcNow) => field?.Type switch
    {
        MetadataV2FieldType.Date => GeoServicesFieldConventions.ToEpochMilliseconds(
            DateOnly.FromDateTime(utcNow.UtcDateTime)),
        // Stamped after attribute validation, so an exact timestamp is written as its RFC 3339 text here.
        MetadataV2FieldType.String or MetadataV2FieldType.TimestampOffset => utcNow.ToString("O", CultureInfo.InvariantCulture),
        _ => utcNow.ToUnixTimeMilliseconds(),
    };

    private static void Restore(
        MetadataV2Object resource,
        IDictionary<string, object?> attributes,
        IReadOnlyDictionary<string, object?>? existingAttributes,
        string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        var name = EditorTrackingFields.CanonicalName(resource, fieldName);
        if (existingAttributes is not null && TryGet(existingAttributes, name, out var value))
        {
            attributes[name] = value;
            return;
        }

        attributes.Remove(name);
    }

    private static bool IsPreservedGlobalId(MetadataV2ObjectEditing? editing, string fieldName, bool useGlobalIds)
        => useGlobalIds &&
           editing?.GlobalIdField is { } globalIdField &&
           fieldName.Equals(globalIdField, StringComparison.OrdinalIgnoreCase);

    private static bool TryGet(IDictionary<string, object?> attributes, string name, out object? value)
        => TryGetValue(attributes, name, out value);

    private static bool TryGet(IReadOnlyDictionary<string, object?> attributes, string name, out object? value)
        => TryGetValue(attributes, name, out value);

    private static bool TryGetValue(
        IEnumerable<KeyValuePair<string, object?>> attributes,
        string name,
        out object? value)
    {
        object? caseInsensitive = null;
        var foundCaseInsensitive = false;
        foreach (var entry in attributes)
        {
            if (entry.Key.Equals(name, StringComparison.Ordinal))
            {
                value = entry.Value;
                return true;
            }

            if (!foundCaseInsensitive && entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitive = entry.Value;
                foundCaseInsensitive = true;
            }
        }

        value = caseInsensitive;
        return foundCaseInsensitive;
    }

    private static void Add(HashSet<string> names, string? fieldName)
    {
        if (!string.IsNullOrWhiteSpace(fieldName))
        {
            names.Add(fieldName);
        }
    }
}
