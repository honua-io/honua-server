// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

// The stored form an edit protocol writes for a tracking timestamp in the declared field type.
// Returning false leaves the field unstamped.
public delegate bool TrackingTimeValue(MetadataV2Field? field, out object? value);

// Editor-tracking fields (creator, created at, editor, updated at) belong to the server in every
// edit protocol: client values are ignored, inserts stamp all four from the caller and the clock,
// and updates stamp the editor and updated-at fields. Each protocol supplies the stored timestamp.
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

    public static void Stamp(
        MetadataV2Object resource,
        IDictionary<string, object?> attributes,
        bool isInsert,
        string? principalName,
        TrackingTimeValue timeValue)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(timeValue);

        var editing = resource.Editing;
        if (editing is null)
        {
            return;
        }

        if (isInsert)
        {
            SetUser(resource, attributes, editing.CreatorField, principalName);
            SetTime(resource, attributes, editing.CreatedAtField, timeValue);
        }

        SetUser(resource, attributes, editing.EditorField, principalName);
        SetTime(resource, attributes, editing.UpdatedAtField, timeValue);
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
        TrackingTimeValue timeValue)
    {
        if (!string.IsNullOrWhiteSpace(fieldName) && timeValue(FindField(resource, fieldName), out var value))
        {
            attributes[CanonicalName(resource, fieldName)] = value;
        }
    }
}
