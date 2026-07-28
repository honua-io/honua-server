// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Npgsql;

namespace Honua.Geocoding.Features.Geocoding.Providers;

/// <summary>
/// Self-hosted geocoder that runs entirely offline against a locally-loaded PostGIS reference
/// dataset, making no external service calls. It supports forward geocode, reverse geocode, and
/// suggest against a documented reference table (see
/// <c>docs/reference/geocoding/local-postgis-geocoder.md</c>) and plugs into the shared provider
/// abstraction so GeocodeServer can serve it like any hosted provider (#2151). This is also the
/// substrate that the Esri <c>.loc/.lox</c> locator import (#2152) will target.
/// </summary>
internal sealed partial class LocalPostgisGeocodeProvider : BaseGeocodeProvider, IDisposable
{
    /// <summary>
    /// Local PostGIS geocoder provider name constant.
    /// </summary>
    public const string ProviderName = GeocodeProviderNames.Local;

    private readonly NpgsqlDataSource _dataSource;
    private readonly LocalGeocoderProviderConfiguration _configuration;
    private readonly bool _ownsDataSource;
    private readonly string _qualifiedTable;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalPostgisGeocodeProvider"/> class.
    /// </summary>
    /// <param name="dataSource">PostGIS data source for the reference dataset.</param>
    /// <param name="configuration">Provider configuration.</param>
    /// <param name="ownsDataSource">Whether this provider should dispose <paramref name="dataSource"/>.</param>
    public LocalPostgisGeocodeProvider(
        NpgsqlDataSource dataSource,
        LocalGeocoderProviderConfiguration configuration,
        bool ownsDataSource = false)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _ownsDataSource = ownsDataSource;
        _qualifiedTable = $"{QuoteIdentifier(configuration.Schema)}.{QuoteIdentifier(configuration.Table)}";
    }

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
    public override GeocodeProviderCapabilities Capabilities => new(
        SupportsForwardGeocode: true,
        SupportsReverseGeocode: true,
        SupportsSuggest: true,
        SupportsBatch: true, // Fanned out via the shared base batch implementation against local data.
        SupportsStructuredInput: true,
        SupportsBiasing: false)
    {
        SupportedSpatialReferences = [4326],
        SupportedStructuredFields = GeocodeStructuredFields.All(),
        MaxResultsPerRequest = _configuration.MaxCandidates,
        MaxBatchSize = _configuration.MaxBatchSize,
        RequiresAuthentication = false,
        DefaultTimeoutSeconds = _configuration.TimeoutSeconds
    };

    /// <inheritdoc />
    public override async Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ValidateSpatialReference(request.SpatialReferenceWkid))
        {
            throw new GeocodeRequestException($"Unsupported spatial reference: {request.SpatialReferenceWkid}")
            {
                ParameterName = nameof(request.SpatialReferenceWkid)
            };
        }

        // Structured input contributes discrete components; otherwise the single-line query is used.
        var searchText = ComposeSearchText(request);
        var tokens = Tokenize(searchText);
        if (tokens.Count == 0)
        {
            return [];
        }

        var limit = ValidateMaxResults(request.MaxResults);

        // Each token must be contained in the normalized search text (AND semantics). Shorter
        // records rank first as the more specific match; final score is computed from the match
        // shape (exact / prefix / contains) in code for determinism.
        var conditions = new List<string>(tokens.Count);
        await using var command = _dataSource.CreateCommand();
        for (var i = 0; i < tokens.Count; i++)
        {
            conditions.Add($"search_text LIKE @t{i}");
            command.Parameters.AddWithValue($"t{i}", "%" + EscapeLike(tokens[i]) + "%");
        }

        var whereClause = string.Join(" AND ", conditions);
        command.CommandText = $"""
            SELECT id, display_name, address_number, street_name, city, region, postal_code,
                   country, neighborhood, address_type, ST_X(geom) AS x, ST_Y(geom) AS y, search_text
            FROM {_qualifiedTable}
            WHERE {whereClause}
            ORDER BY length(search_text) ASC, display_name ASC
            LIMIT {limit}
            """;

        var normalized = string.Join(' ', tokens);
        var candidates = new List<GeocodeCandidate>(limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var searchValue = reader.GetString(reader.GetOrdinal("search_text"));
            var score = ScoreMatch(normalized, searchValue);
            candidates.Add(MapCandidate(reader, score, request.SpatialReferenceWkid));
        }

        return candidates;
    }

    /// <inheritdoc />
    public override async Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ValidateSpatialReference(request.SpatialReferenceWkid))
        {
            throw new GeocodeRequestException($"Unsupported spatial reference: {request.SpatialReferenceWkid}")
            {
                ParameterName = nameof(request.SpatialReferenceWkid)
            };
        }

        var radiusMeters = request.DistanceMeters is > 0
            ? request.DistanceMeters.Value
            : _configuration.DefaultReverseRadiusMeters;

        var sql = $"""
            SELECT id, display_name, address_number, street_name, city, region, postal_code,
                   country, neighborhood, address_type, ST_X(geom) AS x, ST_Y(geom) AS y,
                   ST_Distance(geom::geography, ST_SetSRID(ST_MakePoint(@x, @y), 4326)::geography) AS distance_m
            FROM {_qualifiedTable}
            WHERE ST_DWithin(geom::geography, ST_SetSRID(ST_MakePoint(@x, @y), 4326)::geography, @radius)
            ORDER BY distance_m ASC
            LIMIT 1
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("x", request.X);
        command.Parameters.AddWithValue("y", request.Y);
        command.Parameters.AddWithValue("radius", radiusMeters);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var distance = reader.IsDBNull(reader.GetOrdinal("distance_m"))
            ? (double?)null
            : reader.GetDouble(reader.GetOrdinal("distance_m"));

        var attributes = BuildAttributes(
            providerId: reader.GetInt64(reader.GetOrdinal("id")).ToString(CultureInfo.InvariantCulture),
            additionalAttributes: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["distance"] = distance?.ToString("F1", CultureInfo.InvariantCulture)
            });

        return new ReverseGeocodeMatch(
            Address: GetStringOrEmpty(reader, "display_name"),
            X: reader.GetDouble(reader.GetOrdinal("x")),
            Y: reader.GetDouble(reader.GetOrdinal("y")),
            Attributes: attributes,
            ProviderId: reader.GetInt64(reader.GetOrdinal("id")).ToString(CultureInfo.InvariantCulture),
            DistanceMeters: distance)
        {
            AddressType = GetStringOrNull(reader, "address_type"),
            SpatialReferenceWkid = request.SpatialReferenceWkid,
            StructuredAddress = MapStructuredAddress(reader)
        };
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<GeocodeSuggestion>> SuggestCoreAsync(
        SuggestGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return [];
        }

        var prefix = Normalize(request.Text);
        var limit = ValidateMaxResults(request.MaxResults);

        var sql = $"""
            SELECT id, display_name, address_type
            FROM {_qualifiedTable}
            WHERE search_text LIKE @prefix
            ORDER BY length(search_text) ASC, display_name ASC
            LIMIT {limit}
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("prefix", EscapeLike(prefix) + "%");

        var suggestions = new List<GeocodeSuggestion>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(reader.GetOrdinal("id")).ToString(CultureInfo.InvariantCulture);
            suggestions.Add(new GeocodeSuggestion(
                Text: GetStringOrEmpty(reader, "display_name"),
                MagicKey: id,
                IsCollection: false)
            {
                Category = GetStringOrNull(reader, "address_type")
            });
        }

        return suggestions;
    }

    /// <inheritdoc />
    protected override async Task CheckHealthCoreAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand($"SELECT 1 FROM {_qualifiedTable} LIMIT 1");
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsDataSource)
        {
            _dataSource.Dispose();
        }
    }

    private GeocodeCandidate MapCandidate(DbDataReader reader, double score, int wkid)
    {
        var id = reader.GetInt64(reader.GetOrdinal("id")).ToString(CultureInfo.InvariantCulture);
        var attributes = BuildAttributes(
            providerId: id,
            additionalAttributes: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["score"] = score.ToString("F1", CultureInfo.InvariantCulture)
            });

        return new GeocodeCandidate(
            Address: GetStringOrEmpty(reader, "display_name"),
            X: reader.GetDouble(reader.GetOrdinal("x")),
            Y: reader.GetDouble(reader.GetOrdinal("y")),
            Score: score,
            Attributes: attributes,
            ProviderId: id)
        {
            MatchLevel = score >= 100 ? "exact" : "approximate",
            AddressType = GetStringOrNull(reader, "address_type"),
            SpatialReferenceWkid = wkid,
            StructuredAddress = MapStructuredAddress(reader)
        };
    }

    private static StructuredAddress MapStructuredAddress(DbDataReader reader) => new()
    {
        AddressNumber = GetStringOrNull(reader, "address_number"),
        StreetName = GetStringOrNull(reader, "street_name"),
        City = GetStringOrNull(reader, "city"),
        Region = GetStringOrNull(reader, "region"),
        PostalCode = GetStringOrNull(reader, "postal_code"),
        Country = GetStringOrNull(reader, "country"),
        Neighborhood = GetStringOrNull(reader, "neighborhood")
    };

    private static string ComposeSearchText(ForwardGeocodeRequest request)
    {
        if (request.StructuredAddress is { } structured)
        {
            var streetLine = string.Join(' ', new[] { structured.AddressNumber, structured.StreetName }
                .Where(static p => !string.IsNullOrWhiteSpace(p)));

            var parts = new[]
            {
                streetLine,
                structured.Neighborhood,
                structured.City,
                structured.Region,
                structured.PostalCode,
                structured.Country
            }
            .Where(static p => !string.IsNullOrWhiteSpace(p));

            var composed = string.Join(' ', parts);
            if (!string.IsNullOrWhiteSpace(composed))
            {
                return composed;
            }
        }

        return request.Query;
    }

    // Score is derived from match shape against the normalized record text so identical inputs are
    // deterministic across runs and platforms (no reliance on pg_trgm scoring): exact equality is
    // 100, a prefix match 90, otherwise an all-tokens-contained match scaled by length overlap.
    private static double ScoreMatch(string normalizedQuery, string recordSearchText)
    {
        if (string.Equals(normalizedQuery, recordSearchText, StringComparison.Ordinal))
        {
            return 100.0;
        }

        if (recordSearchText.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            return 90.0;
        }

        var ratio = recordSearchText.Length == 0
            ? 0.0
            : (double)normalizedQuery.Length / recordSearchText.Length;

        return Math.Clamp(50.0 + (ratio * 35.0), 50.0, 85.0);
    }

    private static List<string> Tokenize(string text)
        => Normalize(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    // Normalizes to a lowercase, single-spaced form matching the documented `search_text` column,
    // so the same normalization is applied on load (locator/reference import) and at query time.
    private static string Normalize(string text)
        => GeocodeReferenceText.Normalize(text);

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string GetStringOrEmpty(DbDataReader reader, string column)
        => GetStringOrNull(reader, column) ?? string.Empty;

    private static string? GetStringOrNull(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    // PostgreSQL identifiers cannot be parameterized; the schema/table come from configuration, so
    // they are validated against a strict identifier pattern and double-quoted to prevent injection.
    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !IdentifierRegex().IsMatch(identifier))
        {
            throw new GeocodeProviderException(
                $"Invalid PostgreSQL identifier for the local geocoder reference table: '{identifier}'.")
            {
                ProviderName = ProviderName,
                ErrorCode = GeocodeErrorCodes.InvalidConfiguration
            };
        }

        return "\"" + identifier + "\"";
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();
}
