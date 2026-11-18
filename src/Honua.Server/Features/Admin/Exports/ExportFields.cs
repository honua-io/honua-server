// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;

namespace Honua.Server.Features.Admin.Exports;

// Which source fields each artifact carries. Hidden fields reach neither artifact.
internal sealed record ExportFields(
    IReadOnlyList<ExportVersionField> Declared,
    ImmutableArray<string> Hidden,
    IReadOnlyList<string> TileFields,
    IReadOnlyList<string> ParquetFields,
    string ObjectIdField,
    string IdentityField,
    bool Stable)
{
    // Column names the GeoParquet writer owns; a field with one of them would be renamed.
    private static readonly string[] WriterColumns = ["geometry", "bbox"];

    // A declared text key is stable across Versions. Anything else, including the object id, is not:
    // a reload can renumber it. The object id is still kept, rendered as text.
    public static (string Field, bool Stable) ChooseIdentity(MetadataV2Object resource, string? requested)
    {
        var primary = resource.FindPrimaryIdField();
        var declared = primary is { Type: MetadataV2FieldType.String, Hidden: false } ? primary : null;
        var field = requested ?? declared?.Name ?? primary?.Name ?? FieldNames.ObjectId;
        var stable = declared is not null && string.Equals(declared.Name, field, StringComparison.Ordinal);
        return (field, stable);
    }

    public static ExportFields Resolve(MetadataV2Object resource, string identityField)
    {
        var geometry = resource.FindPrimaryGeometryField();
        var source = resource.SchemaFields
            .Where(field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
                && !string.Equals(field.Name, geometry?.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var hidden = source.Where(field => field.Hidden).Select(field => field.Name).ToImmutableArray();
        var visible = source.Where(field => !field.Hidden).ToList();
        var objectIdField = resource.FindPrimaryIdField()?.Name ?? FieldNames.ObjectId;
        var objectIdAsText = string.Equals(identityField, objectIdField, StringComparison.OrdinalIgnoreCase)
            && visible.FirstOrDefault(field => string.Equals(field.Name, identityField, StringComparison.OrdinalIgnoreCase))
                is not { Type: MetadataV2FieldType.String };
        RequirePublishable(visible, identityField, objectIdAsText);
        if (hidden.Contains(objectIdField, StringComparer.OrdinalIgnoreCase))
        {
            throw new ExportException(
                $"The layer's id field '{objectIdField}' is hidden; GeoParquet always carries it, so show it before publishing.");
        }

        var declared = visible.Select(field => Describe(field, AsText(field, identityField, objectIdField)))
            .Concat(source.Where(field => field.Hidden && DescriptorType(field.Type) is not null
                    && ExportRequestRules.IsPortableFieldName(field.Name))
                .Select(field => new ExportVersionField(field.Name, DescriptorType(field.Type)!, Public: false)))
            .ToList();
        if (!declared.Any(field => string.Equals(field.Name, objectIdField, StringComparison.OrdinalIgnoreCase)))
        {
            var synthesized = string.Equals(identityField, objectIdField, StringComparison.OrdinalIgnoreCase) ? "string" : "integer";
            declared.Insert(0, new ExportVersionField(objectIdField, synthesized, Public: true));
        }

        var parquetFields = GeoParquetFeatureWriter
            .ResolveSelectedFields(resource, objectIdField, visible.Select(field => field.Name).ToArray())
            .Where(field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            .Select(field => field.Name)
            .ToList();
        // Tile features carry every public attribute plus the object id column the tile query selects.
        var tileFields = declared.Where(field => field.Public).Select(field => field.Name).ToList();
        return new ExportFields(
            declared, hidden, tileFields, parquetFields, objectIdField, identityField, ChooseIdentity(resource, identityField).Stable);
    }

    private static bool AsText(MetadataV2Field field, string identityField, string objectIdField)
        => string.Equals(field.Name, identityField, StringComparison.OrdinalIgnoreCase)
            && string.Equals(identityField, objectIdField, StringComparison.OrdinalIgnoreCase)
            && field.Type is not MetadataV2FieldType.String;

    public bool IsPublic(string name)
        => Declared.Any(field => field.Public && string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void RequirePublishable(IReadOnlyList<MetadataV2Field> visible, string identityField, bool objectIdAsText)
    {
        var identity = visible.FirstOrDefault(field => string.Equals(field.Name, identityField, StringComparison.Ordinal));
        if (!objectIdAsText && identity is not { Type: MetadataV2FieldType.String })
        {
            throw new ExportException(
                $"Identity field '{identityField}' must be a visible text field of the layer.");
        }

        if (visible.FirstOrDefault(field => WriterColumns.Contains(field.Name, StringComparer.OrdinalIgnoreCase)) is { } reserved)
        {
            throw new ExportException(
                $"Field '{reserved.Name}' collides with a GeoParquet column; rename or hide it before publishing.");
        }
    }

    private static ExportVersionField Describe(MetadataV2Field field, bool asText)
    {
        if (!ExportRequestRules.IsPortableFieldName(field.Name))
        {
            throw new ExportException(
                $"Field '{field.Name}' is not a portable column name; rename or hide it before publishing.");
        }

        var type = asText
            ? "string"
            : DescriptorType(field.Type) ?? throw new ExportException(
                $"Field '{field.Name}' has a type the publication descriptor cannot describe; hide it before publishing.");
        return new ExportVersionField(field.Name, type, Public: true)
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
