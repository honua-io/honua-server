// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure.Crs;

/// <summary>
/// Postgres-backed CRS registry using spatial_ref_sys.
/// </summary>
internal sealed partial class PostgresCrsRegistry : ICrsRegistry
{
    private const string Crs84Uri = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private const string EpsgUriPrefix = "http://www.opengis.net/def/crs/EPSG/0/";
    private const string EpsgUrnPrefix = "urn:ogc:def:crs:EPSG::";
    private const string EpsgPrefix = "EPSG:";
    private const string SridPrefix = "SRID=";

    private static readonly CrsDefinition _crs84Definition =
        new(Crs84Uri, 4326, AxisOrder.EastNorth, true);
    private static readonly CrsDefinition _epsg4326Definition =
        new($"{EpsgUriPrefix}4326", 4326, AxisOrder.NorthEast, true);
    private static readonly CrsDefinition _epsg3857Definition =
        new($"{EpsgUriPrefix}3857", 3857, AxisOrder.EastNorth, false);
    private static readonly TimeSpan _cacheRetention = TimeSpan.FromHours(24);
    private const int MaxCacheEntries = 10000;

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresCrsRegistry> _logger;
    private static readonly ConcurrentDictionary<int, CrsCacheEntry> _sridCache = new();
    private static readonly ConcurrentDictionary<string, CrsCacheEntry> _identifierCache = new(StringComparer.OrdinalIgnoreCase);

    public PostgresCrsRegistry(IDatabaseConnectionProvider connectionProvider, ILogger<PostgresCrsRegistry> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            if (!IsCacheExpired(cached, now))
            {
                if (!string.Equals(cached.Definition.Uri, Crs84Uri, StringComparison.OrdinalIgnoreCase))
                {
                    return cached.Definition;
                }
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

    private static bool IsCacheExpired(CrsCacheEntry entry, DateTimeOffset now)
    {
        return now - entry.CreatedAt > _cacheRetention;
    }

    private static void CleanupCacheIfNeeded(DateTimeOffset now)
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

    private sealed record CrsCacheEntry(CrsDefinition Definition, DateTimeOffset CreatedAt);

    private async Task<CrsDefinition?> LoadFromSpatialRefSysAsync(int srid, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = (NpgsqlConnection)await _connectionProvider
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
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
            return new CrsDefinition(uri, srid, axisOrder, isGeographic);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ResolveFailed(_logger, srid, ex);
            return null;
        }
    }

    private static string NormalizeIdentifier(string crsIdentifier)
    {
        var trimmed = crsIdentifier.Trim();
        if (trimmed.Length > 1 && trimmed[0] == '<' && trimmed[^1] == '>')
        {
            trimmed = trimmed[1..^1];
        }

        if (string.Equals(trimmed, Crs84Uri, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "CRS84", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "OGC:CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return Crs84Uri;
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
        else if (trimmed.StartsWith(SridPrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[SridPrefix.Length..];
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
        if (!string.IsNullOrWhiteSpace(wkt))
        {
            // Check for projected CRS keywords first (takes precedence)
            if (wkt.Contains("PROJCS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("PROJCRS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("PROJECTEDCRS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("+proj=", StringComparison.OrdinalIgnoreCase) &&
                !wkt.Contains("+proj=longlat", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Check for geographic CRS keywords (WKT and proj4 formats)
            if (wkt.Contains("GEOGCS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("GEOGCRS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("GEODCRS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("GEODETIC", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("+proj=longlat", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // SRID-based fallback: common geographic CRS ranges
        return srid switch
        {
            4326 => true,
            4269 => true,
            4267 => true,
            >= 4000 and <= 4999 => true,
            _ => false
        };
    }

    private static AxisOrder DetermineAxisOrder(string? wkt, bool isGeographic)
    {
        if (!isGeographic)
        {
            return AxisOrder.EastNorth;
        }

        if (!string.IsNullOrWhiteSpace(wkt) && TryParseAxisOrder(wkt, out var axisOrder))
        {
            return axisOrder;
        }

        return AxisOrder.NorthEast;
    }

    private static bool TryParseAxisOrder(string wkt, out AxisOrder axisOrder)
    {
        axisOrder = default;
        var directions = new List<string>(2);
        var index = 0;

        while (index < wkt.Length && directions.Count < 2)
        {
            var axisIndex = wkt.IndexOf("AXIS", index, StringComparison.OrdinalIgnoreCase);
            if (axisIndex < 0)
            {
                break;
            }

            var bracketIndex = wkt.IndexOf('[', axisIndex);
            if (bracketIndex < 0)
            {
                index = axisIndex + 4;
                continue;
            }

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
                index = axisIndex + 4;
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

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7200,
            Level = LogLevel.Warning,
            Message = "Failed to resolve SRID {Srid} from spatial_ref_sys.")]
        public static partial void ResolveFailed(ILogger logger, int srid, Exception exception);
    }
}
