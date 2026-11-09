// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

// A recognized mosaic method this service cannot composite yet must return 501 when more than one raster would be drawn, rather than a silent default.
public enum MosaicMethod
{
    None = 0,

    Attribute = 1,

    ByDate = 2,

    LockRaster = 3,

    Northwest = 4,

    Seamline = 5,

    Nadir = 7,

    Unsupported = 6
}

public readonly record struct ImageServerMosaicRule
{
    public const string AllowedMosaicMethods = "None,NorthWest,LockRaster,ByAttribute,Nadir,Seamline";

    public const string DefaultMosaicMethod = "ByAttribute";

    public static string ToMosaicMethodWireValue(string shortName) =>
        string.Equals(shortName, DefaultMosaicMethod, StringComparison.OrdinalIgnoreCase)
            ? "esriMosaicAttribute"
            : "esriMosaic" + shortName;

    public const string DefaultSortField = "AcquisitionDate";

    public MosaicMethod Method { get; init; }

    public long[]? LockRasterIds { get; init; }

    public bool Ascending { get; init; }

    public string? SortField { get; init; }

    public string? SortValue { get; init; }

    // An attribute mosaic sorts on an allowlisted physical column, never the caller's raw field text, so the name is safe to place in ORDER BY.
    public string? AttributeSortColumn { get; init; }

    public RasterMergeStrategy? Operation { get; init; }

    public RasterMosaicOrdering ToOrdering() => Method switch
    {
        MosaicMethod.Northwest => RasterMosaicOrdering.Northwest,
        MosaicMethod.LockRaster => RasterMosaicOrdering.LockOrder,
        MosaicMethod.Seamline => RasterMosaicOrdering.Seamline,
        MosaicMethod.Nadir => RasterMosaicOrdering.Nadir,
        MosaicMethod.Attribute => RasterMosaicOrdering.Attribute,
        MosaicMethod.ByDate =>
            Ascending ? RasterMosaicOrdering.AcquisitionOldest : RasterMosaicOrdering.AcquisitionNewest,
        _ => RasterMosaicOrdering.AcquisitionNewest
    };

    public RasterMosaicAttributeSort? ToAttributeSort()
        => Method == MosaicMethod.Attribute && !string.IsNullOrEmpty(AttributeSortColumn)
            ? new RasterMosaicAttributeSort(AttributeSortColumn, Ascending)
            : null;

    public RasterInfo[] RestrictToLockedRasters(IReadOnlyList<RasterInfo> rasters)
    {
        ArgumentNullException.ThrowIfNull(rasters);
        if (Method != MosaicMethod.LockRaster)
        {
            return rasters as RasterInfo[] ?? rasters.ToArray();
        }

        var lockedIds = LockRasterIds ?? [];
        var byId = new Dictionary<long, RasterInfo>(rasters.Count);
        foreach (var raster in rasters)
        {
            byId.TryAdd(raster.Id, raster);
        }

        return lockedIds
            .Where(byId.ContainsKey)
            .Select(id =>
            {
                byId.Remove(id, out var raster);
                return raster;
            })
            .ToArray();
    }

    public static bool TryParse(
        string? mosaicRule,
        out ImageServerMosaicRule rule,
        out string error,
        out bool isNotImplemented)
    {
        rule = default;
        error = string.Empty;
        isNotImplemented = false;

        if (string.IsNullOrWhiteSpace(mosaicRule))
        {
            rule = new ImageServerMosaicRule { Method = MosaicMethod.None };
            return true;
        }

        var trimmed = mosaicRule.Trim();
        if (!trimmed.StartsWith('{'))
        {
            // A bare token (e.g. "first"/"mean") only carries a merge-strategy hint.
            if (!TryParseOperationToken(trimmed, out var bareStrategy))
            {
                error = $"mosaicRule operation '{trimmed}' is not implemented.";
                isNotImplemented = true;
                return false;
            }

            rule = new ImageServerMosaicRule
            {
                Method = MosaicMethod.None,
                Operation = bareStrategy
            };
            return true;
        }

        JsonElement root;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            error = "mosaicRule contains invalid JSON.";
            return false;
        }

        using (document)
        {
            root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                rule = new ImageServerMosaicRule { Method = MosaicMethod.None };
                return true;
            }

            if (TryRejectUnimplementedConstraints(root, out error))
            {
                isNotImplemented = true;
                return false;
            }

            if (!TryResolveOperation(root, out var operation, out error))
            {
                isNotImplemented = true;
                return false;
            }
            var ascending = ResolveAscending(root);
            var sortField = ReadString(root, "sortField");
            var sortValue = ReadString(root, "sortValue");

            var methodName = ReadString(root, "mosaicMethod");
            var method = ClassifyMethod(methodName, sortField, sortValue, out var attributeSortColumn);

            if (method == MosaicMethod.LockRaster)
            {
                if (!TryReadLockRasterIds(root, out var lockIds))
                {
                    error = "mosaicRule esriMosaicLockRaster requires a numeric lockRasterIds array.";
                    return false;
                }

                rule = new ImageServerMosaicRule
                {
                    Method = MosaicMethod.LockRaster,
                    LockRasterIds = lockIds,
                    Ascending = ascending,
                    SortField = sortField,
                    SortValue = sortValue,
                    Operation = operation
                };
                return true;
            }

            rule = new ImageServerMosaicRule
            {
                Method = method,
                Ascending = ascending,
                SortField = sortField,
                SortValue = sortValue,
                AttributeSortColumn = attributeSortColumn,
                Operation = operation
            };
            return true;
        }
    }

    private static MosaicMethod ClassifyMethod(string? methodName, string? sortField, string? sortValue, out string? attributeSortColumn)
    {
        attributeSortColumn = null;

        if (string.IsNullOrWhiteSpace(methodName))
        {
            return MosaicMethod.None;
        }

        if (methodName.Equals("esriMosaicNone", StringComparison.OrdinalIgnoreCase))
        {
            return MosaicMethod.None;
        }

        if (methodName.Equals("esriMosaicLockRaster", StringComparison.OrdinalIgnoreCase))
        {
            return MosaicMethod.LockRaster;
        }

        if (methodName.Equals("esriMosaicNorthwest", StringComparison.OrdinalIgnoreCase))
        {
            return MosaicMethod.Northwest;
        }

        if (methodName.Equals("esriMosaicSeamline", StringComparison.OrdinalIgnoreCase))
        {
            return MosaicMethod.Seamline;
        }

        // esriMosaicNadir is now executable (#1870): the export path ranks rasters by the off-nadir
        // angle persisted in the sensor/orientation metadata (#1907). Rasters with no recorded
        // off-nadir angle rank last; with a single raster the method is a no-op accepted upstream.
        if (methodName.Equals("esriMosaicNadir", StringComparison.OrdinalIgnoreCase))
        {
            return MosaicMethod.Nadir;
        }

        if (methodName.Equals("esriMosaicAttribute", StringComparison.OrdinalIgnoreCase))
        {
            // Esri ByAttribute with a base sortValue ranks rasters by their distance from that
            // value. The raster store only orders by the attribute itself, so a base value is a
            // recognized-but-unsupported form (#4063); a direction token ("asc"/"desc") is not.
            if (!string.IsNullOrWhiteSpace(sortValue) && !IsSortDirectionToken(sortValue))
            {
                return MosaicMethod.Unsupported;
            }

            // A date/acquisition attribute sort uses the temporal ordering path.
            if (IsDateSortField(sortField))
            {
                return MosaicMethod.ByDate;
            }

            // A non-date attribute sort over an allowlisted catalog column is now executable
            // (#1870). Any other field remains a recognized-but-unsupported attribute mosaic.
            if (TryResolveAttributeSortColumn(sortField, out attributeSortColumn))
            {
                return MosaicMethod.Attribute;
            }

            return MosaicMethod.Unsupported;
        }

        // esriMosaicCenter, esriMosaicViewpoint, etc. remain unimplemented.
        return MosaicMethod.Unsupported;
    }

    // Maps an Esri esriMosaicAttribute sortField onto a strictly allowlisted physical raster
    // catalog column. Only stable, numeric/sortable columns the catalog actually carries are
    // permitted; everything else (including sensor/nadir-style fields that are not modeled) is
    // rejected so an arbitrary, injectable, or meaningless sort never reaches the SQL ORDER BY.
    // Accepts the Esri canonical catalog field names and their physical aliases. Sensor- and
    // orientation-derived fields (e.g. off-nadir) are intentionally absent here: the dedicated
    // esriMosaicNadir method (MosaicMethod.Nadir) ranks by the off-nadir angle from sensor
    // metadata; an esriMosaicAttribute sort over a raw sensor field stays unsupported.
    private static bool TryResolveAttributeSortColumn(string? sortField, out string? column)
    {
        column = null;
        if (string.IsNullOrWhiteSpace(sortField))
        {
            return false;
        }

        switch (sortField.Trim().ToLowerInvariant())
        {
            case "objectid":
            case "oid":
            case "id":
                column = "id";
                return true;
            case "bandcount":
            case "band_count":
            case "num_bands":
            case "numbands":
                column = "band_count";
                return true;
            case "width":
            case "columns":
                column = "width";
                return true;
            case "height":
            case "rows":
                column = "height";
                return true;
            case "srid":
            case "wkid":
                column = "srid";
                return true;
            default:
                return false;
        }
    }

    // An attribute mosaic is treated as a date ordering when it sorts on a recognized
    // acquisition/date field, or when no explicit sort field is supplied (the implicit Esri
    // default is the acquisition date). Anything else is an unsupported arbitrary attribute.
    private static bool IsDateSortField(string? sortField)
    {
        if (string.IsNullOrWhiteSpace(sortField))
        {
            return true;
        }

        return sortField.Contains("acquisition", StringComparison.OrdinalIgnoreCase)
            || sortField.Contains("date", StringComparison.OrdinalIgnoreCase)
            || sortField.Equals("created_at", StringComparison.OrdinalIgnoreCase)
            || sortField.Equals("createddate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSortDirectionToken(string sortValue)
        => sortValue.Trim().ToLowerInvariant() is "asc" or "ascending" or "desc" or "descending";

    // Esri sortValue/ascending semantics: an explicit "ascending": true wins. Otherwise a
    // sortValue of "asc"/"ascending" requests an ascending (oldest-first) ordering. The
    // default is descending (newest-first), matching the Esri acquisition default.
    private static bool ResolveAscending(JsonElement root)
    {
        if (root.TryGetProperty("ascending", out var ascendingProperty))
        {
            if (ascendingProperty.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (ascendingProperty.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        var sortValue = ReadString(root, "sortValue");
        if (!string.IsNullOrWhiteSpace(sortValue))
        {
            var normalized = sortValue.Trim().ToLowerInvariant();
            if (normalized is "asc" or "ascending")
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryRejectUnimplementedConstraints(JsonElement root, out string error)
    {
        error = string.Empty;
        if (TryReadStringInsensitive(root, "where", out var where) && !IsTrivialMosaicWhere(where))
        {
            error = "mosaicRule where is not implemented.";
            return true;
        }

        if (HasNonEmptyFids(root))
        {
            error = "mosaicRule fids is not implemented.";
            return true;
        }

        return false;
    }

    private static bool IsTrivialMosaicWhere(string? where)
    {
        if (string.IsNullOrWhiteSpace(where))
        {
            return true;
        }

        var compact = where.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact is "1=1" or "(1=1)";
    }

    private static bool HasNonEmptyFids(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("fids", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Array &&
                property.Value.GetArrayLength() > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveOperation(JsonElement root, out RasterMergeStrategy? operation, out string error)
    {
        operation = null;
        error = string.Empty;
        string? token = null;
        if (TryReadStringInsensitive(root, "mergeStrategy", out var mergeStrategy))
        {
            token = mergeStrategy;
        }
        else if (TryReadStringInsensitive(root, "operation", out var operationToken))
        {
            token = operationToken;
        }
        else if (TryReadStringInsensitive(root, "mosaicOperation", out var mosaicOperation))
        {
            token = mosaicOperation;
        }
        else
        {
            return true;
        }

        if (!TryParseOperationToken(token, out var strategy))
        {
            error = $"mosaicRule operation '{token}' is not implemented.";
            return false;
        }

        operation = strategy;
        return true;
    }

    private static bool TryReadLockRasterIds(JsonElement root, out long[]? lockIds)
    {
        lockIds = null;
        if (!root.TryGetProperty("lockRasterIds", out var idsProperty) ||
            idsProperty.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<long>();
        foreach (var element in idsProperty.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var numeric))
            {
                parsed.Add(numeric);
            }
            else if (element.ValueKind == JsonValueKind.String &&
                     long.TryParse(element.GetString(), out var fromString))
            {
                parsed.Add(fromString);
            }
            else
            {
                return false;
            }
        }

        lockIds = parsed.ToArray();
        return true;
    }

    private static string? ReadString(JsonElement root, string propertyName)
        => TryReadStringInsensitive(root, propertyName, out var value) ? value : null;

    private static bool TryReadStringInsensitive(JsonElement element, string propertyName, out string? value)
    {
        // Not rewritten as .Where(...): this is a first-match short-circuit that returns
        // from within the loop as soon as a matching property is found, not a pure filter —
        // a LINQ equivalent would still need to iterate to find the first match and would
        // be harder to read than the loop.
        foreach (var property in (element.EnumerateObject()).Where(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String))
        {
            value = property.Value.GetString();
            return !string.IsNullOrEmpty(value);
        }

        value = null;
        return false;
    }

    // Mirrors the merge-strategy token vocabulary the resource resolver accepts so request
    // overrides parse identically whether they arrive as a bare token or a JSON property.
    private static bool TryParseOperationToken(string? value, out RasterMergeStrategy strategy)
    {
        strategy = RasterMergeStrategy.Newest;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "newest":
            case "latest":
            case "first":
            case "mt_first":
                // Esri mosaicOperator "First" / MT_FIRST is the first raster in the
                // mosaic order. The store's Newest aggregate keeps the last sorted
                // raster, which is the same winner the service advertises as First.
                strategy = RasterMergeStrategy.Newest;
                return true;
            case "oldest":
            case "last":
            case "mt_last":
                strategy = RasterMergeStrategy.Oldest;
                return true;
            case "average":
            case "avg":
            case "mean":
            case "mt_mean":
                strategy = RasterMergeStrategy.Average;
                return true;
            case "max":
            case "maximum":
            case "mt_max":
                strategy = RasterMergeStrategy.Max;
                return true;
            case "min":
            case "minimum":
            case "mt_min":
                strategy = RasterMergeStrategy.Min;
                return true;
            default:
                return false;
        }
    }
}
