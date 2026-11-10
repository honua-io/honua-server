// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal static class DatumTransformationParser
{
    public static bool TryParse(string? value, out DatumTransformationRequest? request, [NotNullWhen(false)] out string? errorMessage)
    {
        request = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();

        // Bare WKID form.
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wkid))
        {
            request = new DatumTransformationRequest(wkid, TransformForward: true, TransformForwardSpecified: false);
            return true;
        }

        // JSON object form: either a single transformation ({"wkid":1241}) or a
        // composite ({"geoTransforms":[...]}).
        if (trimmed.StartsWith('{'))
        {
            return TryParseComposite(trimmed, out request, out errorMessage);
        }

        errorMessage = $"Invalid datumTransformation value '{value}'. Expected a transformation WKID or a geoTransforms object.";
        return false;
    }

    private static bool TryParseComposite(string json, out DatumTransformationRequest? request, [NotNullWhen(false)] out string? errorMessage)
    {
        request = null;
        errorMessage = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                errorMessage = "Invalid datumTransformation: expected a transformation object.";
                return false;
            }

            // GeometryServer's standard `transformation` parameter accepts the same
            // single-WKID object shape returned by findTransformations.
            if (root.TryGetProperty("wkid", out _))
            {
                return TryParseTransform(root, out request, out errorMessage);
            }

            if (!root.TryGetProperty("geoTransforms", out var geoTransforms) ||
                geoTransforms.ValueKind != JsonValueKind.Array ||
                geoTransforms.GetArrayLength() == 0)
            {
                errorMessage = "Invalid datumTransformation: 'geoTransforms' must be a non-empty array.";
                return false;
            }

            // Honua applies a single geographic transformation per reprojection; the
            // first geoTransform entry is honored (composite multi-step chains are a
            // documented follow-up).
            return TryParseTransform(geoTransforms[0], out request, out errorMessage);
        }
        catch (JsonException)
        {
            errorMessage = "Invalid datumTransformation: malformed JSON.";
            return false;
        }
    }

    private static bool TryParseTransform(
        JsonElement transform,
        out DatumTransformationRequest? request,
        [NotNullWhen(false)] out string? errorMessage)
    {
        request = null;
        errorMessage = null;

        if (transform.ValueKind != JsonValueKind.Object ||
            !transform.TryGetProperty("wkid", out var wkidElement) ||
            wkidElement.ValueKind != JsonValueKind.Number ||
            !wkidElement.TryGetInt32(out var wkid))
        {
            errorMessage = "Invalid datumTransformation: each geoTransform requires a numeric 'wkid'.";
            return false;
        }

        var transformForward = true;
        var transformForwardSpecified = transform.TryGetProperty("transformForward", out var forwardElement);
        if (transformForwardSpecified &&
            forwardElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errorMessage = "Invalid datumTransformation: 'transformForward' must be a boolean.";
            return false;
        }

        if (transformForwardSpecified)
        {
            transformForward = forwardElement.GetBoolean();
        }

        request = new DatumTransformationRequest(wkid, transformForward, transformForwardSpecified);
        return true;
    }
}

internal readonly record struct DatumTransformationRequest(
    int Wkid,
    bool TransformForward,
    bool TransformForwardSpecified = false);
