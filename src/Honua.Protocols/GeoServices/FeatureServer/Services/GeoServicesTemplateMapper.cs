// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal static class GeoServicesTemplateMapper
{
    public static GeoServicesLayerType[]? MapTypes(MetadataV2Subtypes? subtypes, string? geometryType)
    {
        if (subtypes is null || subtypes.Subtypes.Count == 0)
        {
            return null;
        }

        var drawingTool = ResolveDrawingTool(geometryType);
        var subtypeField = subtypes.SubtypeField;

        return subtypes.Subtypes
            .Select(subtype => new GeoServicesLayerType
            {
                Id = subtype.Code.Clone(),
                Name = subtype.Name,
                Domains = MapDomains(subtype.FieldOverrides),
                Templates =
                [
                    new FeatureTemplate
                    {
                        Name = subtype.Name,
                        Description = string.Empty,
                        DrawingTool = drawingTool,
                        Prototype = new FeatureTemplatePrototype
                        {
                            Attributes = BuildPrototypeAttributes(subtypeField, subtype),
                        },
                    },
                ],
            })
            .ToArray();
    }

    private static Dictionary<string, JsonElement> BuildPrototypeAttributes(
        string subtypeField,
        MetadataV2Subtype subtype)
    {
        var attributes = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        // The subtype code itself is the canonical "default value" a new feature of this
        // subtype starts with, written under the layer's subtype field.
        if (!string.IsNullOrWhiteSpace(subtypeField))
        {
            attributes[subtypeField] = subtype.Code.Clone();
        }

        // Per-field default values authored on the subtype overrides seed the rest of the
        // prototype, so the editing client starts a new feature with the subtype's defaults.
        foreach (var pair in subtype.FieldOverrides.Where(pair => pair.Value.DefaultValue is not null))
        {
            attributes[pair.Key] = pair.Value.DefaultValue!.Value.Clone();
        }

        return attributes;
    }

    private static Dictionary<string, GeoServicesFieldDomainInfo>? MapDomains(
        IReadOnlyDictionary<string, MetadataV2SubtypeFieldOverride> overrides)
    {
        var domains = new Dictionary<string, GeoServicesFieldDomainInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, mapped) in overrides
            .Select(pair => (pair.Key, Mapped: GeoServicesFieldDomainMapper.Map(pair.Value.Domain)))
            .Where(pair => pair.Mapped is not null))
        {
            domains[key] = mapped!;
        }

        return domains.Count == 0 ? null : domains;
    }

    private static string ResolveDrawingTool(string? geometryType)
        => geometryType switch
        {
            "esriGeometryPolyline" => "esriFeatureEditToolLine",
            "esriGeometryPolygon" => "esriFeatureEditToolPolygon",
            "esriGeometryMultipoint" => "esriFeatureEditToolPoint",
            _ => "esriFeatureEditToolPoint",
        };
}
