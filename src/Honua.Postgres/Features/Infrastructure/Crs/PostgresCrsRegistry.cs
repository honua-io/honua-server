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

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresCrsRegistry> _logger;
    private readonly ConcurrentDictionary<int, CrsDefinition> _sridCache = new();
    private readonly ConcurrentDictionary<string, CrsDefinition> _identifierCache = new(StringComparer.OrdinalIgnoreCase);

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

        var normalized = NormalizeIdentifier(crsIdentifier);
        if (_identifierCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        if (string.Equals(normalized, Crs84Uri, StringComparison.OrdinalIgnoreCase))
        {
            _identifierCache.TryAdd(normalized, _crs84Definition);
            return _crs84Definition;
        }

        if (!TryParseEpsg(normalized, out var srid))
        {
            return null;
        }

        var definition = await ResolveBySridAsync(srid, cancellationToken).ConfigureAwait(false);
        if (definition.HasValue)
        {
            _identifierCache.TryAdd(normalized, definition.Value);
        }

        return definition;
    }

    public async ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
    {
        if (srid <= 0)
        {
            return null;
        }

        if (_sridCache.TryGetValue(srid, out var cached))
        {
            return cached;
        }

        var definition = await LoadFromSpatialRefSysAsync(srid, cancellationToken).ConfigureAwait(false);
        if (definition.HasValue)
        {
            _sridCache.TryAdd(srid, definition.Value);
            _identifierCache.TryAdd(definition.Value.Uri, definition.Value);
        }

        return definition;
    }

    public async ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
    {
        return (await ResolveBySridAsync(srid, cancellationToken).ConfigureAwait(false)).HasValue;
    }

    private async Task<CrsDefinition?> LoadFromSpatialRefSysAsync(int srid, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = (NpgsqlConnection)await _connectionProvider
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT srtext FROM spatial_ref_sys WHERE srid = @srid LIMIT 1",
                connection);
            command.Parameters.AddWithValue("@srid", srid);

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is not string srtext)
            {
                return null;
            }

            var isGeographic = DetermineIsGeographic(srid, srtext);
            var axisOrder = isGeographic ? AxisOrder.NorthEast : AxisOrder.EastNorth;
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
            if (wkt.Contains("PROJCS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("PROJCRS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("PROJECTEDCRS", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (wkt.Contains("GEOGCS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("GEOGCRS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("GEODCRS", StringComparison.OrdinalIgnoreCase) ||
                wkt.Contains("GEODETIC", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return srid switch
        {
            4326 => true,
            4269 => true,
            4267 => true,
            >= 4000 and <= 4999 => true,
            _ => false
        };
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
