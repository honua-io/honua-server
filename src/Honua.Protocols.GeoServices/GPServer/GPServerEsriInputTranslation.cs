// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// Translates stock ArcGIS GP geometry inputs — <c>esriGeometry</c> JSON
/// (point / polyline / polygon / multipoint / envelope) and the
/// <c>GPFeatureRecordSetLayer</c> / <c>GPRecordSet</c> <c>FeatureSet</c>
/// envelope — into the canonical single-geometry process input contract
/// (base64-encoded WKB plus an SRID) that the Honua geoprocessing engine
/// consumes.
///
/// This is additive: native string inputs and base64-WKB inputs pass through
/// untouched. Only values whose JSON shape matches a recognised Esri geometry
/// or FeatureSet payload are rewritten.
///
/// Honua <c>geometry.*</c> processes execute on a SINGLE geometry + SRID, so a
/// multi-feature FeatureSet cannot be flattened to a single canonical input
/// without losing data. Such inputs are surfaced through
/// <see cref="EsriInputTranslationResult.RequiresFeatureCollectionExecution"/>
/// so the adapter can return an honest capability message rather than silently
/// dropping features.
/// </summary>
internal static class GPServerEsriInputTranslation
{
    /// <summary>
    /// Outcome of attempting to translate Esri geometry/FeatureSet inputs.
    /// </summary>
    internal readonly record struct EsriInputTranslationResult(
        Dictionary<string, string> Inputs,
        bool RequiresFeatureCollectionExecution,
        string? CapabilityMessage,
        int? InputSpatialReference)
    {
        public bool Translated { get; init; }
    }

    /// <summary>
    /// Walks every supplied input value and rewrites recognised Esri geometry
    /// JSON / FeatureSet payloads into canonical base64-WKB strings.
    /// </summary>
    /// <param name="inputs">
    /// The raw, protocol-control-stripped GP inputs (native names already in
    /// place). Values may be simple strings, base64 WKB, GP unit objects, or
    /// Esri geometry / FeatureSet JSON.
    /// </param>
    public static EsriInputTranslationResult Translate(IReadOnlyDictionary<string, string> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var translated = new Dictionary<string, string>(inputs.Count, StringComparer.OrdinalIgnoreCase);
        var anyTranslated = false;
        int? inputSpatialReference = null;

        foreach (var (key, value) in inputs)
        {
            if (!LooksLikeJsonObject(value))
            {
                translated[key] = value;
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(value);
            }
            catch (JsonException)
            {
                translated[key] = value;
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    translated[key] = value;
                    continue;
                }

                // FeatureSet (GPFeatureRecordSetLayer / GPRecordSet): { "features": [...], "geometryType": "...", "spatialReference": {...} }
                if (root.TryGetProperty("features", out var featuresProp) &&
                    featuresProp.ValueKind == JsonValueKind.Array)
                {
                    var featureCount = featuresProp.GetArrayLength();
                    if (featureCount == 0)
                    {
                        return new EsriInputTranslationResult(
                            translated,
                            RequiresFeatureCollectionExecution: false,
                            CapabilityMessage:
                                $"Input '{key}' is an empty FeatureSet; supply at least one feature with a geometry.",
                            InputSpatialReference: inputSpatialReference)
                        { Translated = false };
                    }

                    if (featureCount > 1)
                    {
                        // Multi-feature execution requires the server feature-collection /
                        // layer-level execution stream, which is not part of the
                        // single-geometry geometry.* contract. Do not fake a result.
                        return new EsriInputTranslationResult(
                            translated,
                            RequiresFeatureCollectionExecution: true,
                            CapabilityMessage:
                                $"Input '{key}' is a FeatureSet carrying {featureCount} features. " +
                                "Honua geometry.* tasks execute on a single geometry; multi-feature " +
                                "execution requires the feature-collection/layer-level execution stream " +
                                "(use a layer-scoped task such as analytics.* / generalization.* / " +
                                "conversion.feature-project, or submit one feature per request).",
                            InputSpatialReference: inputSpatialReference)
                        { Translated = false };
                    }

                    var feature = featuresProp[0];
                    var setSr = ReadSpatialReference(root);
                    if (!feature.TryGetProperty("geometry", out var featureGeometry) ||
                        featureGeometry.ValueKind != JsonValueKind.Object)
                    {
                        return new EsriInputTranslationResult(
                            translated,
                            RequiresFeatureCollectionExecution: false,
                            CapabilityMessage:
                                $"Input '{key}' FeatureSet feature is missing a 'geometry' object.",
                            InputSpatialReference: inputSpatialReference)
                        { Translated = false };
                    }

                    if (!TryConvertEsriGeometry(featureGeometry, setSr, out var wkbBase64, out var sr, out var convertError))
                    {
                        return new EsriInputTranslationResult(
                            translated,
                            RequiresFeatureCollectionExecution: false,
                            CapabilityMessage: $"Input '{key}' FeatureSet geometry could not be translated: {convertError}",
                            InputSpatialReference: inputSpatialReference)
                        { Translated = false };
                    }

                    translated[key] = wkbBase64;
                    inputSpatialReference ??= sr;
                    anyTranslated = true;
                    continue;
                }

                // Bare esriGeometry JSON: point/polyline/polygon/multipoint/envelope.
                if (IsEsriGeometryShape(root))
                {
                    if (!TryConvertEsriGeometry(root, parentSpatialReference: null, out var wkbBase64, out var sr, out var convertError))
                    {
                        return new EsriInputTranslationResult(
                            translated,
                            RequiresFeatureCollectionExecution: false,
                            CapabilityMessage: $"Input '{key}' esriGeometry could not be translated: {convertError}",
                            InputSpatialReference: inputSpatialReference)
                        { Translated = false };
                    }

                    translated[key] = wkbBase64;
                    inputSpatialReference ??= sr;
                    anyTranslated = true;
                    continue;
                }

                // Unrecognised JSON object (e.g. GP unit / data-file shapes) — pass through.
                translated[key] = value;
            }
        }

        // When the caller supplied an esriGeometry/FeatureSet but no explicit
        // 'srid' input, surface the geometry's spatial reference so the adapter
        // can populate the canonical srid parameter the engine requires.
        if (anyTranslated && inputSpatialReference is { } sridValue)
        {
            if (!translated.ContainsKey("srid"))
            {
                translated["srid"] = sridValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return new EsriInputTranslationResult(
            translated,
            RequiresFeatureCollectionExecution: false,
            CapabilityMessage: null,
            InputSpatialReference: inputSpatialReference)
        { Translated = anyTranslated };
    }

    private static bool LooksLikeJsonObject(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var span = value.AsSpan().TrimStart();
        return span.Length > 0 && span[0] == '{';
    }

    private static bool IsEsriGeometryShape(JsonElement root)
    {
        // esriGeometryPoint: { x, y }
        if (root.TryGetProperty("x", out _) && root.TryGetProperty("y", out _))
        {
            return true;
        }

        // esriGeometryEnvelope: { xmin, ymin, xmax, ymax }
        if (root.TryGetProperty("xmin", out _) && root.TryGetProperty("ymin", out _) &&
            root.TryGetProperty("xmax", out _) && root.TryGetProperty("ymax", out _))
        {
            return true;
        }

        // esriGeometryPolyline / esriGeometryPolygon / esriGeometryMultipoint
        return root.TryGetProperty("paths", out _)
            || root.TryGetProperty("rings", out _)
            || root.TryGetProperty("points", out _);
    }

    private static bool TryConvertEsriGeometry(
        JsonElement geometryElement,
        int? parentSpatialReference,
        out string wkbBase64,
        out int? spatialReference,
        out string? error)
    {
        wkbBase64 = string.Empty;
        spatialReference = null;
        error = null;

        var rawJson = geometryElement.GetRawText();
        GeoServicesGeometry? geometry;
        try
        {
            geometry = JsonSerializer.Deserialize(rawJson, FeatureServerJsonContext.Default.GeoServicesGeometry);
        }
        catch (JsonException)
        {
            error = "Geometry JSON could not be parsed.";
            return false;
        }

        if (geometry is null)
        {
            error = "geometry JSON deserialized to null.";
            return false;
        }

        var srid = geometry.SpatialReference?.Wkid
            ?? geometry.SpatialReference?.LatestWkid
            ?? parentSpatialReference;

        try
        {
            var wkb = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(geometry, srid);
            wkbBase64 = Convert.ToBase64String(wkb);
            spatialReference = srid;
            return true;
        }
        catch (ArgumentException)
        {
            error = "Geometry could not be converted.";
            return false;
        }
    }

    private static int? ReadSpatialReference(JsonElement root)
    {
        if (!root.TryGetProperty("spatialReference", out var srElement) ||
            srElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (srElement.TryGetProperty("wkid", out var wkid) && wkid.ValueKind == JsonValueKind.Number)
        {
            return wkid.GetInt32();
        }

        if (srElement.TryGetProperty("latestWkid", out var latest) && latest.ValueKind == JsonValueKind.Number)
        {
            return latest.GetInt32();
        }

        return null;
    }
}
