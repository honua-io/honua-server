// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Infrastructure.GeoJson;

internal static partial class GeoJsonFeatureBaseBuilder
{
    // Prepared metadata lives only as long as its response. Never cache it across
    // resources or requests: visibility can differ with the authorized resource.
    internal static GeoJsonFeatureBuildOptions PrepareOptions(
        MetadataV2Resource resource, GeoJsonFeatureBuildOptions options = default)
        => options with { Schema = new PreparedSchema(resource, options.IncludeAdditionalAttributes) };

    internal sealed class PreparedSchema
    {
        internal PreparedSchema(MetadataV2Resource resource, bool includeAdditionalAttributes)
        {
            Resource = resource;
            IncludeAdditionalAttributes = includeAdditionalAttributes;
            ObjectIdFieldName = ResolveObjectIdFieldName(resource);
            VisibleFields = resource.SchemaFields.Where(field => !field.Hidden && !IsGeometryField(field)).ToArray();
            DeclaredAttributeFields = includeAdditionalAttributes ? new(StringComparer.OrdinalIgnoreCase) : null;
            VisibleAttributeFields = includeAdditionalAttributes ? new(StringComparer.OrdinalIgnoreCase) : null;
            foreach (var field in resource.SchemaFields)
            {
                if (IsGeometryField(field))
                {
                    continue;
                }
                DeclaredAttributeFields?.Add(field.Name);
                if (!field.Hidden)
                {
                    VisibleAttributeFields?.Add(field.Name);
                }
                if (field.Type == MetadataV2FieldType.Date)
                {
                    DateOnlyFields.Add(field.Name);
                }
                else if (field.Type == MetadataV2FieldType.DateTime)
                {
                    DateTimeFields.Add(field.Name);
                }
            }
        }

        internal MetadataV2Resource Resource { get; }
        internal bool IncludeAdditionalAttributes { get; }
        internal string ObjectIdFieldName { get; }
        internal MetadataV2Field[] VisibleFields { get; }
        internal HashSet<string>? DeclaredAttributeFields { get; }
        internal HashSet<string>? VisibleAttributeFields { get; }
        // Preserve case-insensitive date classification, including additional
        // attributes whose spelling differs from the declared schema.
        internal HashSet<string> DateOnlyFields { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> DateTimeFields { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
