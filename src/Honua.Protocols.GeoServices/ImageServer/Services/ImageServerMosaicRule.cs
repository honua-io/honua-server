// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// The Esri <c>mosaicMethod</c> that an ImageServer <c>mosaicRule</c> selects, normalized to
/// the subset this service understands. <see cref="Unsupported"/> covers recognized-but-not-yet
/// implemented methods (for example <c>esriMosaicSeamline</c>, <c>esriMosaicNadir</c>, or an
/// <c>esriMosaicByAttribute</c> over a non-acquisition field) which must surface a clean 501
/// when more than one raster would be composited.
/// </summary>
public enum MosaicMethod
{
    /// <summary>No mosaic method was requested (<c>esriMosaicNone</c> or an absent rule).</summary>
    None = 0,

    /// <summary>
    /// <c>esriMosaicByAttribute</c> over an allowlisted NON-date raster attribute — composites by
    /// the named attribute value (ascending or descending). The resolved physical column is
    /// carried on <see cref="ImageServerMosaicRule.AttributeSortColumn"/>.
    /// </summary>
    Attribute = 1,

    /// <summary>
    /// <c>esriMosaicByAttribute</c> over a date field, or <c>esriMosaicAttribute</c> resolved to
    /// an acquisition ordering. Equivalent to <see cref="Attribute"/> for ordering purposes.
    /// </summary>
    ByDate = 2,

    /// <summary>
    /// <c>esriMosaicLockRaster</c> — composites only the caller-pinned raster ids in
    /// <see cref="ImageServerMosaicRule.LockRasterIds"/>.
    /// </summary>
    LockRaster = 3,

    /// <summary>
    /// <c>esriMosaicNorthwest</c> — the upper-left-most raster wins each contested pixel.
    /// </summary>
    Northwest = 4,

    /// <summary>
    /// A recognized Esri mosaic method this service does not yet implement (seamline, nadir,
    /// center, arbitrary non-date attribute sort). Surfaces a 501 when it would affect
    /// multi-raster pixel selection.
    /// </summary>
    Unsupported = 5
}

/// <summary>
/// A parsed ImageServer <c>mosaicRule</c>. Consolidates the JSON validation and the
/// merge-strategy token resolution that the export handler previously performed inline, so
/// the handler can map a single parsed value to a <see cref="RasterMergeStrategy"/> and a
/// <see cref="RasterMosaicOrdering"/>.
/// </summary>
public readonly record struct ImageServerMosaicRule
{
    /// <summary>The normalized mosaic method requested by the rule.</summary>
    public MosaicMethod Method { get; init; }

    /// <summary>
    /// The raster ids pinned by an <c>esriMosaicLockRaster</c> rule, or <c>null</c> when the
    /// method is not <see cref="MosaicMethod.LockRaster"/>.
    /// </summary>
    public long[]? LockRasterIds { get; init; }

    /// <summary>
    /// Whether an attribute/date sort is ascending (oldest first). Defaults to <c>false</c>
    /// (descending, newest first) to match the Esri default acquisition sort.
    /// </summary>
    public bool Ascending { get; init; }

    /// <summary>The <c>sortField</c> declared by an attribute rule, if any.</summary>
    public string? SortField { get; init; }

    /// <summary>The <c>sortValue</c> declared by an attribute rule, if any.</summary>
    public string? SortValue { get; init; }

    /// <summary>
    /// The allowlisted physical raster-catalog column an <c>esriMosaicByAttribute</c> rule sorts
    /// on when <see cref="Method"/> is <see cref="MosaicMethod.Attribute"/> over a non-date
    /// field; <c>null</c> for date-attribute and all other methods. Always a vetted physical
    /// column name (never the caller's raw <see cref="SortField"/>), so it is safe to thread into
    /// the mosaic ORDER BY.
    /// </summary>
    public string? AttributeSortColumn { get; init; }

    /// <summary>
    /// The pixel-resolution merge strategy resolved from the rule's
    /// <c>mergeStrategy</c>/<c>operation</c> token, or <c>null</c> when the rule carries no such
    /// token (the caller then falls back to the resource/default strategy).
    /// </summary>
    public RasterMergeStrategy? Operation { get; init; }

    /// <summary>
    /// Maps the parsed method to the mosaic ordering passed to the raster store. Falls back to
    /// the default newest-first ordering for methods that do not constrain ordering.
    /// </summary>
    public RasterMosaicOrdering ToOrdering() => Method switch
    {
        MosaicMethod.Northwest => RasterMosaicOrdering.Northwest,
        MosaicMethod.LockRaster => RasterMosaicOrdering.LockOrder,
        MosaicMethod.Attribute => RasterMosaicOrdering.Attribute,
        MosaicMethod.ByDate =>
            Ascending ? RasterMosaicOrdering.AcquisitionOldest : RasterMosaicOrdering.AcquisitionNewest,
        _ => RasterMosaicOrdering.AcquisitionNewest
    };

    /// <summary>
    /// Builds the non-date attribute ordering passed to the raster store when this rule is an
    /// <c>esriMosaicByAttribute</c> over an allowlisted non-date column; <c>null</c> otherwise.
    /// </summary>
    public RasterMosaicAttributeSort? ToAttributeSort()
        => Method == MosaicMethod.Attribute && !string.IsNullOrEmpty(AttributeSortColumn)
            ? new RasterMosaicAttributeSort(AttributeSortColumn, Ascending)
            : null;

    /// <summary>
    /// Parses an ImageServer <c>mosaicRule</c> request string.
    /// </summary>
    /// <param name="mosaicRule">The raw <c>mosaicRule</c> query value (Esri JSON or empty).</param>
    /// <param name="rule">The parsed rule when this returns <c>true</c>.</param>
    /// <param name="error">A client-facing error message when this returns <c>false</c>.</param>
    /// <param name="isNotImplemented">
    /// When this returns <c>false</c>, indicates whether the failure is a recognized but
    /// unimplemented method (501) rather than malformed input (400).
    /// </param>
    /// <returns><c>true</c> when the rule parsed (including the empty/no-rule case).</returns>
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
            // A bare token (e.g. "last"/"mean") only carries a merge-strategy hint.
            rule = new ImageServerMosaicRule
            {
                Method = MosaicMethod.None,
                Operation = TryParseOperationToken(trimmed, out var bareStrategy) ? bareStrategy : null
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

            var operation = ResolveOperation(root);
            var ascending = ResolveAscending(root);
            var sortField = ReadString(root, "sortField");
            var sortValue = ReadString(root, "sortValue");

            var methodName = ReadString(root, "mosaicMethod");
            var method = ClassifyMethod(methodName, sortField, out var attributeSortColumn);

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

    private static MosaicMethod ClassifyMethod(string? methodName, string? sortField, out string? attributeSortColumn)
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

        if (methodName.Equals("esriMosaicAttribute", StringComparison.OrdinalIgnoreCase) ||
            methodName.Equals("esriMosaicByAttribute", StringComparison.OrdinalIgnoreCase))
        {
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

        // esriMosaicSeamline, esriMosaicNadir, esriMosaicCenter, esriMosaicViewpoint, etc.
        return MosaicMethod.Unsupported;
    }

    // Maps an Esri esriMosaicByAttribute sortField onto a strictly allowlisted physical raster
    // catalog column. Only stable, numeric/sortable columns the catalog actually carries are
    // permitted; everything else (including sensor/nadir-style fields that are not modeled) is
    // rejected so an arbitrary, injectable, or meaningless sort never reaches the SQL ORDER BY.
    // Accepts the Esri canonical catalog field names and their physical aliases. Sensor- and
    // orientation-derived fields (e.g. nadir/off-nadir) are intentionally absent: esriMosaicNadir
    // and sensor-attribute sorts stay deferred until that metadata is modeled.
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

    private static RasterMergeStrategy? ResolveOperation(JsonElement root)
    {
        if (TryReadStringInsensitive(root, "mergeStrategy", out var mergeStrategy) &&
            TryParseOperationToken(mergeStrategy, out var fromMerge))
        {
            return fromMerge;
        }

        if (TryReadStringInsensitive(root, "operation", out var operation) &&
            TryParseOperationToken(operation, out var fromOperation))
        {
            return fromOperation;
        }

        return null;
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
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString();
                return !string.IsNullOrEmpty(value);
            }
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
            case "last":
            case "mt_last":
                strategy = RasterMergeStrategy.Newest;
                return true;
            case "oldest":
            case "first":
            case "mt_first":
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
