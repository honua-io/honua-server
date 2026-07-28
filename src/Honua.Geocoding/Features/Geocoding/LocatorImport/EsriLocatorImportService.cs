// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Geocoding.Features.Geocoding.LocatorImport;

/// <summary>
/// Default <see cref="IEsriLocatorImportService"/>: parses classic Esri <c>.loc</c> locator
/// definitions, classifies every construct into an explicit translation report, and materializes
/// supplied reference data into the local PostGIS geocoder reference table (#2152) using the same
/// documented schema and <c>search_text</c> normalization the local provider queries (#2151).
/// </summary>
internal sealed partial class EsriLocatorImportService(
    IConfiguration configuration,
    IOptionsMonitor<LocalGeocoderProviderConfiguration> localConfiguration,
    ILogger<EsriLocatorImportService> logger) : IEsriLocatorImportService
{
    private const int BatchSize = 500;
    private const int MaxSkippedRowDetails = 25;
    private const int MaxReferenceRows = 2_000_000;

    // Canonical reference roles the loader can populate.
    private const string RoleDisplayName = "displayName";
    private const string RoleAddressNumber = "addressNumber";
    private const string RoleStreetName = "streetName";
    private const string RoleCity = "city";
    private const string RoleRegion = "region";
    private const string RolePostalCode = "postalCode";
    private const string RoleCountry = "country";
    private const string RoleNeighborhood = "neighborhood";
    private const string RoleAddressType = "addressType";
    private const string RoleX = "x";
    private const string RoleY = "y";

    private static readonly string[] _roles =
    [
        RoleDisplayName, RoleAddressNumber, RoleStreetName, RoleCity, RoleRegion,
        RolePostalCode, RoleCountry, RoleNeighborhood, RoleAddressType, RoleX, RoleY,
    ];

    // Well-known Esri reference-data field name aliases, per role. Matched case-insensitively
    // against CSV header columns when no explicit fieldMap override is supplied.
    private static readonly Dictionary<string, string[]> _roleAliases = new(StringComparer.Ordinal)
    {
        [RoleDisplayName] = ["displayname", "display_name", "full_addr", "fulladdr", "address", "singleline", "single_line", "match_addr"],
        [RoleAddressNumber] = ["addressnumber", "address_number", "house_num", "housenum", "house_number", "housenumber", "addr_num"],
        [RoleStreetName] = ["streetname", "street_name", "st_name", "street"],
        [RoleCity] = ["city", "place", "placename", "place_name", "municipality"],
        [RoleRegion] = ["region", "state", "province", "state_abbr", "st_abbrev"],
        [RolePostalCode] = ["postalcode", "postal_code", "postal", "zip", "zipcode", "zip_code"],
        [RoleCountry] = ["country", "country_code", "countrycode", "nation"],
        [RoleNeighborhood] = ["neighborhood", "nbrhd", "district"],
        [RoleAddressType] = ["addresstype", "address_type", "addr_type"],
        [RoleX] = ["x", "lon", "lng", "long", "longitude", "point_x", "xcoord", "x_coord"],
        [RoleY] = ["y", "lat", "latitude", "point_y", "ycoord", "y_coord"],
    };

    /// <inheritdoc />
    public async Task<EsriLocatorImportResult> ImportAsync(
        EsriLocatorImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = GeocodingTelemetry.Source.StartActivity("geocoding.locator_import");
        activity?.SetTag("honua.operation", "locator_import");

        try
        {
            var result = await ImportCoreAsync(request, activity, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("honua.geocoding.reference_imported", result.ReferenceDataImported);
            activity?.SetTag("honua.geocoding.records_imported", result.RecordsImported);
            activity?.SetTag("honua.geocoding.records_skipped", result.RecordsSkipped);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private async Task<EsriLocatorImportResult> ImportCoreAsync(
        EsriLocatorImportRequest request,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var locatorName = ResolveLocatorName(request);
        activity?.SetTag("honua.geocoding.locator", locatorName);
        var report = new List<LocatorTranslationEntry>();
        var definition = EsriLocFileParser.Parse(request.LocContent.Span, locatorName, report);

        if (!string.IsNullOrWhiteSpace(request.IndexFileName))
        {
            // Behavior note: the .lox sidecar is a generated match index. It is intentionally not
            // parsed — the local geocoder rebuilds equivalent structures as PostGIS indexes — so it
            // is reported as regenerated rather than silently accepted or dropped.
            report.Add(new LocatorTranslationEntry(
                request.IndexFileName,
                LocatorTranslationStatus.Regenerated,
                "The .lox binary index is not read; equivalent match indexes are rebuilt in PostGIS."));
        }

        var config = localConfiguration.CurrentValue;
        ValidateIdentifier(config.Schema, "schema");
        ValidateIdentifier(config.Table, "table");

        if (request.ReferenceData is null)
        {
            LogParsedOnly(logger, locatorName, report.Count);
            return new EsriLocatorImportResult
            {
                Definition = definition,
                Schema = config.Schema,
                Table = config.Table,
                ReferenceDataImported = false,
                Report = report,
            };
        }

        var connectionString = !string.IsNullOrWhiteSpace(config.ConnectionString)
            ? config.ConnectionString
            : configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new EsriLocatorImportException(
                "The local geocoder has no reference database configured. Set " +
                "Geocoding:Providers:Local:ConnectionString or ConnectionStrings:DefaultConnection.");
        }

        int imported;
        int skippedCount;
        List<LocatorImportSkippedRow> skippedRows;

        try
        {
            (imported, skippedCount, skippedRows) = await LoadReferenceDataAsync(
                request, definition, config, connectionString, report, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException ex)
        {
            LogReferenceStoreFailure(logger, locatorName, ex);
            throw new EsriLocatorImportException(
                "The geocoder reference store rejected the locator import.", ex);
        }

        LogImported(logger, locatorName, imported, skippedCount);

        return new EsriLocatorImportResult
        {
            Definition = definition,
            Schema = config.Schema,
            Table = config.Table,
            ReferenceDataImported = true,
            RecordsImported = imported,
            RecordsSkipped = skippedCount,
            SkippedRows = skippedRows,
            Report = report,
        };
    }

    private static async Task<(int Imported, int Skipped, List<LocatorImportSkippedRow> SkippedRows)> LoadReferenceDataAsync(
        EsriLocatorImportRequest request,
        EsriLocatorDefinition definition,
        LocalGeocoderProviderConfiguration config,
        string connectionString,
        List<LocatorTranslationEntry> report,
        CancellationToken cancellationToken)
    {
        using var textReader = new StreamReader(request.ReferenceData!, Encoding.UTF8, leaveOpen: true);
        var records = LocatorReferenceCsv.ReadRecordsAsync(textReader, cancellationToken);

        await using var enumerator = records.GetAsyncEnumerator(cancellationToken);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw new EsriLocatorImportException("The reference data CSV is empty; a header row is required.");
        }

        var header = enumerator.Current;
        var mapping = BuildColumnMapping(header, request.FieldMap, report);

        var qualifiedTable = $"{Quote(config.Schema)}.{Quote(config.Table)}";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await EnsureReferenceTableAsync(connection, qualifiedTable, config.Table, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (request.ReplaceExisting)
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM {qualifiedTable}", connection, transaction);
            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var batch = new ReferenceRowBatch(BatchSize);
        var imported = 0;
        var skippedCount = 0;
        var skippedRows = new List<LocatorImportSkippedRow>();
        var rowNumber = 0;

        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            rowNumber++;
            if (rowNumber > MaxReferenceRows)
            {
                throw new EsriLocatorImportException(
                    $"The reference data exceeds the {MaxReferenceRows:N0}-row import limit.");
            }

            var record = enumerator.Current;
            if (!TryMapRow(record, header.Length, mapping, definition, out var row, out var reason))
            {
                skippedCount++;
                if (skippedRows.Count < MaxSkippedRowDetails)
                {
                    skippedRows.Add(new LocatorImportSkippedRow(rowNumber, reason!));
                }

                continue;
            }

            batch.Add(row);
            if (batch.Count >= BatchSize)
            {
                imported += await FlushBatchAsync(connection, transaction, qualifiedTable, batch, cancellationToken).ConfigureAwait(false);
            }
        }

        if (batch.Count > 0)
        {
            imported += await FlushBatchAsync(connection, transaction, qualifiedTable, batch, cancellationToken).ConfigureAwait(false);
        }

        // Phantom-commit guard (mirrors DbTransactionExtensions.CommitSafelyAsync in the Postgres
        // module, which this satellite cannot reference): check the token before the COMMIT
        // round-trip begins, then never interrupt the in-flight COMMIT.
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return (imported, skippedCount, skippedRows);
    }

    private static Dictionary<string, int> BuildColumnMapping(
        string[] header,
        IReadOnlyDictionary<string, string>? fieldMap,
        List<LocatorTranslationEntry> report)
    {
        if (header.Length == 0 || header.All(static c => string.IsNullOrWhiteSpace(c)))
        {
            throw new EsriLocatorImportException("The reference data CSV header row is empty.");
        }

        var columnsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            var column = header[i].Trim();
            if (column.Length > 0)
            {
                // First occurrence wins for duplicate header names.
                _ = columnsByName.TryAdd(column, i);
            }
        }

        var mapping = new Dictionary<string, int>(StringComparer.Ordinal);

        if (fieldMap is not null)
        {
            foreach (var (roleKey, column) in fieldMap)
            {
                var role = _roles.FirstOrDefault(r => r.Equals(roleKey, StringComparison.OrdinalIgnoreCase))
                    ?? throw new EsriLocatorImportException(
                        $"Unknown field-map role '{roleKey}'. Valid roles: {string.Join(", ", _roles)}.");

                // System.Text.Json can materialize a JSON null into this non-nullable dictionary
                // value; reject it as client input instead of throwing NullReferenceException.
                if (string.IsNullOrWhiteSpace(column))
                {
                    throw new EsriLocatorImportException(
                        $"Field-map value for role '{roleKey}' must be a non-empty CSV column name.");
                }

                if (!columnsByName.TryGetValue(column.Trim(), out var index))
                {
                    throw new EsriLocatorImportException(
                        $"Field-map column '{column}' does not exist in the reference data CSV header.");
                }

                mapping[role] = index;
            }
        }

        foreach (var role in _roles.Where(role => !mapping.ContainsKey(role)))
        {
            foreach (var alias in _roleAliases[role])
            {
                if (columnsByName.TryGetValue(alias, out var index) && !mapping.ContainsValue(index))
                {
                    mapping[role] = index;
                    break;
                }
            }
        }

        if (!mapping.ContainsKey(RoleX) || !mapping.ContainsKey(RoleY))
        {
            throw new EsriLocatorImportException(
                "The reference data CSV must contain WGS84 longitude/latitude columns (for example " +
                "POINT_X/POINT_Y, LON/LAT) or an explicit fieldMap for the 'x' and 'y' roles.");
        }

        if (!mapping.ContainsKey(RoleDisplayName) && !mapping.ContainsKey(RoleStreetName))
        {
            throw new EsriLocatorImportException(
                "The reference data CSV must contain an address column (for example ADDRESS or " +
                "STREET_NAME) or an explicit fieldMap for the 'displayName' or 'streetName' role.");
        }

        // Report every header column: mapped columns as supported, the rest explicitly ignored.
        var mappedIndexes = mapping.ToDictionary(static p => p.Value, static p => p.Key);
        for (var i = 0; i < header.Length; i++)
        {
            var column = header[i].Trim();
            if (column.Length == 0)
            {
                continue;
            }

            report.Add(mappedIndexes.TryGetValue(i, out var role)
                ? new LocatorTranslationEntry(column, LocatorTranslationStatus.Supported, $"Reference column mapped to '{role}'.")
                : new LocatorTranslationEntry(column, LocatorTranslationStatus.Ignored, "Reference column is not mapped to a geocoder field."));
        }

        return mapping;
    }

    private static bool TryMapRow(
        string[] record,
        int headerLength,
        Dictionary<string, int> mapping,
        EsriLocatorDefinition definition,
        out ReferenceRow row,
        out string? reason)
    {
        row = default;

        if (record.Length != headerLength)
        {
            reason = $"Expected {headerLength} columns but found {record.Length}.";
            return false;
        }

        string? Get(string role)
        {
            if (!mapping.TryGetValue(role, out var index))
            {
                return null;
            }

            var value = record[index].Trim();
            return value.Length == 0 ? null : value;
        }

        var xText = Get(RoleX);
        var yText = Get(RoleY);
        if (xText is null || yText is null ||
            !double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            reason = "Longitude/latitude values are missing or not valid numbers.";
            return false;
        }

        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < -180 or > 180 || y is < -90 or > 90)
        {
            reason = "Longitude/latitude values are outside the WGS84 range.";
            return false;
        }

        var addressNumber = Get(RoleAddressNumber);
        var streetName = Get(RoleStreetName);
        var city = Get(RoleCity);
        var region = Get(RoleRegion);
        var postalCode = Get(RolePostalCode);

        var country = Get(RoleCountry);
        var neighborhood = Get(RoleNeighborhood);

        var displayName = Get(RoleDisplayName) ?? ComposeDisplayName(addressNumber, streetName, city, region, postalCode);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            reason = "Row has no address text to geocode against.";
            return false;
        }

        // search_text must contain every stored structured component, not just the display
        // label: LocalPostgisGeocodeProvider.ComposeSearchText folds neighborhood and country
        // into structured queries, and forward matching requires all query tokens to appear
        // in the record's normalized text.
        var searchSource = string.Join(' ', new[]
        {
            displayName, addressNumber, streetName, neighborhood, city, region, postalCode, country,
        }.Where(static p => !string.IsNullOrWhiteSpace(p)));

        row = new ReferenceRow(
            DisplayName: displayName,
            SearchText: GeocodeReferenceText.Normalize(searchSource),
            AddressNumber: addressNumber,
            StreetName: streetName,
            City: city,
            Region: region,
            PostalCode: postalCode,
            Country: country,
            Neighborhood: neighborhood,
            AddressType: Get(RoleAddressType) ?? definition.Category,
            X: x,
            Y: y);
        reason = null;
        return true;
    }

    private static string ComposeDisplayName(
        string? addressNumber,
        string? streetName,
        string? city,
        string? region,
        string? postalCode)
    {
        var streetLine = string.Join(' ', new[] { addressNumber, streetName }
            .Where(static p => !string.IsNullOrWhiteSpace(p)));
        var regionLine = string.Join(' ', new[] { region, postalCode }
            .Where(static p => !string.IsNullOrWhiteSpace(p)));

        return string.Join(", ", new[] { streetLine, city, regionLine }
            .Where(static p => !string.IsNullOrWhiteSpace(p)));
    }

    private static async Task EnsureReferenceTableAsync(
        NpgsqlConnection connection,
        string qualifiedTable,
        string table,
        CancellationToken cancellationToken)
    {
        // Documented reference schema: docs/reference/geocoding/local-postgis-geocoder.md (#2151).
        var indexBase = "ix_" + (table.Length > 48 ? table[..48] : table);
        var sql = $"""
            CREATE TABLE IF NOT EXISTS {qualifiedTable} (
                id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                display_name   text NOT NULL,
                search_text    text NOT NULL,
                address_number text,
                street_name    text,
                city           text,
                region         text,
                postal_code    text,
                country        text,
                neighborhood   text,
                address_type   text,
                geom           geometry(Point, 4326) NOT NULL
            );
            CREATE INDEX IF NOT EXISTS {Quote(indexBase + "_geom")} ON {qualifiedTable} USING gist (geom);
            CREATE INDEX IF NOT EXISTS {Quote(indexBase + "_search_text")} ON {qualifiedTable} (search_text text_pattern_ops);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> FlushBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string qualifiedTable,
        ReferenceRowBatch batch,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {qualifiedTable}
                (display_name, search_text, address_number, street_name, city, region,
                 postal_code, country, neighborhood, address_type, geom)
            SELECT t.display_name, t.search_text, t.address_number, t.street_name, t.city, t.region,
                   t.postal_code, t.country, t.neighborhood, t.address_type,
                   ST_SetSRID(ST_MakePoint(t.x, t.y), 4326)
            FROM unnest(@display_name, @search_text, @address_number, @street_name, @city, @region,
                        @postal_code, @country, @neighborhood, @address_type, @x, @y)
                 AS t(display_name, search_text, address_number, street_name, city, region,
                      postal_code, country, neighborhood, address_type, x, y)
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("display_name", batch.DisplayNames.ToArray());
        command.Parameters.AddWithValue("search_text", batch.SearchTexts.ToArray());
        command.Parameters.AddWithValue("address_number", batch.AddressNumbers.ToArray());
        command.Parameters.AddWithValue("street_name", batch.StreetNames.ToArray());
        command.Parameters.AddWithValue("city", batch.Cities.ToArray());
        command.Parameters.AddWithValue("region", batch.Regions.ToArray());
        command.Parameters.AddWithValue("postal_code", batch.PostalCodes.ToArray());
        command.Parameters.AddWithValue("country", batch.Countries.ToArray());
        command.Parameters.AddWithValue("neighborhood", batch.Neighborhoods.ToArray());
        command.Parameters.AddWithValue("address_type", batch.AddressTypes.ToArray());
        command.Parameters.AddWithValue("x", batch.Xs.ToArray());
        command.Parameters.AddWithValue("y", batch.Ys.ToArray());

        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        batch.Clear();
        return inserted;
    }

    private string ResolveLocatorName(EsriLocatorImportRequest request)
    {
        // The GeocodeServer runtime serves only the statically configured Geocoding:LocatorName
        // route; until per-locator registration exists, an import must land under that name or
        // the response would advertise a locator the server cannot serve.
        var configured = configuration["Geocoding:LocatorName"];
        var served = string.IsNullOrWhiteSpace(configured) ? "World" : configured.Trim();

        if (string.IsNullOrWhiteSpace(request.LocatorName))
        {
            return served;
        }

        var name = request.LocatorName.Trim();
        if (name.Length == 0 || name.Length > 128 || !LocatorNameRegex().IsMatch(name))
        {
            throw new EsriLocatorImportException(
                "Locator name must be 1-128 characters of letters, digits, spaces, '.', '_' or '-'.");
        }

        if (!string.Equals(name, served, StringComparison.OrdinalIgnoreCase))
        {
            throw new EsriLocatorImportException(
                $"Locator name '{name}' does not match the geocode service name '{served}' this " +
                "server registers. Import under the configured name or change Geocoding:LocatorName.");
        }

        return name;
    }

    private static void ValidateIdentifier(string identifier, string kind)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !IdentifierRegex().IsMatch(identifier))
        {
            throw new EsriLocatorImportException(
                $"The configured local geocoder reference {kind} is not a valid PostgreSQL identifier.");
        }
    }

    // Identifiers are validated against the same strict pattern the local provider uses and then
    // double-quoted, so schema/table configuration cannot inject SQL.
    private static string Quote(string identifier) => "\"" + identifier + "\"";

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._\-]*$")]
    private static partial Regex LocatorNameRegex();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Parsed Esri locator '{LocatorName}' without reference data ({ReportEntries} report entries).")]
    private static partial void LogParsedOnly(ILogger logger, string locatorName, int reportEntries);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Imported Esri locator '{LocatorName}': {Imported} reference rows loaded, {Skipped} skipped.")]
    private static partial void LogImported(ILogger logger, string locatorName, int imported, int skipped);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "The geocoder reference store rejected the Esri locator import for '{LocatorName}'.")]
    private static partial void LogReferenceStoreFailure(ILogger logger, string locatorName, Exception exception);

    private sealed class ReferenceRowBatch(int capacity)
    {
        public List<string> DisplayNames { get; } = new(capacity);
        public List<string> SearchTexts { get; } = new(capacity);
        public List<string?> AddressNumbers { get; } = new(capacity);
        public List<string?> StreetNames { get; } = new(capacity);
        public List<string?> Cities { get; } = new(capacity);
        public List<string?> Regions { get; } = new(capacity);
        public List<string?> PostalCodes { get; } = new(capacity);
        public List<string?> Countries { get; } = new(capacity);
        public List<string?> Neighborhoods { get; } = new(capacity);
        public List<string?> AddressTypes { get; } = new(capacity);
        public List<double> Xs { get; } = new(capacity);
        public List<double> Ys { get; } = new(capacity);

        public int Count => DisplayNames.Count;

        public void Add(in ReferenceRow row)
        {
            DisplayNames.Add(row.DisplayName);
            SearchTexts.Add(row.SearchText);
            AddressNumbers.Add(row.AddressNumber);
            StreetNames.Add(row.StreetName);
            Cities.Add(row.City);
            Regions.Add(row.Region);
            PostalCodes.Add(row.PostalCode);
            Countries.Add(row.Country);
            Neighborhoods.Add(row.Neighborhood);
            AddressTypes.Add(row.AddressType);
            Xs.Add(row.X);
            Ys.Add(row.Y);
        }

        public void Clear()
        {
            DisplayNames.Clear();
            SearchTexts.Clear();
            AddressNumbers.Clear();
            StreetNames.Clear();
            Cities.Clear();
            Regions.Clear();
            PostalCodes.Clear();
            Countries.Clear();
            Neighborhoods.Clear();
            AddressTypes.Clear();
            Xs.Clear();
            Ys.Clear();
        }
    }

    private readonly record struct ReferenceRow(
        string DisplayName,
        string SearchText,
        string? AddressNumber,
        string? StreetName,
        string? City,
        string? Region,
        string? PostalCode,
        string? Country,
        string? Neighborhood,
        string? AddressType,
        double X,
        double Y);
}
