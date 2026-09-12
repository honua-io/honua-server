// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    /// <summary>
    /// Normalizes a MapServer extent value into the comma-delimited "xmin,ymin,xmax,ymax"
    /// form. Accepts both the legacy comma string and the Esri JSON envelope object
    /// ({"xmin":..,"ymin":..,"xmax":..,"ymax":..}) that ArcGIS Pro, the ArcGIS JS SDK and arcpy emit.
    /// </summary>
    private static bool TryNormalizeMapEnvelope(
        string? envelopeValue,
        string parameterName,
        out string? normalized,
        out string? error)
    {
        normalized = envelopeValue;
        error = null;

        if (string.IsNullOrWhiteSpace(envelopeValue))
        {
            return true;
        }

        var trimmed = envelopeValue.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            // Legacy comma form (or anything else) — let the shared bbox parser decide.
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(envelopeValue);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"Invalid {parameterName} parameter. Expected an Esri JSON envelope or xmin,ymin,xmax,ymax.";
                return false;
            }

            var root = doc.RootElement;
            var xmin = TryGetDouble(root, "xmin");
            var ymin = TryGetDouble(root, "ymin");
            var xmax = TryGetDouble(root, "xmax");
            var ymax = TryGetDouble(root, "ymax");

            if (!(xmin.HasValue && ymin.HasValue && xmax.HasValue && ymax.HasValue))
            {
                error = $"{parameterName} envelope must include xmin, ymin, xmax, and ymax.";
                return false;
            }

            normalized = string.Create(
                CultureInfo.InvariantCulture,
                $"{xmin.Value},{ymin.Value},{xmax.Value},{ymax.Value}");
            return true;
        }
        catch (JsonException)
        {
            error = $"Invalid {parameterName} parameter. Expected an Esri JSON envelope or xmin,ymin,xmax,ymax.";
            return false;
        }
    }
}
