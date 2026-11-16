// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

// Editor-tracking fields (creator, created at, editor, updated at) belong to the server in every
// edit protocol: client values are ignored, inserts stamp all four from the caller and the clock,
// and updates stamp the editor and updated-at fields. Each protocol supplies the stored timestamp
// through EditorTrackingStamp.
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
        EditorTrackingStamp stamp)
    {
        var target = Target.Of(resource, attributes, stamp);
        if (resource.Editing is { } editing)
        {
            target.SetUser(editing.CreatorField, stamp.PrincipalName);
            target.SetTime(editing.CreatedAtField, stamp.TimeValue);
        }

        StampUpdate(resource, attributes, stamp);
    }

    public static void StampUpdate(
        MetadataV2Object resource,
        IDictionary<string, object?> attributes,
        EditorTrackingStamp stamp)
    {
        var target = Target.Of(resource, attributes, stamp);
        if (resource.Editing is { } editing)
        {
            target.SetUser(editing.EditorField, stamp.PrincipalName);
            target.SetTime(editing.UpdatedAtField, stamp.TimeValue);
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

    private readonly record struct Target(MetadataV2Object Resource, IDictionary<string, object?> Attributes)
    {
        public static Target Of(
            MetadataV2Object resource,
            IDictionary<string, object?> attributes,
            EditorTrackingStamp stamp)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ArgumentNullException.ThrowIfNull(attributes);
            ArgumentNullException.ThrowIfNull(stamp.TimeValue);
            return new Target(resource, attributes);
        }

        public void SetUser(string? fieldName, string? principalName)
        {
            if (!string.IsNullOrWhiteSpace(fieldName))
            {
                Attributes[CanonicalName(Resource, fieldName)] = principalName;
            }
        }

        public void SetTime(string? fieldName, Func<MetadataV2Field?, object?> timeValue)
        {
            if (!string.IsNullOrWhiteSpace(fieldName) && timeValue(FindField(Resource, fieldName)) is { } value)
            {
                Attributes[CanonicalName(Resource, fieldName)] = value;
            }
        }
    }
}
