// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

/// <summary>
/// Map-family body gate: the published portable <c>honua_map_package.v1</c> schema
/// (honua-sdk-js <c>schemas/honua-map-package.v1.json</c>), checked member by member.
/// </summary>
/// <remarks>
/// <para>
/// The body used to be admitted by deserializing it into the geoprocessing
/// <c>MapPackage</c> record. That record is narrower than the schema — it required
/// <c>initialView.bbox</c> and <c>initialView.crs</c>, a locator <c>url</c>, a legend
/// <c>color</c>, a popup <c>fieldName</c>, a string <c>layerId</c>, and
/// <c>status</c>/<c>createdAt</c> — so the SDK's own canonical artifact was refused, and
/// every mismatch collapsed into one opaque <c>/body</c> diagnostic (honua-server#4898).
/// </para>
/// <para>
/// This walk admits what the schema admits for every member the server models and names
/// the offending member path otherwise. Two schema-required members,
/// <c>sourceBindings</c> and <c>mapSpec</c>, stay optional because server-authored
/// composition bodies predate them. A JSON <c>null</c> on an optional member reads as
/// absent, as it did through the record. Unknown members are permitted, as the schema
/// permits them. <c>widgets</c>, <c>layers</c> and <c>view</c> are gated by
/// <see cref="ValidateCompositionBlocks"/>.
/// </para>
/// </remarks>
public sealed partial class StudioPackageValidator
{
    private const string MapPackageFormat = "honua_map_package.v1";

    private static readonly string[] MapPackageStatuses = ["Draft", "Composing", "Ready", "Failed", "Expired"];

    private static readonly string[] MapSourceProtocols =
    [
        "geoservices_feature_service",
        "geoservices_map_service",
        "ogc_features",
        "ogc_maps",
        "ogc_tiles",
        "wfs",
        "wms",
        "wmts",
        "odata",
        "vector_tile",
        "raster_tile",
        "pmtiles",
        "workspace_artifact",
    ];

    private static void ValidateMapBody(JsonElement body, List<StudioValidationDiagnostic> diagnostics)
    {
        const string path = "/body";

        MapRequiredIdentifier(body, "mapPackageId", path, diagnostics);
        var format = MapString(body, "format", path, required: true, diagnostics);
        if (format is not null && !string.Equals(format, MapPackageFormat, StringComparison.Ordinal))
        {
            diagnostics.Add(Error("studio.map.format.invalid", $"{path}/format", "map body format must be honua_map_package.v1."));
        }

        MapEnum(body, "status", path, MapPackageStatuses, required: false, diagnostics);
        MapTimestamp(body, "createdAt", path, diagnostics);
        MapTimestamp(body, "updatedAt", path, diagnostics);
        MapString(body, "templateId", path, required: false, diagnostics);
        MapString(body, "themeId", path, required: false, diagnostics);
        MapString(body, "previewArtifactId", path, required: false, diagnostics);
        MapItems(body, "sourceBindings", path, ValidateMapSourceBinding, diagnostics);
        MapItems(body, "styleRefs", path, ValidateMapStyleRef, diagnostics);
        MapItems(body, "legend", path, ValidateMapLegendEntry, diagnostics);
        MapItems(body, "popupBindings", path, ValidateMapPopupBinding, diagnostics);
        MapItems(body, "labelBindings", path, ValidateMapLabelBinding, diagnostics);
        MapStringItems(body, "boundArtifacts", path, diagnostics);

        if (TryGetMapMember(body, "mapSpec", path, required: false, diagnostics, out var mapSpec))
        {
            // Structural MapLibre validity is delegated to the style-spec validator on the
            // client; the server only requires the style document to be an object.
            MapObject(mapSpec, $"{path}/mapSpec", "mapSpec", diagnostics);
        }

        if (TryGetMapMember(body, "initialView", path, required: false, diagnostics, out var initialView)
            && MapObject(initialView, $"{path}/initialView", "initialView", diagnostics))
        {
            ValidateMapInitialView(initialView, $"{path}/initialView", diagnostics);
        }
    }

    private static void ValidateMapSourceBinding(JsonElement binding, string path, List<StudioValidationDiagnostic> diagnostics)
    {
        MapRequiredIdentifier(binding, "sourceId", path, diagnostics);
        MapEnum(binding, "protocol", path, MapSourceProtocols, required: true, diagnostics);
        MapString(binding, "filter", path, required: false, diagnostics);
        MapString(binding, "attribution", path, required: false, diagnostics);

        if (TryGetMapMember(binding, "locator", path, required: true, diagnostics, out var locator)
            && MapObject(locator, $"{path}/locator", "locator", diagnostics))
        {
            // Every locator form is addressable: url, serviceId/layerId, collectionId,
            // typeName and entitySet are all optional in the schema.
            var locatorPath = $"{path}/locator";
            MapString(locator, "url", locatorPath, required: false, diagnostics);
            MapString(locator, "serviceId", locatorPath, required: false, diagnostics);
            MapString(locator, "typeName", locatorPath, required: false, diagnostics);
            MapString(locator, "entitySet", locatorPath, required: false, diagnostics);
            MapStringOrInteger(locator, "layerId", locatorPath, diagnostics);
            MapStringOrInteger(locator, "collectionId", locatorPath, diagnostics);
        }

        if (TryGetMapMember(binding, "metadata", path, required: false, diagnostics, out var metadata)
            && MapObject(metadata, $"{path}/metadata", "metadata", diagnostics))
        {
            foreach (var entry in metadata.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String)
                {
                    diagnostics.Add(Error(
                        "studio.map.member.type",
                        $"{path}/metadata/{EscapeJsonPointerSegment(entry.Name)}",
                        "metadata values must be strings."));
                }
            }
        }
    }

    private static void ValidateMapStyleRef(JsonElement styleRef, string path, List<StudioValidationDiagnostic> diagnostics)
    {
        MapRequiredIdentifier(styleRef, "styleId", path, diagnostics);
        MapString(styleRef, "label", path, required: false, diagnostics);
        MapString(styleRef, "presetId", path, required: false, diagnostics);

        if (TryGetMapMember(styleRef, "body", path, required: false, diagnostics, out var styleBody)
            && MapObject(styleBody, $"{path}/body", "body", diagnostics))
        {
            foreach (var layerOverride in styleBody.EnumerateObject())
            {
                MapObject(
                    layerOverride.Value,
                    $"{path}/body/{EscapeJsonPointerSegment(layerOverride.Name)}",
                    "style layer override",
                    diagnostics);
            }
        }
    }

    private static void ValidateMapLegendEntry(JsonElement entry, string path, List<StudioValidationDiagnostic> diagnostics)
    {
        MapString(entry, "label", path, required: true, diagnostics);
        MapString(entry, "color", path, required: false, diagnostics);
        MapString(entry, "iconUrl", path, required: false, diagnostics);
        MapNumber(entry, "minValue", path, diagnostics);
        MapNumber(entry, "maxValue", path, diagnostics);
    }

    private static void ValidateMapPopupBinding(JsonElement binding, string path, List<StudioValidationDiagnostic> diagnostics)
    {
        MapRequiredIdentifier(binding, "sourceId", path, diagnostics);
        MapString(binding, "fieldName", path, required: false, diagnostics);
        MapString(binding, "template", path, required: false, diagnostics);
        MapString(binding, "title", path, required: false, diagnostics);
    }

    private static void ValidateMapLabelBinding(JsonElement binding, string path, List<StudioValidationDiagnostic> diagnostics)
    {
        MapRequiredIdentifier(binding, "sourceId", path, diagnostics);
        MapRequiredIdentifier(binding, "fieldName", path, diagnostics);
        MapString(binding, "placement", path, required: false, diagnostics);
    }

    private static void ValidateMapInitialView(JsonElement initialView, string path, List<StudioValidationDiagnostic> diagnostics)
    {
        // Every member is optional: the camera form ({center, zoom, pitch, bearing}) and the
        // extent form ({bbox, crs}) are both valid, alone or together.
        if (TryGetMapMember(initialView, "bbox", path, required: false, diagnostics, out var bbox))
        {
            if (!TryReadMapNumbers(bbox, 4, out var extent))
            {
                diagnostics.Add(Error("studio.map.initial-view.bbox.invalid", $"{path}/bbox", "bbox must contain [minX,minY,maxX,maxY]."));
            }
            else if (extent[0] > extent[2] || extent[1] > extent[3])
            {
                diagnostics.Add(Error("studio.map.initial-view.bbox.order", $"{path}/bbox", "bbox min values must be less than or equal to max values."));
            }
        }

        if (TryGetMapMember(initialView, "center", path, required: false, diagnostics, out var center)
            && !TryReadMapNumbers(center, 2, out _))
        {
            diagnostics.Add(Error("studio.map.initial-view.center.invalid", $"{path}/center", "center must contain [longitude,latitude]."));
        }

        MapNumber(initialView, "zoom", path, diagnostics);
        MapNumber(initialView, "pitch", path, diagnostics);
        MapNumber(initialView, "bearing", path, diagnostics);

        if (TryGetMapMember(initialView, "crs", path, required: false, diagnostics, out var crs)
            && (crs.ValueKind != JsonValueKind.String || !IsValidCrs(crs.GetString()!)))
        {
            diagnostics.Add(Error("studio.map.initial-view.crs.invalid", $"{path}/crs", "initial view CRS must be an EPSG identifier or CRS URI."));
        }
    }

    private static bool TryGetMapMember(
        JsonElement value,
        string memberName,
        string path,
        bool required,
        List<StudioValidationDiagnostic> diagnostics,
        out JsonElement member)
    {
        if (value.TryGetProperty(memberName, out member) && member.ValueKind != JsonValueKind.Null)
        {
            return true;
        }

        if (required)
        {
            diagnostics.Add(Error("studio.map.member.required", $"{path}/{memberName}", $"{memberName} is required."));
        }

        return false;
    }

    private static bool MapObject(JsonElement value, string path, string description, List<StudioValidationDiagnostic> diagnostics)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        diagnostics.Add(Error("studio.map.member.type", path, $"{description} must be a JSON object."));
        return false;
    }

    private static string? MapString(
        JsonElement value,
        string memberName,
        string path,
        bool required,
        List<StudioValidationDiagnostic> diagnostics)
    {
        if (!TryGetMapMember(value, memberName, path, required, diagnostics, out var member))
        {
            return null;
        }

        if (member.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(Error("studio.map.member.type", $"{path}/{memberName}", $"{memberName} must be a string."));
            return null;
        }

        return member.GetString();
    }

    private static void MapRequiredIdentifier(
        JsonElement value,
        string memberName,
        string path,
        List<StudioValidationDiagnostic> diagnostics)
    {
        if (MapString(value, memberName, path, required: true, diagnostics) is { Length: 0 })
        {
            diagnostics.Add(Error("studio.map.member.empty", $"{path}/{memberName}", $"{memberName} must not be empty."));
        }
    }

    private static void MapEnum(
        JsonElement value,
        string memberName,
        string path,
        string[] allowed,
        bool required,
        List<StudioValidationDiagnostic> diagnostics)
    {
        var text = MapString(value, memberName, path, required, diagnostics);
        if (text is not null && !allowed.Contains(text, StringComparer.Ordinal))
        {
            diagnostics.Add(Error(
                "studio.map.member.enum",
                $"{path}/{memberName}",
                $"{memberName} must be one of: {string.Join(", ", allowed)}."));
        }
    }

    private static void MapNumber(JsonElement value, string memberName, string path, List<StudioValidationDiagnostic> diagnostics)
    {
        if (TryGetMapMember(value, memberName, path, required: false, diagnostics, out var member)
            && member.ValueKind != JsonValueKind.Number)
        {
            diagnostics.Add(Error("studio.map.member.type", $"{path}/{memberName}", $"{memberName} must be a number."));
        }
    }

    private static void MapStringOrInteger(JsonElement value, string memberName, string path, List<StudioValidationDiagnostic> diagnostics)
    {
        if (TryGetMapMember(value, memberName, path, required: false, diagnostics, out var member)
            && member.ValueKind != JsonValueKind.String
            && !(member.ValueKind == JsonValueKind.Number && member.TryGetInt64(out _)))
        {
            diagnostics.Add(Error("studio.map.member.type", $"{path}/{memberName}", $"{memberName} must be a string or an integer."));
        }
    }

    private static void MapTimestamp(JsonElement value, string memberName, string path, List<StudioValidationDiagnostic> diagnostics)
    {
        var text = MapString(value, memberName, path, required: false, diagnostics);
        if (text is not null
            && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            diagnostics.Add(Error("studio.map.timestamp.invalid", $"{path}/{memberName}", $"{memberName} must be an ISO-8601 timestamp."));
        }
    }

    private static void MapItems(
        JsonElement value,
        string memberName,
        string path,
        Action<JsonElement, string, List<StudioValidationDiagnostic>> validateItem,
        List<StudioValidationDiagnostic> diagnostics)
    {
        if (!TryGetMapMember(value, memberName, path, required: false, diagnostics, out var collection))
        {
            return;
        }

        if (collection.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(Error("studio.map.member.type", $"{path}/{memberName}", $"{memberName} must be an array."));
            return;
        }

        var index = 0;
        foreach (var item in collection.EnumerateArray())
        {
            var itemPath = $"{path}/{memberName}/{index++}";
            if (MapObject(item, itemPath, $"{memberName} entry", diagnostics))
            {
                validateItem(item, itemPath, diagnostics);
            }
        }
    }

    private static void MapStringItems(JsonElement value, string memberName, string path, List<StudioValidationDiagnostic> diagnostics)
    {
        if (!TryGetMapMember(value, memberName, path, required: false, diagnostics, out var collection))
        {
            return;
        }

        if (collection.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(Error("studio.map.member.type", $"{path}/{memberName}", $"{memberName} must be an array."));
            return;
        }

        var index = 0;
        foreach (var item in collection.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(Error("studio.map.member.type", $"{path}/{memberName}/{index}", $"{memberName} entries must be strings."));
            }

            index++;
        }
    }

    private static bool TryReadMapNumbers(JsonElement value, int count, out double[] numbers)
    {
        numbers = [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count)
        {
            return false;
        }

        var read = new double[count];
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out read[index]) || !double.IsFinite(read[index]))
            {
                return false;
            }

            index++;
        }

        numbers = read;
        return true;
    }
}
