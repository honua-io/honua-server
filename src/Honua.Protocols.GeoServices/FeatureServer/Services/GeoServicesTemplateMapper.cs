// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Derives the Esri-style FeatureServer layer editing <c>types</c> and <c>templates</c>
/// from the canonical Metadata v2 subtype set. ArcGIS does not author standalone editing
/// templates: a layer's types/templates are projected from its subtypes (one type per
/// subtype, each carrying an editing template whose prototype pre-seeds that subtype's
/// field default values and the subtype code). Honua mirrors that exactly — there is no
/// separate template authoring on the canonical graph, so this mapper is the single source
/// of the layer template/type surface (#1878). Reuses
/// <see cref="GeoServicesFieldDomainMapper"/> for per-subtype domains so the served domain
/// shape stays consistent with the layer-level field domains and the <c>subtypes</c> array.
/// </summary>
internal static class GeoServicesTemplateMapper
{
    /// <summary>
    /// Maps a resource's subtype set to the layer <c>types</c> array, or <c>null</c> when the
    /// layer declares no subtypes (so the caller omits <c>types</c> from the response, matching
    /// the existing <c>subtypes</c> handling).
    /// </summary>
    /// <param name="subtypes">The canonical subtype set, or <see langword="null"/>.</param>
    /// <param name="geometryType">The layer's GeoServices geometry type (e.g. <c>esriGeometryPoint</c>),
    /// used to pick the editing drawing tool.</param>
    public static GeoServicesLayerType[]? MapTypes(MetadataV2Subtypes? subtypes, string geometryType)
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

    /// <summary>
    /// Picks the Esri editing drawing tool for a geometry type. Mirrors the ArcGIS default
    /// of a point/line/polygon tool per the layer's geometry; falls back to the point tool.
    /// </summary>
    private static string ResolveDrawingTool(string geometryType)
        => geometryType switch
        {
            "esriGeometryPolyline" => "esriFeatureEditToolLine",
            "esriGeometryPolygon" => "esriFeatureEditToolPolygon",
            "esriGeometryMultipoint" => "esriFeatureEditToolPoint",
            _ => "esriFeatureEditToolPoint",
        };
}
