// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure.Crs;

internal sealed class PostgresCrsRegistryCache
{
    internal ConcurrentDictionary<int, PostgresCrsRegistry.CrsCacheEntry> SridCache { get; } = new();
    internal ConcurrentDictionary<string, PostgresCrsRegistry.CrsCacheEntry> IdentifierCache { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed partial class PostgresCrsRegistry : ICrsRegistry
{
    private const string Crs84Uri = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private const string EpsgUriPrefix = "http://www.opengis.net/def/crs/EPSG/0/";
    private const string EpsgUrnPrefix = "urn:ogc:def:crs:EPSG::";
    private const string EpsgPrefix = "EPSG:";
    private const string SridPrefix = "SRID=";

    // The built-in definitions answer 4326/3857 without touching spatial_ref_sys, so they must
    // carry the same WKT the catalog would have returned; without it every WKT consumer
    // (shapefile .prj, GeoPackage gpkg_spatial_ref_sys) got null for the two commonest SRIDs.
    private static readonly CrsDefinition _crs84Definition =
        new(Crs84Uri, 4326, AxisOrder.EastNorth, true)
        {
            Wkt = WellKnownCrsWkt.Epsg4326,
            LinearUnitFactor = CrsLinearUnitFactor.Resolve(null, WellKnownCrsWkt.Epsg4326, isGeographic: true)
        };
    private static readonly CrsDefinition _epsg4326Definition =
        new($"{EpsgUriPrefix}4326", 4326, AxisOrder.NorthEast, true)
        {
            Wkt = WellKnownCrsWkt.Epsg4326,
            LinearUnitFactor = CrsLinearUnitFactor.Resolve(null, WellKnownCrsWkt.Epsg4326, isGeographic: true)
        };
    private static readonly CrsDefinition _epsg3857Definition =
        new($"{EpsgUriPrefix}3857", 3857, AxisOrder.EastNorth, false)
        {
            Wkt = WellKnownCrsWkt.Epsg3857,
            LinearUnitFactor = CrsLinearUnitFactor.Resolve(null, WellKnownCrsWkt.Epsg3857, isGeographic: false)
        };
    private static readonly TimeSpan _cacheRetention = TimeSpan.FromHours(24);
    private const int MaxCacheEntries = 10000;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresCrsRegistry> _logger;
    private readonly ConcurrentDictionary<int, CrsCacheEntry> _sridCache;
    private readonly ConcurrentDictionary<string, CrsCacheEntry> _identifierCache;

    public PostgresCrsRegistry(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresCrsRegistry> logger,
        PostgresCrsRegistryCache? cache = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var effectiveCache = cache ?? new PostgresCrsRegistryCache();
        _sridCache = effectiveCache.SridCache;
        _identifierCache = effectiveCache.IdentifierCache;
    }

    public async ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(crsIdentifier))
        {
            return _crs84Definition;
        }

        var now = DateTimeOffset.UtcNow;
        var normalized = NormalizeIdentifier(crsIdentifier);
        if (_identifierCache.TryGetValue(normalized, out var cached))
        {
            if (!IsCacheExpired(cached, now))
            {
                return cached.Definition;
            }

            _identifierCache.TryRemove(normalized, out _);
        }

        if (string.Equals(normalized, Crs84Uri, StringComparison.OrdinalIgnoreCase))
        {
            var entry = new CrsCacheEntry(_crs84Definition, now);
            _identifierCache.TryAdd(normalized, entry);
            CleanupCacheIfNeeded(now);
            return _crs84Definition;
        }

        if (!TryParseEpsg(normalized, out var srid))
        {
            return null;
        }

        var definition = await ResolveBySridAsync(srid, cancellationToken).ConfigureAwait(false);
        if (definition.HasValue)
        {
            _identifierCache.TryAdd(normalized, new CrsCacheEntry(definition.Value, now));
            CleanupCacheIfNeeded(now);
        }

        return definition;
    }

    public async ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
    {
        if (srid <= 0)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_sridCache.TryGetValue(srid, out var cached))
        {
            if (!IsCacheExpired(cached, now) &&
                !string.Equals(cached.Definition.Uri, Crs84Uri, StringComparison.OrdinalIgnoreCase))
            {
                return cached.Definition;
            }

            _sridCache.TryRemove(srid, out _);
        }

        if (TryGetBuiltInDefinition(srid, out var builtInDefinition))
        {
            var entry = new CrsCacheEntry(builtInDefinition, now);
            _sridCache.TryAdd(srid, entry);
            _identifierCache.TryAdd(builtInDefinition.Uri, entry);
            CleanupCacheIfNeeded(now);
            return builtInDefinition;
        }

        var definition = await LoadFromSpatialRefSysAsync(srid, cancellationToken).ConfigureAwait(false);
        if (definition.HasValue)
        {
            var entry = new CrsCacheEntry(definition.Value, now);
            _sridCache.TryAdd(srid, entry);
            _identifierCache.TryAdd(definition.Value.Uri, entry);
            CleanupCacheIfNeeded(now);
        }

        return definition;
    }

    public async ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
    {
        return (await ResolveBySridAsync(srid, cancellationToken).ConfigureAwait(false)).HasValue;
    }

    public async ValueTask<CoordinateSystemSearchResult> SearchAsync(
        string query, int limit, CancellationToken cancellationToken = default)
    {
        var search = CoordinateSystemSearchQuery.Parse(query, limit);
        var exactCode = search.ExactCode;
        query = search.Text;
        const string sql = """
            SELECT srid, srtext
            FROM public.spatial_ref_sys
            WHERE upper(auth_name) = 'EPSG' AND auth_srid = srid AND srid > 0 AND srtext IS NOT NULL
              AND (srid = @code OR strpos(lower(split_part(srtext, '"', 2)), lower(@query)) > 0)
            ORDER BY (srid = @code) DESC, split_part(srtext, '"', 2) COLLATE "C", srid
            LIMIT @limit
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("code", exactCode);
        command.Parameters.AddWithValue("query", query);
        command.Parameters.AddWithValue("limit", limit + 1);
        var items = new List<CoordinateSystemEntry>(limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (CoordinateSystemEntry.FromWkt(reader.GetInt32(0), reader.GetString(1)) is { } entry) items.Add(entry);
        }
        return new CoordinateSystemSearchResult { Items = items.Take(limit).ToArray(), HasMore = items.Count > limit };
    }

    private static bool IsCacheExpired(CrsCacheEntry entry, DateTimeOffset now)
    {
        return now - entry.CreatedAt > _cacheRetention;
    }

    private void CleanupCacheIfNeeded(DateTimeOffset now)
    {
        RemoveExpiredEntries(_sridCache, now);
        RemoveExpiredEntries(_identifierCache, now);
        TrimCache(_sridCache, MaxCacheEntries);
        TrimCache(_identifierCache, MaxCacheEntries);
    }

    private static void RemoveExpiredEntries<TKey>(ConcurrentDictionary<TKey, CrsCacheEntry> cache, DateTimeOffset now)
        where TKey : notnull
    {
        foreach (var (key, entry) in cache)
        {
            if (IsCacheExpired(entry, now))
            {
                cache.TryRemove(key, out _);
            }
        }
    }

    private static void TrimCache<TKey>(ConcurrentDictionary<TKey, CrsCacheEntry> cache, int maxEntries)
        where TKey : notnull
    {
        var overflow = cache.Count - maxEntries;
        if (overflow <= 0)
        {
            return;
        }

        var entriesToRemove = cache
            .OrderBy(kvp => kvp.Value.CreatedAt)
            .Take(overflow)
            .Select(kvp => kvp.Key)
            .ToArray();
        foreach (var key in entriesToRemove)
        {
            cache.TryRemove(key, out _);
        }
    }

    internal sealed record CrsCacheEntry(CrsDefinition Definition, DateTimeOffset CreatedAt);

    private async Task<CrsDefinition?> LoadFromSpatialRefSysAsync(int srid, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT srtext, proj4text FROM public.spatial_ref_sys WHERE srid = @srid LIMIT 1",
                connection);
            command.Parameters.AddWithValue("@srid", srid);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var srtext = reader["srtext"] as string;
            var proj4text = reader["proj4text"] as string;
            var wkt = srtext ?? proj4text;

            var isGeographic = DetermineIsGeographic(srid, wkt);
            var axisOrder = DetermineAxisOrder(wkt, isGeographic);
            var uri = FormattableString.Invariant($"{EpsgUriPrefix}{srid}");
            return new CrsDefinition(uri, srid, axisOrder, isGeographic)
            {
                Wkt = srtext,
                LinearUnitFactor = CrsLinearUnitFactor.Resolve(proj4text, srtext, isGeographic)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsTransientConnectionError(ex))
        {
            Log.ResolveFailed(_logger, srid, ex);
            return null;
        }
    }

    internal static string NormalizeIdentifier(string crsIdentifier)
    {
        var trimmed = CrsIdentifier.Unwrap(crsIdentifier);

        if (CrsIdentifier.IsCrs84(trimmed))
        {
            return Crs84Uri;
        }

        if (trimmed.StartsWith(SridPrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[SridPrefix.Length..];
        }

        if (CrsIdentifier.TryParseEpsgCode(trimmed, out var parsedSrid))
        {
            return FormattableString.Invariant($"{EpsgUriPrefix}{parsedSrid}");
        }

        if (trimmed.StartsWith(EpsgUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.StartsWith(EpsgUrnPrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[EpsgUrnPrefix.Length..];
        }
        else if (trimmed.StartsWith(EpsgPrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[EpsgPrefix.Length..];
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            ? FormattableString.Invariant($"{EpsgUriPrefix}{srid}")
            : trimmed;
    }

    private static bool TryParseEpsg(string normalizedIdentifier, out int srid)
    {
        srid = 0;

        if (normalizedIdentifier.StartsWith(EpsgUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var code = normalizedIdentifier[EpsgUriPrefix.Length..];
            return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
        }

        return int.TryParse(normalizedIdentifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
    }

    private static bool DetermineIsGeographic(int srid, string? wkt)
    {
        return SpatialReference.Create(srid, null, null, null, wkt).IsGeographic;
    }

    internal static AxisOrder DetermineAxisOrder(string? wkt, bool isGeographic)
    {
        // Consult the WKT AXIS clauses for both geographic and projected CRS:
        // some projected systems (EPSG:3035, 2193, 2180, 31466 …) are officially
        // northing,easting and spatial_ref_sys.srtext carries the AXIS elements.
        if (!string.IsNullOrWhiteSpace(wkt) && TryParseAxisOrder(wkt, out var axisOrder))
        {
            return axisOrder;
        }

        // Fall back to conventional defaults when no AXIS clauses are present.
        // Geographic CRS without AXIS → assume north-east (OGC convention for urn: authorities).
        // Projected CRS without AXIS → assume east-north (the common case).
        return isGeographic ? AxisOrder.NorthEast : AxisOrder.EastNorth;
    }

    internal static bool TryParseAxisOrder(string wkt, out AxisOrder axisOrder)
    {
        axisOrder = default;
        var directions = new List<string>(2);
        var index = FindAxisSearchStartIndex(wkt);

        while (index < wkt.Length && directions.Count < 2)
        {
            var axisIndex = wkt.IndexOf("AXIS[", index, StringComparison.OrdinalIgnoreCase);
            if (axisIndex < 0)
            {
                break;
            }

            if (axisIndex > 0 && (char.IsLetterOrDigit(wkt[axisIndex - 1]) || wkt[axisIndex - 1] == '_'))
            {
                index = axisIndex + 5;
                continue;
            }

            var bracketIndex = axisIndex + 4;
            var position = bracketIndex + 1;
            var inQuotes = false;

            while (position < wkt.Length)
            {
                var c = wkt[position];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && c == ',')
                {
                    position++;
                    break;
                }

                position++;
            }

            if (position >= wkt.Length)
            {
                index = axisIndex + 5;
                continue;
            }

            while (position < wkt.Length && char.IsWhiteSpace(wkt[position]))
            {
                position++;
            }

            var start = position;
            while (position < wkt.Length)
            {
                var c = wkt[position];
                if (c == ',' || c == ']' || char.IsWhiteSpace(c))
                {
                    break;
                }
                position++;
            }

            if (position > start)
            {
                directions.Add(wkt[start..position]);
            }

            index = position;
        }

        if (directions.Count < 2)
        {
            return false;
        }

        var first = directions[0];
        var second = directions[1];
        if (IsEastWest(first) && IsNorthSouth(second))
        {
            axisOrder = AxisOrder.EastNorth;
            return true;
        }

        if (IsNorthSouth(first) && IsEastWest(second))
        {
            axisOrder = AxisOrder.NorthEast;
            return true;
        }

        return false;
    }

    private static readonly string[] _projectedCrsKeywords = ["PROJCRS[", "PROJECTEDCRS[", "PROJCS["];
    private static readonly string[] _baseGeographicCrsKeywords = ["BASEGEOGCRS", "BASEGEODCRS", "GEOGCS", "GEODCRS", "GEOGRAPHICCRS", "GEOGCRS"];

    private static int FindAxisSearchStartIndex(string wkt)
    {
        var isProjected = false;
        foreach (var keyword in _projectedCrsKeywords)
        {
            if (wkt.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                isProjected = true;
                break;
            }
        }

        if (!isProjected)
        {
            return 0;
        }

        var maxBaseEnd = -1;
        foreach (var keyword in _baseGeographicCrsKeywords)
        {
            if (TryFindBlockEnd(wkt, keyword, out var blockEnd) && blockEnd > maxBaseEnd)
            {
                maxBaseEnd = blockEnd;
            }
        }

        return maxBaseEnd >= 0 ? maxBaseEnd + 1 : 0;
    }

    private static bool TryFindBlockEnd(string wkt, string keyword, out int end)
    {
        end = -1;
        var idx = wkt.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var isWordStart = idx == 0 || (!char.IsLetterOrDigit(wkt[idx - 1]) && wkt[idx - 1] != '_');
            var i = idx + keyword.Length;
            while (i < wkt.Length && char.IsWhiteSpace(wkt[i]))
            {
                i++;
            }

            if (isWordStart && i < wkt.Length && wkt[i] == '[')
            {
                var close = FindMatchingBracket(wkt, i);
                if (close > i)
                {
                    end = close;
                    return true;
                }
            }

            idx = wkt.IndexOf(keyword, idx + keyword.Length, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static int FindMatchingBracket(string s, int openIndex)
    {
        var depth = 0;
        var inQuote = false;
        for (var i = openIndex; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (inQuote)
            {
                continue;
            }

            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool IsEastWest(string direction)
        => direction.Equals("east", StringComparison.OrdinalIgnoreCase) ||
           direction.Equals("west", StringComparison.OrdinalIgnoreCase) ||
           direction.Equals("easting", StringComparison.OrdinalIgnoreCase) ||
           direction.Equals("westing", StringComparison.OrdinalIgnoreCase);

    private static bool IsNorthSouth(string direction)
        => direction.Equals("north", StringComparison.OrdinalIgnoreCase) ||
           direction.Equals("south", StringComparison.OrdinalIgnoreCase) ||
           direction.Equals("northing", StringComparison.OrdinalIgnoreCase) ||
           direction.Equals("southing", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetBuiltInDefinition(int srid, out CrsDefinition definition)
    {
        switch (srid)
        {
            case 4326:
                definition = _epsg4326Definition;
                return true;
            case 3857:
                definition = _epsg3857Definition;
                return true;
            default:
                definition = default;
                return false;
        }
    }

    private static bool IsTransientConnectionError(Exception ex)
    {
        // IOException / SocketException covers physical-connection loss.
        if (ex is System.IO.IOException)
        {
            return true;
        }

        // NpgsqlException with a connection-class SQL state is a server-side connection error.
        if (ex is NpgsqlException npgsql)
        {
            return npgsql.SqlState is "57P03"  // cannot_connect_now
                or "08000"  // connection_exception
                or "08003"  // connection_does_not_exist
                or "08006"; // connection_failure
        }

        return false;
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7200,
            Level = LogLevel.Warning,
            Message = "Failed to resolve SRID {Srid} from spatial_ref_sys.")]
        public static partial void ResolveFailed(ILogger logger, int srid, Exception exception);
    }
}
