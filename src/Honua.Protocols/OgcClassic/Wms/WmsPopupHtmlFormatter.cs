// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Metadata.Domain.V2;
using static Honua.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Protocols.Ogc.Classic.Wms;

internal static partial class WmsPopupHtmlFormatter
{
    internal readonly record struct FeatureEntry(
        string LayerName,
        MetadataV2Object Resource,
        IReadOnlyDictionary<string, object?> Attributes);

    [GeneratedRegex(@"\{([^{}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    internal static string BuildHtml(IReadOnlyList<FeatureEntry> features)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <title>Feature Info</title>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        if (features.Count == 0)
        {
            sb.AppendLine("  <p>No features found.</p>");
        }
        else
        {
            foreach (var entry in features)
            {
                AppendFeatureSection(sb, entry);
            }
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static void AppendFeatureSection(StringBuilder sb, FeatureEntry entry)
    {
        var popupInfo = ResolvePopupInfo(entry.Resource);
        var resolvedTitle = ResolveTitle(entry, popupInfo);
        var resolvedDescription = ResolveDescription(entry, popupInfo);

        sb.Append("  <section class=\"honua-feature-info\" data-layer=\"")
            .Append(WebUtility.HtmlEncode(entry.LayerName))
            .AppendLine("\">");
        sb.Append("    <h2>").Append(WebUtility.HtmlEncode(resolvedTitle)).AppendLine("</h2>");

        if (!string.IsNullOrWhiteSpace(resolvedDescription))
        {
            sb.Append("    <p class=\"honua-popup-description\">")
                .Append(WebUtility.HtmlEncode(resolvedDescription))
                .AppendLine("</p>");
        }

        AppendRowsTable(sb, ResolveRows(entry.Resource, entry.Attributes, popupInfo));
        sb.AppendLine("  </section>");
    }

    private static string ResolveTitle(FeatureEntry entry, JsonElement? popupInfo)
    {
        var defaultTitle = entry.Resource.Metadata.Title ?? entry.Resource.Metadata.Name ?? entry.LayerName;
        var titleTemplate = TryGetStringProperty(popupInfo, "title");
        if (string.IsNullOrWhiteSpace(titleTemplate))
        {
            return defaultTitle;
        }

        var interpolated = InterpolateTemplate(titleTemplate, entry.Attributes);
        return string.IsNullOrWhiteSpace(interpolated) ? defaultTitle : interpolated;
    }

    private static string? ResolveDescription(FeatureEntry entry, JsonElement? popupInfo)
    {
        var descriptionTemplate = TryGetStringProperty(popupInfo, "description");
        return !string.IsNullOrWhiteSpace(descriptionTemplate)
            ? InterpolateTemplate(descriptionTemplate, entry.Attributes)
            : null;
    }

    private static void AppendRowsTable(StringBuilder sb, List<(string Label, string Value)> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        sb.AppendLine("    <table>");
        sb.AppendLine("      <tbody>");
        foreach (var (label, value) in rows)
        {
            sb.Append("        <tr><th>")
                .Append(WebUtility.HtmlEncode(label))
                .Append("</th><td>")
                .Append(WebUtility.HtmlEncode(value))
                .AppendLine("</td></tr>");
        }

        sb.AppendLine("      </tbody>");
        sb.AppendLine("    </table>");
    }

    private static List<(string Label, string Value)> ResolveRows(
        MetadataV2Object resource,
        IReadOnlyDictionary<string, object?> attributes,
        JsonElement? popupInfo)
    {
        var aliases = BuildFieldAliases(resource);
        if (popupInfo is { ValueKind: JsonValueKind.Object } popup &&
            popup.TryGetProperty("fieldInfos", out var fieldInfos) &&
            fieldInfos.ValueKind == JsonValueKind.Array)
        {
            return ResolveConfiguredRows(fieldInfos, attributes, aliases);
        }

        return attributes
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (aliases.TryGetValue(pair.Key, out var alias) ? alias : pair.Key, FormatFeatureInfoValue(pair.Value)))
            .ToList();
    }

    private static Dictionary<string, string> BuildFieldAliases(MetadataV2Object resource)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in resource.SchemaFields)
        {
            if (!string.IsNullOrWhiteSpace(field.Alias))
            {
                aliases[field.Name] = field.Alias;
            }
        }

        return aliases;
    }

    private static List<(string Label, string Value)> ResolveConfiguredRows(
        JsonElement fieldInfos,
        IReadOnlyDictionary<string, object?> attributes,
        Dictionary<string, string> aliases)
    {
        var rows = new List<(string Label, string Value)>();
        foreach (var fieldInfo in fieldInfos.EnumerateArray())
        {
            if (fieldInfo.ValueKind != JsonValueKind.Object || IsHiddenFieldInfo(fieldInfo))
            {
                continue;
            }

            var fieldName = TryGetStringProperty(fieldInfo, "fieldName");
            if (string.IsNullOrWhiteSpace(fieldName) || !attributes.TryGetValue(fieldName, out var rawValue))
            {
                continue;
            }

            var configuredLabel = TryGetStringProperty(fieldInfo, "label");
            var label = !string.IsNullOrWhiteSpace(configuredLabel)
                ? configuredLabel
                : aliases.TryGetValue(fieldName, out var schemaAlias) ? schemaAlias : fieldName;
            rows.Add((label, FormatFeatureInfoValue(rawValue)));
        }

        return rows;
    }

    private static bool IsHiddenFieldInfo(JsonElement fieldInfo)
        => fieldInfo.TryGetProperty("visible", out var visibleProp) && visibleProp.ValueKind == JsonValueKind.False;

    private static JsonElement? ResolvePopupInfo(MetadataV2Object resource)
    {
        if (resource.Extensions is not null &&
            (resource.Extensions.TryGetValue("geoservices:popupInfo", out var popupInfo) ||
             resource.Extensions.TryGetValue("popupInfo", out popupInfo)) &&
            popupInfo.ValueKind == JsonValueKind.Object)
        {
            return popupInfo;
        }

        return null;
    }

    private static string? TryGetStringProperty(JsonElement? element, string propertyName)
        => element is { ValueKind: JsonValueKind.Object } obj
            ? TryGetStringProperty(obj, propertyName)
            : null;

    private static string? TryGetStringProperty(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string InterpolateTemplate(string template, IReadOnlyDictionary<string, object?> attributes)
        => PlaceholderRegex().Replace(template, match =>
        {
            var fieldName = match.Groups[1].Value.Trim();
            return attributes.TryGetValue(fieldName, out var value)
                ? FormatFeatureInfoValue(value)
                : string.Empty;
        });
}
