// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Db.Postgres.Features.Migration;

/// <summary>
/// Maps ArcGIS field names onto the columns of an imported PostGIS table.
/// <c>objectid</c> is always reserved for the surrogate key. <c>geom</c> is reserved when the
/// layer has a geometry type. Any other collision, including two source names that sanitize to
/// the same identifier, is disambiguated deterministically.
/// </summary>
internal static class GeoservicesImportColumns
{
    internal const int MaxIdentifierLength = 63;

    internal static GeoservicesImportColumnMap Map(GeoservicesLayerInfo layerInfo)
    {
        ArgumentNullException.ThrowIfNull(layerInfo);
        var spatial = !string.IsNullOrEmpty(layerInfo.GeometryType);
        var used = new HashSet<string>(StringComparer.Ordinal) { "objectid" };
        if (spatial)
        {
            used.Add("geom");
        }

        var columns = new List<GeoservicesImportColumn>();
        foreach (var field in layerInfo.Fields)
        {
            if (field.IsObjectId || GeoservicesImportService.IsGeometryField(field))
            {
                continue;
            }

            var sanitized = field.Name.SanitizeFieldName();
            var columnName = sanitized;
            var renamed = false;
            if (used.Contains(columnName))
            {
                columnName = Disambiguate(sanitized, used);
                renamed = true;
            }

            used.Add(columnName);
            columns.Add(new GeoservicesImportColumn(field, sanitized, columnName, renamed));
        }

        return new GeoservicesImportColumnMap(columns, spatial);
    }

    internal static Dictionary<string, MetadataV2FieldDomain> BuildFieldDomainMap(GeoservicesLayerInfo layerInfo)
    {
        var map = Map(layerInfo);
        var domains = new Dictionary<string, MetadataV2FieldDomain>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in layerInfo.Fields)
        {
            if (field.Domain is null || string.IsNullOrWhiteSpace(field.Name))
            {
                continue;
            }

            var key = field.IsObjectId
                ? "objectid"
                : GeoservicesImportService.IsGeometryField(field)
                    ? map.Spatial ? "geom" : map.MapReference(field.Name)
                    : map.MapReference(field.Name);
            domains[key] = field.Domain;
        }

        return domains;
    }

    internal static MetadataV2Subtypes? RemapSubtypes(MetadataV2Subtypes? subtypes, GeoservicesImportColumnMap map)
    {
        if (subtypes is null)
        {
            return null;
        }

        return subtypes with
        {
            SubtypeField = map.MapReference(subtypes.SubtypeField),
            Subtypes = subtypes.Subtypes
                .Select(subtype => subtype with
                {
                    FieldOverrides = RemapOverrides(subtype.FieldOverrides, map)
                })
                .ToArray()
        };
    }

    internal static IReadOnlyList<MetadataV2AttributeRule>? RemapAttributeRules(
        IReadOnlyList<MetadataV2AttributeRule>? rules,
        GeoservicesImportColumnMap map)
    {
        if (rules is null)
        {
            return null;
        }

        return rules
            .Select(rule => rule with
            {
                FieldName = string.IsNullOrWhiteSpace(rule.FieldName)
                    ? rule.FieldName
                    : map.MapReference(rule.FieldName),
                ScriptExpression = RemapFeatureReferences(rule.ScriptExpression, map)
            })
            .ToArray();
    }

    /// <summary>
    /// Rewrites <c>$feature.Name</c> and <c>$feature["Name"]</c> when the import
    /// allocated a different column. A case-only change is left alone because field
    /// lookup is already case-insensitive. Text inside quotes is not rewritten.
    /// </summary>
    internal static string RemapFeatureReferences(string? expression, GeoservicesImportColumnMap map)
    {
        if (string.IsNullOrEmpty(expression))
        {
            return expression ?? string.Empty;
        }

        var rewritten = new StringBuilder(expression.Length);
        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (c is '\'' or '"')
            {
                var end = ClosingQuote(expression, i);
                if (end < 0)
                {
                    rewritten.Append(expression.AsSpan(i));
                    break;
                }

                rewritten.Append(expression.AsSpan(i, end - i + 1));
                i = end;
                continue;
            }

            if (TryReadFeatureReference(expression, i, out var fieldName, out var endIndex))
            {
                var mapped = map.MapReference(fieldName);
                if (string.Equals(mapped, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    rewritten.Append(expression.AsSpan(i, endIndex - i));
                }
                else
                {
                    rewritten.Append(FormatFeatureReference(mapped));
                }

                i = endIndex - 1;
                continue;
            }

            rewritten.Append(c);
        }

        return rewritten.ToString();
    }

    private static int ClosingQuote(string text, int open)
    {
        var quote = text[open];
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] == quote)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryReadFeatureReference(string text, int start, out string fieldName, out int end)
    {
        fieldName = string.Empty;
        end = start;
        const string token = "$feature";
        if (start + token.Length > text.Length
            || !text.AsSpan(start, token.Length).Equals(token, StringComparison.Ordinal))
        {
            return false;
        }

        if (start > 0)
        {
            var previous = text[start - 1];
            if (char.IsLetterOrDigit(previous) || previous is '_' or '$')
            {
                return false;
            }
        }

        var i = start + token.Length;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i >= text.Length)
        {
            return false;
        }

        if (text[i] == '.')
        {
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var dotStart = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            {
                i++;
            }

            if (i == dotStart)
            {
                return false;
            }

            fieldName = text[dotStart..i];
            end = i;
            return true;
        }

        if (text[i] != '[')
        {
            return false;
        }

        i++;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i >= text.Length || text[i] is not ('\'' or '"'))
        {
            return false;
        }

        var quote = text[i];
        var nameStart = ++i;
        while (i < text.Length && text[i] != quote)
        {
            if (text[i] == '\\')
            {
                return false;
            }

            i++;
        }

        if (i >= text.Length)
        {
            return false;
        }

        fieldName = text[nameStart..i];
        i++;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i >= text.Length || text[i] != ']')
        {
            return false;
        }

        end = i + 1;
        return true;
    }

    private static string FormatFeatureReference(string column)
    {
        if (column.Length > 0
            && (char.IsLetter(column[0]) || column[0] == '_')
            && column.All(static c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return "$feature." + column;
        }

        return "$feature[\"" + column.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"]";
    }

    private static Dictionary<string, MetadataV2SubtypeFieldOverride> RemapOverrides(
        IReadOnlyDictionary<string, MetadataV2SubtypeFieldOverride> overrides,
        GeoservicesImportColumnMap map)
    {
        var remapped = new Dictionary<string, MetadataV2SubtypeFieldOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in overrides)
        {
            remapped[map.MapReference(pair.Key)] = pair.Value;
        }

        return remapped;
    }

    private static string Disambiguate(string sanitized, HashSet<string> used)
    {
        var first = Fit(sanitized, "_src");
        if (!used.Contains(first))
        {
            return first;
        }

        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = Fit(sanitized, "_src" + suffix.ToString(CultureInfo.InvariantCulture));
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Could not allocate a unique import column name for '" + sanitized + "'.");
    }

    private static string Fit(string stem, string suffix)
    {
        var maxStem = MaxIdentifierLength - suffix.Length;
        if (maxStem < 1)
        {
            maxStem = 1;
        }

        if (stem.Length > maxStem)
        {
            stem = stem[..maxStem];
        }

        return stem + suffix;
    }
}

/// <summary>One importable source field and the column it lands in.</summary>
/// <param name="Field">Source field. Attribute values are still read by its original name.</param>
/// <param name="SanitizedName">Identifier after <c>SanitizeFieldName</c>, before collision handling.</param>
/// <param name="ColumnName">Physical column name.</param>
/// <param name="Renamed">True when <paramref name="ColumnName"/> differs from <paramref name="SanitizedName"/>.</param>
internal sealed record GeoservicesImportColumn(
    GeoservicesFieldInfo Field,
    string SanitizedName,
    string ColumnName,
    bool Renamed);

/// <summary>Column mapping for one ArcGIS layer.</summary>
/// <param name="Columns">Importable attribute columns, in source order.</param>
/// <param name="Spatial">True when the layer has a geometry column named <c>geom</c>.</param>
internal sealed record GeoservicesImportColumnMap(
    IReadOnlyList<GeoservicesImportColumn> Columns,
    bool Spatial)
{
    /// <summary>
    /// Maps a source field or metadata reference onto the physical column name.
    /// An importable field uses its allocated column. A name that sanitizes to <c>objectid</c>
    /// or, on a spatial layer, <c>geom</c> and is not itself an importable field refers to the
    /// reserved column.
    /// </summary>
    internal string MapReference(string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return sourceName ?? string.Empty;
        }

        foreach (var column in Columns)
        {
            if (column.Field.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
            {
                return column.ColumnName;
            }
        }

        var sanitized = sourceName.SanitizeFieldName();
        if (string.Equals(sanitized, "objectid", StringComparison.Ordinal))
        {
            return "objectid";
        }

        if (Spatial && string.Equals(sanitized, "geom", StringComparison.Ordinal))
        {
            return "geom";
        }

        return sanitized;
    }
}
