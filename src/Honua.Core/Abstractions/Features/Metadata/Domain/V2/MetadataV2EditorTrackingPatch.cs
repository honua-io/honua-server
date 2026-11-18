// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

// Applies the editor-tracking part of an editing patch. Each named field must be declared on the
// resource with a type the edit protocols can stamp: the caller's name goes into a String field, and
// the server clock into Date, DateTime, String, BigInteger or Double fields.
internal static class MetadataV2EditorTrackingPatch
{
    private static readonly TrackingSlot Creator = new("creatorField", [MetadataV2FieldType.String]);

    private static readonly TrackingSlot Editor = new("editorField", [MetadataV2FieldType.String]);

    private static readonly MetadataV2FieldType[] TimeTypes =
    [
        MetadataV2FieldType.Date,
        MetadataV2FieldType.DateTime,
        MetadataV2FieldType.String,
        MetadataV2FieldType.BigInteger,
        MetadataV2FieldType.Double
    ];

    private static readonly TrackingSlot CreatedAt = new("createdAtField", TimeTypes);

    private static readonly TrackingSlot UpdatedAt = new("updatedAtField", TimeTypes);

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
            CreatorField = patch.CreatorField is null ? editing.CreatorField : Resolve(resource, patch.CreatorField, Creator),
            CreatedAtField = patch.CreatedAtField is null ? editing.CreatedAtField : Resolve(resource, patch.CreatedAtField, CreatedAt),
            EditorField = patch.EditorField is null ? editing.EditorField : Resolve(resource, patch.EditorField, Editor),
            UpdatedAtField = patch.UpdatedAtField is null ? editing.UpdatedAtField : Resolve(resource, patch.UpdatedAtField, UpdatedAt)
        };
    }

    // An empty string clears the field; any other value must name a declared field of an allowed type.
    private static string? Resolve(MetadataV2Object resource, string requested, TrackingSlot slot)
    {
        if (requested.Length == 0)
        {
            return null;
        }

        var field = resource.SchemaFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (field is null || !slot.Allowed.Contains(field.Type))
        {
            throw new ArgumentException(
                $"{slot.Name} must reference a declared field of type {string.Join(", ", slot.Allowed)}.", nameof(requested));
        }
        return field.Name;
    }

    private sealed record TrackingSlot(string Name, MetadataV2FieldType[] Allowed);
}
