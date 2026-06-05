// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Ai.MapGeneration;

/// <summary>
/// Structural, DB-independent validator for a generated <see cref="MapPackage"/> body. The Studio
/// publish path (<c>StudioPackageValidator.ValidateFamilyBody</c>) only deserializes the family body
/// and checks the envelope/initial-view shape — the real layer existence, style resolution, and
/// source reachability bind at publish time against live metadata. So, exactly like
/// <c>FormPackageValidator</c> (which the form generation gate runs with a stub target resolver) this
/// validator checks only what is knowable from a natural-language prompt: a non-empty layer/source
/// list with valid source refs, a chosen basemap, and a well-formed initial extent. It emits issues
/// using the same <see cref="MapPackageValidationResult"/>/<see cref="MapValidationIssue"/> shape the
/// form family uses, and tags runtime-binding gaps with "*NotFound"/"*NotResolved" codes that the
/// generation gate tolerates.
/// </summary>
internal static class MapGenerationStructuralValidator
{
    /// <summary>Supported source protocol tokens (mirrors <see cref="SourceProtocol"/> JSON names).</summary>
    internal static readonly string[] SourceProtocols =
    [
        "geoservices_feature_service", "geoservices_map_service", "ogc_features", "ogc_maps",
        "ogc_tiles", "wfs", "wms", "odata", "vector_tile", "raster_tile", "workspace_artifact", "pmtiles"
    ];

    /// <summary>The server-canonical map package body format.</summary>
    internal const string MapPackageFormat = "honua_map_package.v1";

    public static MapPackageValidationResult Validate(MapPackage? map)
    {
        var issues = new List<MapValidationIssue>();
        if (map is null)
        {
            issues.Add(Error("mapMissing", "/", "The map package body is missing."));
            return Result(issues);
        }

        if (string.IsNullOrWhiteSpace(map.Format))
        {
            issues.Add(Error("formatMissing", "/format", "format is required."));
        }
        else if (!string.Equals(map.Format, MapPackageFormat, StringComparison.Ordinal))
        {
            issues.Add(Error("formatInvalid", "/format", $"format must be '{MapPackageFormat}'."));
        }

        // A map must bind at least one data source (its "layer list"). The source's reachability and
        // the underlying layer existence are runtime bindings resolved at publish; here we only require
        // a structurally complete locator.
        if (map.SourceBindings is not { Count: > 0 })
        {
            issues.Add(Error("layersEmpty", "/sourceBindings", "A map must declare at least one source binding (layer)."));
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; map.SourceBindings is not null && i < map.SourceBindings.Count; i++)
        {
            var binding = map.SourceBindings[i];
            var path = $"/sourceBindings/{i}";
            if (string.IsNullOrWhiteSpace(binding.SourceId))
            {
                issues.Add(Error("sourceIdMissing", $"{path}/sourceId", "sourceId is required."));
            }
            else if (!sourceIds.Add(binding.SourceId))
            {
                issues.Add(Error("sourceIdDuplicate", $"{path}/sourceId", $"sourceId '{binding.SourceId}' is duplicated."));
            }

            if (binding.Locator is null || string.IsNullOrWhiteSpace(binding.Locator.Url))
            {
                // The concrete URL/service is bound at publish; tolerate it as a runtime binding so a
                // prompt that names a layer descriptively still generates.
                issues.Add(Warning("layerNotResolved", $"{path}/locator", "Source locator URL is not yet bound; resolved at publish."));
            }
        }

        // Style refs reference styles resolved at publish; only require a non-empty styleId when present.
        for (var i = 0; map.StyleRefs is not null && i < map.StyleRefs.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(map.StyleRefs[i].StyleId))
            {
                issues.Add(Error("styleIdMissing", $"/styleRefs/{i}/styleId", "styleId is required for a style reference."));
            }
        }

        // Popup/label bindings must reference a declared source (structural cross-ref).
        for (var i = 0; map.PopupBindings is not null && i < map.PopupBindings.Count; i++)
        {
            var popup = map.PopupBindings[i];
            if (!string.IsNullOrWhiteSpace(popup.SourceId) && sourceIds.Count > 0 && !sourceIds.Contains(popup.SourceId))
            {
                issues.Add(Warning("popupSourceNotFound", $"/popupBindings/{i}/sourceId", $"popup references unknown source '{popup.SourceId}'; bound at publish."));
            }
        }

        for (var i = 0; map.LabelBindings is not null && i < map.LabelBindings.Count; i++)
        {
            var label = map.LabelBindings[i];
            if (!string.IsNullOrWhiteSpace(label.SourceId) && sourceIds.Count > 0 && !sourceIds.Contains(label.SourceId))
            {
                issues.Add(Warning("labelSourceNotFound", $"/labelBindings/{i}/sourceId", $"label references unknown source '{label.SourceId}'; bound at publish."));
            }
        }

        ValidateInitialView(map.InitialView, issues);

        return Result(issues);
    }

    private static void ValidateInitialView(MapInitialView? view, List<MapValidationIssue> issues)
    {
        if (view is null)
        {
            issues.Add(Error("extentMissing", "/initialView", "A map must declare an initial view (extent)."));
            return;
        }

        if (view.Bbox is null || view.Bbox.Length != 4)
        {
            issues.Add(Error("extentInvalid", "/initialView/bbox", "bbox must contain [minLon, minLat, maxLon, maxLat]."));
        }
        else if (view.Bbox[0] > view.Bbox[2] || view.Bbox[1] > view.Bbox[3])
        {
            issues.Add(Error("extentOrder", "/initialView/bbox", "bbox min values must be less than or equal to max values."));
        }

        if (string.IsNullOrWhiteSpace(view.Crs))
        {
            issues.Add(Error("extentCrsMissing", "/initialView/crs", "initial view crs is required (e.g. EPSG:4326)."));
        }
        else if (!IsValidCrs(view.Crs))
        {
            issues.Add(Error("extentCrsInvalid", "/initialView/crs", "crs must be an EPSG identifier or CRS URI."));
        }
    }

    private static bool IsValidCrs(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            var srid = trimmed[5..];
            return srid.Length > 0 && srid.All(static c => c is >= '0' and <= '9');
        }

        return trimmed.StartsWith("http://www.opengis.net/def/crs/", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("urn:ogc:def:crs:", StringComparison.OrdinalIgnoreCase);
    }

    private static MapPackageValidationResult Result(List<MapValidationIssue> issues) => new()
    {
        IsValid = !issues.Any(static i => string.Equals(i.Severity, "error", StringComparison.OrdinalIgnoreCase)),
        Issues = issues.ToArray(),
    };

    private static MapValidationIssue Error(string code, string path, string message) =>
        new() { Code = code, Severity = "error", Path = path, Message = message };

    private static MapValidationIssue Warning(string code, string path, string message) =>
        new() { Code = code, Severity = "warning", Path = path, Message = message };
}

/// <summary>Validation result for a generated map package, mirroring <c>FormPackageValidationResult</c>.</summary>
public sealed class MapPackageValidationResult
{
    [JsonPropertyName("isValid")]
    public bool IsValid { get; init; }

    [JsonPropertyName("issues")]
    public MapValidationIssue[] Issues { get; init; } = [];
}

/// <summary>Single map-package validation issue, mirroring <c>FormValidationIssue</c>.</summary>
public sealed class MapValidationIssue
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "error";

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
