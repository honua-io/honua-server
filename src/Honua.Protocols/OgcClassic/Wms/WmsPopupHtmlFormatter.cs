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
        var defaultTitle = entry.Resource.Metadata.Title
            ?? entry.Resource.Metadata.Name
            ?? entry.LayerName;

        var titleTemplate = TryGetStringProperty(popupInfo, "title");
        var resolvedTitle = !string.IsNullOrWhiteSpace(titleTemplate)
            ? InterpolateTemplate(titleTemplate, entry.Attributes)
            : defaultTitle;
        if (string.IsNullOrWhiteSpace(resolvedTitle))
        {
            resolvedTitle = defaultTitle;
        }

        var descriptionTemplate = TryGetStringProperty(popupInfo, "description");
        var resolvedDescription = !string.IsNullOrWhiteSpace(descriptionTemplate)
            ? InterpolateTemplate(descriptionTemplate, entry.Attributes)
            : null;

        sb.Append("  <section class=\"honua-feature-info\" data-layer=\"")
            .Append(WebUtility.HtmlEncode(entry.LayerName))
            .AppendLine("\">");
        sb.Append("    <h2>")
            .Append(WebUtility.HtmlEncode(resolvedTitle))
            .AppendLine("</h2>");

        if (!string.IsNullOrWhiteSpace(resolvedDescription))
        {
            sb.Append("    <p class=\"honua-popup-description\">")
                .Append(WebUtility.HtmlEncode(resolvedDescription))
                .AppendLine("</p>");
        }

        var rows = ResolveRows(entry.Resource, entry.Attributes, popupInfo);
        if (rows.Count > 0)
        {
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

        sb.AppendLine("  </section>");
    }

    private static List<(string Label, string Value)> ResolveRows(
        MetadataV2Object resource,
        IReadOnlyDictionary<string, object?> attributes,
        JsonElement? popupInfo)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in resource.SchemaFields)
        {
            if (!string.IsNullOrWhiteSpace(field.Alias))
            {
                aliases[field.Name] = field.Alias;
            }
        }

        var rows = new List<(string Label, string Value)>();
        if (popupInfo is { ValueKind: JsonValueKind.Object } popup &&
            popup.TryGetProperty("fieldInfos", out var fieldInfos) &&
            fieldInfos.ValueKind == JsonValueKind.Array)
        {
            foreach (var fieldInfo in fieldInfos.EnumerateArray())
            {
                if (fieldInfo.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (fieldInfo.TryGetProperty("visible", out var visibleProp) &&
                    visibleProp.ValueKind == JsonValueKind.False)
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
                    : aliases.TryGetValue(fieldName, out var schemaAlias)
                        ? schemaAlias
                        : fieldName;

                rows.Add((label, FormatFeatureInfoValue(rawValue)));
            }

            return rows;
        }

        foreach (var attribute in attributes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var label = aliases.TryGetValue(attribute.Key, out var schemaAlias)
                ? schemaAlias
                : attribute.Key;
            rows.Add((label, FormatFeatureInfoValue(attribute.Value)));
        }

        return rows;
    }

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
