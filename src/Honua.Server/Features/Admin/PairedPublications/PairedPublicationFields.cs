// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;

namespace Honua.Server.Features.Admin.PairedPublications;

// Which source fields each artifact carries. Hidden fields reach neither artifact.
internal sealed record PairedPublicationFields(
    IReadOnlyList<PublicationField> Declared,
    ImmutableArray<string> Hidden,
    IReadOnlyList<string> TileFields,
    IReadOnlyList<string> ParquetFields,
    string ObjectIdField,
    string IdentityField)
{
    // Column names the GeoParquet writer owns; a field with one of them would be renamed.
    private static readonly string[] WriterColumns = ["geometry", "bbox"];

    public static PairedPublicationFields Resolve(MetadataV2Object resource, string identityField)
    {
        var geometry = resource.FindPrimaryGeometryField();
        var source = resource.SchemaFields
            .Where(field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
                && !string.Equals(field.Name, geometry?.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var hidden = source.Where(field => field.Hidden).Select(field => field.Name).ToImmutableArray();
        var visible = source.Where(field => !field.Hidden).ToList();
        RequirePublishable(visible, identityField);

        var objectIdField = resource.FindPrimaryIdField()?.Name ?? FieldNames.ObjectId;
        if (hidden.Contains(objectIdField, StringComparer.OrdinalIgnoreCase))
        {
            throw new PairedPublicationException(
                $"The layer's id field '{objectIdField}' is hidden; GeoParquet always carries it, so show it before publishing.");
        }

        var declared = visible.Select(Describe)
            .Concat(source.Where(field => field.Hidden && DescriptorType(field.Type) is not null
                    && PairedPublishRequestRules.IsPortableFieldName(field.Name))
                .Select(field => new PublicationField(field.Name, DescriptorType(field.Type)!, Public: false)))
            .ToList();
        if (!declared.Any(field => string.Equals(field.Name, objectIdField, StringComparison.OrdinalIgnoreCase)))
        {
            declared.Insert(0, new PublicationField(objectIdField, "integer", Public: true));
        }

        var parquetFields = GeoParquetFeatureWriter
            .ResolveSelectedFields(resource, objectIdField, visible.Select(field => field.Name).ToArray())
            .Where(field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            .Select(field => field.Name)
            .ToList();
        // Tile features carry every public attribute plus the object id column the tile query selects.
        var tileFields = declared.Where(field => field.Public).Select(field => field.Name).ToList();
        return new PairedPublicationFields(declared, hidden, tileFields, parquetFields, objectIdField, identityField);
    }

    public bool IsPublic(string name)
        => Declared.Any(field => field.Public && string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void RequirePublishable(IReadOnlyList<MetadataV2Field> visible, string identityField)
    {
        var identity = visible.FirstOrDefault(field => string.Equals(field.Name, identityField, StringComparison.Ordinal));
        if (identity is not { Type: MetadataV2FieldType.String })
        {
            throw new PairedPublicationException(
                $"Identity field '{identityField}' must be a visible text field of the layer.");
        }

        if (visible.FirstOrDefault(field => WriterColumns.Contains(field.Name, StringComparer.OrdinalIgnoreCase)) is { } reserved)
        {
            throw new PairedPublicationException(
                $"Field '{reserved.Name}' collides with a GeoParquet column; rename or hide it before publishing.");
        }
    }

    private static PublicationField Describe(MetadataV2Field field)
    {
        if (!PairedPublishRequestRules.IsPortableFieldName(field.Name))
        {
            throw new PairedPublicationException(
                $"Field '{field.Name}' is not a portable column name; rename or hide it before publishing.");
        }

        var type = DescriptorType(field.Type) ?? throw new PairedPublicationException(
            $"Field '{field.Name}' has a type the publication descriptor cannot describe; hide it before publishing.");
        return new PublicationField(field.Name, type, Public: true)
        {
            Title = string.IsNullOrWhiteSpace(field.Alias) ? null : field.Alias,
            Description = string.IsNullOrWhiteSpace(field.Description) ? null : field.Description
        };
    }

    private static string? DescriptorType(MetadataV2FieldType type) => type switch
    {
        MetadataV2FieldType.String or MetadataV2FieldType.Uuid => "string",
        MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger => "integer",
        MetadataV2FieldType.Double or MetadataV2FieldType.Float => "number",
        MetadataV2FieldType.Boolean => "boolean",
        MetadataV2FieldType.Date => "date",
        MetadataV2FieldType.DateTime => "datetime",
        _ => null
    };
}
