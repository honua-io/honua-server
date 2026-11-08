// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

internal static class MetadataV2FieldSemantics
{
    internal static bool SupportsGeometry(MetadataV2FieldType type)
        => type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography;

    internal static bool SupportsTime(MetadataV2FieldType type)
        => type is MetadataV2FieldType.Date or MetadataV2FieldType.DateTime or MetadataV2FieldType.Time;

    internal static bool HasSupportedReferences(MetadataV2Resource dataset)
        => SupportsField(dataset, dataset.Spatial?.PrimaryGeometryField, SupportsGeometry)
           && SupportsField(dataset, dataset.Temporal?.StartTimeField, SupportsTime)
           && SupportsField(dataset, dataset.Temporal?.EndTimeField, SupportsTime);

    private static bool SupportsField(
        MetadataV2Resource dataset, string? fieldName, Func<MetadataV2FieldType, bool> supportsType)
        => string.IsNullOrWhiteSpace(fieldName)
           || dataset.SchemaFields.FirstOrDefault(field =>
               string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase)) is { } field
           && supportsType(field.Type);
}
