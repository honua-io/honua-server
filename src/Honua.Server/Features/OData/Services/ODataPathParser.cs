// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Server.Features.OData.Services;

internal enum ODataResourceKind
{
    Unknown,
    Layers,
    Layer,
    Features,
    Feature
}

internal readonly record struct ODataParsedPath(
    ODataResourceKind Kind,
    int? LayerId,
    long? ObjectId);

internal static partial class ODataPathParser
{
    public static bool TryParse(string url, out ODataParsedPath parsed, out string? errorMessage)
    {
        parsed = default;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            errorMessage = "Request URL is required.";
            return false;
        }

        var trimmed = url.Trim().TrimStart('/');
        if (trimmed.StartsWith("odata/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["odata/".Length..];
        }

        var path = trimmed.Split('?', 2)[0];

        if (path.Equals("Layers", StringComparison.OrdinalIgnoreCase))
        {
            parsed = new ODataParsedPath(ODataResourceKind.Layers, null, null);
            return true;
        }

        if (path.Equals("Features", StringComparison.OrdinalIgnoreCase))
        {
            parsed = new ODataParsedPath(ODataResourceKind.Features, null, null);
            return true;
        }

        var layerMatch = LayerRegex().Match(path);
        if (layerMatch.Success)
        {
            if (!TryParseInt(layerMatch.Groups["layerId"].Value, out var layerId))
            {
                errorMessage = "LayerId must be a valid integer.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(layerMatch.Groups["featureSegment"].Value))
            {
                parsed = new ODataParsedPath(ODataResourceKind.Layer, layerId, null);
                return true;
            }

            var featureSegment = layerMatch.Groups["featureSegment"].Value;
            featureSegment = featureSegment.TrimStart('/');
            if (featureSegment.Equals("Features", StringComparison.OrdinalIgnoreCase))
            {
                parsed = new ODataParsedPath(ODataResourceKind.Features, layerId, null);
                return true;
            }

            var featureMatch = LayerFeatureRegex().Match(path);
            if (featureMatch.Success)
            {
                if (!TryParseLong(featureMatch.Groups["objectId"].Value, out var objectId))
                {
                    errorMessage = "ObjectId must be a valid integer.";
                    return false;
                }

                parsed = new ODataParsedPath(ODataResourceKind.Feature, layerId, objectId);
                return true;
            }
        }

        var featureMatchNamed = FeatureRegex().Match(path);
        if (featureMatchNamed.Success)
        {
            var keyText = featureMatchNamed.Groups["keys"].Value;
            if (!TryParseFeatureKeys(keyText, out var layerId, out var objectId, out errorMessage))
            {
                return false;
            }

            parsed = new ODataParsedPath(ODataResourceKind.Feature, layerId, objectId);
            return true;
        }

        errorMessage = $"Unsupported OData URL format: {url}.";
        return false;
    }

    private static bool TryParseFeatureKeys(
        string keyText,
        out int layerId,
        out long objectId,
        out string? errorMessage)
    {
        layerId = default;
        objectId = default;
        errorMessage = null;

        if (keyText.Contains('=', StringComparison.Ordinal))
        {
            var parts = keyText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var kvp = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kvp.Length != 2)
                {
                    continue;
                }

                var name = kvp[0];
                var value = kvp[1].Trim('\'');

                if (name.Equals("LayerId", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseInt(value, out layerId))
                    {
                        errorMessage = "LayerId must be a valid integer.";
                        return false;
                    }
                }
                else if (name.Equals("ObjectId", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseLong(value, out objectId))
                    {
                        errorMessage = "ObjectId must be a valid integer.";
                        return false;
                    }
                }
            }

            if (layerId == 0 || objectId == 0)
            {
                errorMessage = "Both LayerId and ObjectId are required.";
                return false;
            }

            return true;
        }

        var positional = keyText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (positional.Length != 2 ||
            !TryParseInt(positional[0], out layerId) ||
            !TryParseLong(positional[1], out objectId))
        {
            errorMessage = "Invalid key predicate format for Features.";
            return false;
        }

        return true;
    }

    private static bool TryParseInt(string value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryParseLong(string value, out long result)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    [GeneratedRegex(@"^Layers\((?<layerId>[^)]+)\)(?<featureSegment>/Features(?:\((?<objectId>[^)]+)\))?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LayerRegex();

    [GeneratedRegex(@"^Layers\((?<layerId>[^)]+)\)/Features\((?<objectId>[^)]+)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LayerFeatureRegex();

    [GeneratedRegex(@"^Features\((?<keys>[^)]+)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeatureRegex();
}
