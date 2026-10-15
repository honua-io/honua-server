// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
            .Select(rule => string.IsNullOrWhiteSpace(rule.FieldName)
                ? rule
                : rule with { FieldName = map.MapReference(rule.FieldName) })
            .ToArray();
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
