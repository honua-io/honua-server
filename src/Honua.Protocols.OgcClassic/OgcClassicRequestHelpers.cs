// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Protocols.Ogc.Classic;

internal static class OgcClassicRequestHelpers
{
    internal const int DefaultFeatureInfoCount = 1;
    internal const int DefaultFeatureInfoTolerancePixels = 3;
    internal const string PngMimeType = "image/png";
    internal const string PlainTextMimeType = "text/plain";
    internal const string JsonMimeType = "application/json";
    internal const string CiteServiceName = "cite";
    internal const string CiteTerrainLayerTitle = "cite:Terrain";

    internal static string? GetQueryValue(IQueryCollection query, string key)
        => query.TryGetValue(key, out var value) ? value.ToString() : null;

    internal static bool TryGetRequiredQueryValue(
        IQueryCollection query,
        string key,
        out string value,
        bool allowEmpty = false)
    {
        value = string.Empty;
        if (!query.TryGetValue(key, out var raw))
        {
            return false;
        }

        value = raw.ToString();
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return true;
    }

    internal static string FormatFeatureInfoValue(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        return value.ToString() ?? string.Empty;
    }

    internal static bool TryNormalizeFeatureInfoFormat(string? format, out string normalizedFormat)
    {
        normalizedFormat = PlainTextMimeType;
        if (string.IsNullOrWhiteSpace(format))
        {
            return true;
        }

        if (string.Equals(format, PlainTextMimeType, StringComparison.OrdinalIgnoreCase))
        {
            normalizedFormat = PlainTextMimeType;
            return true;
        }

        if (string.Equals(format, JsonMimeType, StringComparison.OrdinalIgnoreCase))
        {
            normalizedFormat = JsonMimeType;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the resource's display name with any leading namespace prefix removed,
    /// falling back to the publication's layer index when the resource has no name.
    /// </summary>
    internal static string GetWmsLayerName(MetadataV2Resource resource, MetadataV2Publication? publication = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var name = resource.Metadata.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            if (publication?.LayerIndex is int layerIndex)
            {
                return layerIndex.ToString(CultureInfo.InvariantCulture);
            }
            return resource.Metadata.Id;
        }
        return TrimNamespacePrefix(name);
    }

    private static string TrimNamespacePrefix(string name)
    {
        var fullName = name.Trim();
        var separatorIndex = fullName.LastIndexOf(':');
        return separatorIndex >= 0 && separatorIndex < fullName.Length - 1
            ? fullName[(separatorIndex + 1)..]
            : fullName;
    }

    internal static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
