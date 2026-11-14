// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    private static void PopulateLayerMetadata(Proto.LayerDescriptor descriptor, GrpcLayerContext layer,
        MetadataV2Field[] fields)
    {
        var resource = layer.Resource;
        var visible = fields.Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddMetadataFields(descriptor, layer, fields);
        if (resource.Spatial?.Bbox is { } bbox)
        {
            descriptor.Extent = new()
            {
                Xmin = bbox.West,
                Ymin = bbox.South,
                Xmax = bbox.East,
                Ymax = bbox.North,
                SpatialReference = descriptor.SpatialReference.Clone()
            };
        }
        if (resource.Subtypes is { } subtypes && visible.Contains(subtypes.SubtypeField))
        {
            descriptor.Subtypes = GrpcMetadataProjector.Subtypes(subtypes, fields);
        }
        else
        {
            var template = GrpcMetadataProjector.Template(descriptor.Name, fields);
            if (template.DefaultAttributes.Count > 0)
            {
                descriptor.Templates.Add(template);
            }
        }
        if (resource.Temporal is { } time && visible.Contains(time.StartTimeField ?? string.Empty))
        {
            descriptor.TimeInfo = GrpcMetadataProjector.Time(time, visible);
        }
        AddEditingMetadata(descriptor, resource, visible);
    }

    private static void AddMetadataFields(Proto.LayerDescriptor descriptor, GrpcLayerContext layer,
        MetadataV2Field[] fields)
    {
        foreach (var field in fields)
        {
            var projected = GrpcMetadataProjector.Field(field);
            if (layer.Publication.FieldAliases?.TryGetValue(field.Name, out var alias) == true)
            {
                projected.Definition.Alias = alias;
            }
            descriptor.Fields.Add(projected);
        }
    }

    private static void AddEditingMetadata(Proto.LayerDescriptor descriptor, MetadataV2Resource resource,
        ISet<string> visible)
    {
        if (resource.Editing is { } editing)
        {
            descriptor.EditingInfo = new()
            {
                CreatorField = GrpcMetadataProjector.Visible(editing.CreatorField, visible),
                CreatedAtField = GrpcMetadataProjector.Visible(editing.CreatedAtField, visible),
                EditorField = GrpcMetadataProjector.Visible(editing.EditorField, visible),
                EditedAtField = GrpcMetadataProjector.Visible(editing.UpdatedAtField, visible),
                OwnershipAccessControlEnabled = resource.OwnerEditPolicy?.Enabled ?? false
            };
            // Tracking timestamps and ownership exceptions are omitted unless canonical metadata specifies them.
        }
    }

}
