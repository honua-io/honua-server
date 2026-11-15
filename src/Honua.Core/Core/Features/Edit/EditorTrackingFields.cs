// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

// Editor-tracking fields (creator, created at, editor, updated at) belong to the server in every
// edit protocol: client values are ignored, inserts stamp all four from the caller and the clock,
// and updates stamp the editor and updated-at fields. Each protocol supplies the stored timestamp
// for a declared field; a null timestamp leaves that field unstamped.
public static class EditorTrackingFields
{
    public static IEnumerable<string> Names(MetadataV2ObjectEditing? editing)
    {
        if (editing is null)
        {
            yield break;
        }

        foreach (var name in new[] { editing.CreatorField, editing.CreatedAtField, editing.EditorField, editing.UpdatedAtField })
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }
    }

    public static void StampInsert(
        MetadataV2Object resource,
        IDictionary<string, object?> attributes,
        string? principalName,
        Func<MetadataV2Field?, object?> timeValue)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(timeValue);
        if (resource.Editing is { } editing)
        {
            SetUser(resource, attributes, editing.CreatorField, principalName);
            SetTime(resource, attributes, editing.CreatedAtField, timeValue);
        }

        StampUpdate(resource, attributes, principalName, timeValue);
    }

    public static void StampUpdate(
        MetadataV2Object resource,
        IDictionary<string, object?> attributes,
        string? principalName,
        Func<MetadataV2Field?, object?> timeValue)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(timeValue);
        if (resource.Editing is { } editing)
        {
            SetUser(resource, attributes, editing.EditorField, principalName);
            SetTime(resource, attributes, editing.UpdatedAtField, timeValue);
        }
    }

    // The declared spelling of a field name, matched without regard to case.
    public static string CanonicalName(MetadataV2Object resource, string fieldName)
        => FindField(resource, fieldName)?.Name ?? fieldName;

    public static MetadataV2Field? FindField(MetadataV2Object resource, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(resource);
        foreach (var field in resource.SchemaFields)
        {
            if (field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }
        }

        return null;
    }

    private static void SetUser(
        MetadataV2Object resource,
        IDictionary<string, object?> attributes,
        string? fieldName,
        string? principalName)
    {
        if (!string.IsNullOrWhiteSpace(fieldName))
        {
            attributes[CanonicalName(resource, fieldName)] = principalName;
        }
    }

    private static void SetTime(
        MetadataV2Object resource,
        IDictionary<string, object?> attributes,
        string? fieldName,
        Func<MetadataV2Field?, object?> timeValue)
    {
        if (!string.IsNullOrWhiteSpace(fieldName) && timeValue(FindField(resource, fieldName)) is { } value)
        {
            attributes[CanonicalName(resource, fieldName)] = value;
        }
    }
}
