// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

// Applies the editor-tracking part of an editing patch. Each named field must be declared on the
// resource with a type the edit protocols can stamp: the caller's name goes into a String field, and
// the server clock into Date, DateTime, String, BigInteger or Double fields.
internal static class MetadataV2EditorTrackingPatch
{
    private static readonly MetadataV2FieldType[] UserTypes = [MetadataV2FieldType.String];

    private static readonly MetadataV2FieldType[] TimeTypes =
    [
        MetadataV2FieldType.Date,
        MetadataV2FieldType.DateTime,
        MetadataV2FieldType.String,
        MetadataV2FieldType.BigInteger,
        MetadataV2FieldType.Double
    ];

    public static MetadataV2ObjectEditing Apply(
        MetadataV2Object resource,
        MetadataV2ObjectEditing editing,
        MetadataV2EditingPatch patch)
    {
        if (!patch.ChangesTracking)
        {
            return editing;
        }

        return editing with
        {
            CreatorField = Resolve(resource, patch.CreatorField, editing.CreatorField, "creatorField", UserTypes),
            CreatedAtField = Resolve(resource, patch.CreatedAtField, editing.CreatedAtField, "createdAtField", TimeTypes),
            EditorField = Resolve(resource, patch.EditorField, editing.EditorField, "editorField", UserTypes),
            UpdatedAtField = Resolve(resource, patch.UpdatedAtField, editing.UpdatedAtField, "updatedAtField", TimeTypes)
        };
    }

    private static string? Resolve(
        MetadataV2Object resource,
        string? requested,
        string? current,
        string slot,
        MetadataV2FieldType[] allowed)
    {
        if (requested is null)
        {
            return current;
        }
        if (requested.Length == 0)
        {
            return null;
        }

        var field = resource.SchemaFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (field is null || !allowed.Contains(field.Type))
        {
            throw new ArgumentException(
                $"{slot} must reference a declared field of type {string.Join(", ", allowed)}.", nameof(requested));
        }
        return field.Name;
    }
}
