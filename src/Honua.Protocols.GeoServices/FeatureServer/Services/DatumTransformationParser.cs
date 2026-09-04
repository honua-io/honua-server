// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Parses the Esri GeoServices <c>datumTransformation</c> query parameter into a
/// neutral request the catalog can resolve. The parameter is either a bare WKID
/// (for example <c>datumTransformation=1241</c>) or a composite JSON object
/// (<c>{"geoTransforms":[{"wkid":1241,"transformForward":false}]}</c>).
/// </summary>
internal static class DatumTransformationParser
{
    /// <summary>
    /// Attempts to parse the <c>datumTransformation</c> parameter value.
    /// </summary>
    /// <param name="value">Raw parameter value (may be null/empty).</param>
    /// <param name="request">The parsed request when successful.</param>
    /// <param name="errorMessage">An Esri-style error message when parsing fails.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent (no transformation requested,
    /// <paramref name="request"/> is <see langword="null"/>) or parses cleanly.
    /// <see langword="false"/> with <paramref name="errorMessage"/> set when the value
    /// is malformed.
    /// </returns>
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

/// <summary>
/// A neutral, parsed representation of a client-requested datum transformation.
/// </summary>
/// <param name="Wkid">The Esri geotransformation WKID requested by the client.</param>
/// <param name="TransformForward">Whether the transformation applies in its forward direction.</param>
/// <param name="TransformForwardSpecified">Whether the client explicitly supplied the direction.</param>
internal readonly record struct DatumTransformationRequest(
    int Wkid,
    bool TransformForward,
    bool TransformForwardSpecified = false);
