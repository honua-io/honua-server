// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Server-owned GlobalID and editor-tracking values for GeoServices edits (#6134).
/// Client values for those fields, and for other non-editable fields on insert, are
/// dropped before validation. The handler then stamps the values that are stored.
/// </summary>
internal static class ServerMaintainedEditFields
{
    /// <summary>
    /// Returns <paramref name="attributes"/> without client values the server owns.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?> OmitClientValues(
        MetadataV2Resource resource,
        IReadOnlyDictionary<string, object?> attributes,
        bool isUpdate,
        bool useGlobalIds)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(attributes);

        var omit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var editing = resource.Editing;
        if (editing is not null)
        {
            Add(omit, editing.CreatorField);
            Add(omit, editing.CreatedAtField);
            Add(omit, editing.EditorField);
            Add(omit, editing.UpdatedAtField);
            if (isUpdate || !useGlobalIds)
            {
                Add(omit, editing.GlobalIdField);
            }
        }

        if (!isUpdate)
        {
            foreach (var field in resource.SchemaFields)
            {
                if (!field.Editable && !IsPreservedGlobalId(editing, field.Name, useGlobalIds))
                {
                    omit.Add(field.Name);
                }
            }
        }

        if (omit.Count == 0)
        {
            return attributes;
        }

        var dropped = false;
        foreach (var key in attributes.Keys)
        {
            if (omit.Contains(key))
            {
                dropped = true;
                break;
            }
        }

        if (!dropped)
        {
            return attributes;
        }

        var kept = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in attributes)
        {
            if (!omit.Contains(key))
            {
                kept[key] = value;
            }
        }

        return kept;
    }

    /// <summary>
    /// Writes server-owned GlobalID and editor-tracking values into <paramref name="attributes"/>.
    /// </summary>
    internal static void Stamp(
        MetadataV2Resource resource,
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

        if (isInsert)
        {
            StampInsertGlobalId(resource, attributes, editing.GlobalIdField, useGlobalIds);
            SetUser(resource, attributes, editing.CreatorField, principal.Name);
            SetUser(resource, attributes, editing.EditorField, principal.Name);
            SetTime(resource, attributes, editing.CreatedAtField, utcNow);
            SetTime(resource, attributes, editing.UpdatedAtField, utcNow);
            return;
        }

        Restore(resource, attributes, existingAttributes, editing.GlobalIdField);
        Restore(resource, attributes, existingAttributes, editing.CreatorField);
        Restore(resource, attributes, existingAttributes, editing.CreatedAtField);
        SetUser(resource, attributes, editing.EditorField, principal.Name);
        SetTime(resource, attributes, editing.UpdatedAtField, utcNow);
    }

    private static void StampInsertGlobalId(
        MetadataV2Resource resource,
        IDictionary<string, object?> attributes,
        string? fieldName,
        bool useGlobalIds)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        var name = CanonicalName(resource, fieldName);
        if (useGlobalIds && TryGet(attributes, name, out var supplied) && supplied is not null)
        {
            attributes[name] = NormalizeGuid(supplied);
            return;
        }

        attributes[name] = Guid.NewGuid().ToString("D");
    }

    private static void SetUser(
        MetadataV2Resource resource,
        IDictionary<string, object?> attributes,
        string? fieldName,
        string? principalName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        attributes[CanonicalName(resource, fieldName)] = principalName;
    }

    private static void SetTime(
        MetadataV2Resource resource,
        IDictionary<string, object?> attributes,
        string? fieldName,
        DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        var name = CanonicalName(resource, fieldName);
        var field = FindField(resource, fieldName);
        attributes[name] = field?.Type switch
        {
            MetadataV2FieldType.Date => GeoServicesFieldConventions.ToEpochMilliseconds(
                DateOnly.FromDateTime(utcNow.UtcDateTime)),
            MetadataV2FieldType.String => utcNow.ToString("O", CultureInfo.InvariantCulture),
            _ => utcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static void Restore(
        MetadataV2Resource resource,
        IDictionary<string, object?> attributes,
        IReadOnlyDictionary<string, object?>? existingAttributes,
        string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        var name = CanonicalName(resource, fieldName);
        if (existingAttributes is not null && TryGet(existingAttributes, name, out var value))
        {
            attributes[name] = value;
            return;
        }

        attributes.Remove(name);
    }

    private static bool IsPreservedGlobalId(MetadataV2ResourceEditing? editing, string fieldName, bool useGlobalIds)
        => useGlobalIds &&
           editing?.GlobalIdField is { } globalIdField &&
           fieldName.Equals(globalIdField, StringComparison.OrdinalIgnoreCase);

    private static string CanonicalName(MetadataV2Resource resource, string fieldName)
        => FindField(resource, fieldName)?.Name ?? fieldName;

    private static MetadataV2Field? FindField(MetadataV2Resource resource, string fieldName)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }
        }

        return null;
    }

    private static bool TryGet(IReadOnlyDictionary<string, object?> attributes, string name, out object? value)
    {
        if (attributes.TryGetValue(name, out value))
        {
            return true;
        }

        foreach (var entry in attributes)
        {
            if (entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string NormalizeGuid(object value)
    {
        if (value is Guid guid)
        {
            return guid.ToString("D");
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return Guid.TryParse(text, out var parsed) ? parsed.ToString("D") : text ?? Guid.NewGuid().ToString("D");
    }

    private static void Add(HashSet<string> names, string? fieldName)
    {
        if (!string.IsNullOrWhiteSpace(fieldName))
        {
            names.Add(fieldName);
        }
    }
}
