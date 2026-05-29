// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// Imports OGC Web Feature Service (WFS) feature data into Honua PostGIS.
/// </summary>
internal sealed partial class OgcWfsImportService : IOgcWfsImportService
{
    /// <summary>
    /// Named <see cref="IHttpClientFactory"/> identifier for the WFS paged GetFeature client.
    /// </summary>
    public const string HttpClientName = "ogc-wfs-import";

    private const string DefaultGeoJsonOutputFormat = "application/json";
    private const string SourceKind = "ogc-wfs";

    private readonly IOgcServiceMigrationScanner _scanner;
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly HttpClient _httpClient;
    private readonly PostgresSchemaConfiguration _schemaConfiguration;
    private readonly ILogger<OgcWfsImportService> _logger;

    public OgcWfsImportService(
        IOgcServiceMigrationScanner scanner,
        IDatabaseConnectionProvider connectionProvider,
        HttpClient httpClient,
        ILogger<OgcWfsImportService> logger,
        PostgresSchemaConfiguration? schemaConfiguration = null)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _schemaConfiguration = schemaConfiguration ?? new PostgresSchemaConfiguration(
            PostgresSchemaConfiguration.DefaultMetadataSchema,
            PostgresSchemaConfiguration.DefaultDataSchema,
            [PostgresSchemaConfiguration.DefaultDataSchema, "public"]);
    }

    /// <inheritdoc />
    public async Task<OgcWfsImportResult> ImportFeaturesAsync(
        OgcWfsImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            throw new ArgumentException("ServiceUrl is required.", nameof(request));
        }

        if (request.PageSize <= 0)
        {
            throw new ArgumentException("PageSize must be greater than zero.", nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
        var jobId = string.IsNullOrWhiteSpace(request.JobId)
            ? Guid.NewGuid().ToString("N")[..8]
            : request.JobId;
        var targetSchema = ResolveTargetSchema(request.TargetSchema);
        var safeSourceUrl = ToSafeSourceUrl(request.ServiceUrl);

        Log.ImportStarting(_logger, safeSourceUrl, jobId, request.DryRun);

        MigrationSourceInventoryArtifact inventory;
        try
        {
            inventory = await _scanner.ScanSourceAsync(
                new OgcServiceScanRequest
                {
                    ServiceType = "WFS",
                    ServiceUrl = request.ServiceUrl,
                    Version = request.Version,
                    TimeoutSeconds = request.RequestTimeoutSeconds,
                    AllowUnsafeLocalUrls = request.AllowUnsafeLocalUrls
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.InventoryScanFailed(_logger, safeSourceUrl, ex);
            return CreateFailureResult(request, jobId, "Failed to scan WFS source for migration planning.", stopwatch.Elapsed);
        }

        if (!string.Equals(inventory.SourceKind, SourceKind, StringComparison.OrdinalIgnoreCase))
        {
            return CreateFailureResult(request, jobId, "Source URL did not resolve to a WFS endpoint.", stopwatch.Elapsed);
        }

        if (string.Equals(inventory.ScanCompleteness.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            var reason = inventory.ScanCompleteness.Warnings.FirstOrDefault()
                ?? "WFS source scan failed.";
            return CreateFailureResult(request, jobId, reason, stopwatch.Elapsed, inventory: inventory);
        }

        var version = inventory.Source.Version ?? request.Version ?? "2.0.0";
        var serviceUri = new Uri(request.ServiceUrl, UriKind.Absolute);
        var selectedResources = SelectResources(inventory, request.FeatureTypeNames);

        var perFeatureType = new List<OgcWfsImportedFeatureType>();
        var topLevelWarnings = new List<string>();
        var featuresCopied = 0;
        var featuresFailed = 0;
        var imported = 0;
        var skipped = 0;

        foreach (var resource in selectedResources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsUnsupported(resource))
            {
                perFeatureType.Add(new OgcWfsImportedFeatureType
                {
                    SourceName = resource.Name,
                    GeometryType = resource.GeometryType,
                    Classification = MigrationFidelityAutomationStatuses.Unsupported,
                    Warnings = [resource.Compatibility.Reason]
                });
                skipped++;
                continue;
            }

            var tableName = BuildTableName(resource.Name);
            var srid = ResolveSrid(resource, request.TargetSrid);

            if (request.DryRun)
            {
                perFeatureType.Add(new OgcWfsImportedFeatureType
                {
                    SourceName = resource.Name,
                    TargetSchema = targetSchema,
                    TargetTable = tableName,
                    GeometryType = resource.GeometryType,
                    Srid = srid,
                    FeaturesCopied = 0,
                    FeaturesFailed = 0,
                    Classification = MigrationFidelityAutomationStatuses.Automated,
                    Warnings = []
                });
                imported++;
                continue;
            }

            try
            {
                var outcome = await ImportFeatureTypeAsync(
                        request,
                        serviceUri,
                        version,
                        resource,
                        targetSchema,
                        tableName,
                        srid,
                        cancellationToken)
                    .ConfigureAwait(false);

                perFeatureType.Add(outcome);
                featuresCopied += outcome.FeaturesCopied;
                featuresFailed += outcome.FeaturesFailed;
                imported++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.FeatureTypeImportFailed(_logger, resource.Name, ex);
                perFeatureType.Add(new OgcWfsImportedFeatureType
                {
                    SourceName = resource.Name,
                    TargetSchema = targetSchema,
                    TargetTable = tableName,
                    GeometryType = resource.GeometryType,
                    Srid = srid,
                    Classification = MigrationFidelityAutomationStatuses.ManualReview,
                    Warnings = [ToSafeFailureReason(ex)]
                });
                skipped++;
            }
        }

        var manifest = MigrationManifestTranslator.Translate(inventory);
        var parityEvidence = MigrationParityEvidenceGenerator.Generate(inventory, manifest);

        stopwatch.Stop();
        Log.ImportCompleted(_logger, safeSourceUrl, imported, skipped, featuresCopied, featuresFailed, stopwatch.Elapsed.TotalSeconds);

        return new OgcWfsImportResult
        {
            Success = true,
            WasDryRun = request.DryRun,
            SourceServiceUrl = safeSourceUrl,
            SourceVersion = version,
            FeatureTypesPlanned = selectedResources.Length,
            FeatureTypesImported = imported,
            FeatureTypesSkipped = skipped,
            FeaturesCopied = featuresCopied,
            FeaturesFailed = featuresFailed,
            FeatureTypes = perFeatureType,
            Warnings = topLevelWarnings,
            Manifest = manifest,
            ParityEvidence = parityEvidence,
            Duration = stopwatch.Elapsed
        };
    }

    private async Task<OgcWfsImportedFeatureType> ImportFeatureTypeAsync(
        OgcWfsImportRequest request,
        Uri serviceUri,
        string version,
        MigrationInventoryResource resource,
        string targetSchema,
        string tableName,
        int srid,
        CancellationToken cancellationToken)
    {
        var perTypeWarnings = new List<string>();
        var classification = MigrationFidelityAutomationStatuses.Automated;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;

        await EnsureSchemaAsync(connection, targetSchema, cancellationToken).ConfigureAwait(false);

        if (request.OverwriteExisting)
        {
            await DropTableAsync(connection, targetSchema, tableName, cancellationToken).ConfigureAwait(false);
        }
        else if (await TableExistsAsync(connection, targetSchema, tableName, cancellationToken).ConfigureAwait(false))
        {
            perTypeWarnings.Add("Target table already exists; idempotent re-run skipped data copy. Use overwriteExisting=true to recreate.");
            Log.IdempotentSkip(_logger, targetSchema, tableName);
            return new OgcWfsImportedFeatureType
            {
                SourceName = resource.Name,
                TargetSchema = targetSchema,
                TargetTable = tableName,
                GeometryType = resource.GeometryType,
                Srid = srid,
                FeaturesCopied = 0,
                FeaturesFailed = 0,
                Classification = classification,
                Warnings = [.. perTypeWarnings]
            };
        }

        var fields = resource.Fields
            .Where(field => !IsGeometryField(field))
            .ToArray();

        await CreateTableAsync(connection, targetSchema, tableName, fields, resource.GeometryType, srid, cancellationToken).ConfigureAwait(false);

        var inserted = 0;
        var failed = 0;
        var startIndex = 0;
        var pageSize = request.PageSize;
        var hasMore = true;
        var pageNumber = 0;
        var mixedGeometryDetected = false;

        while (hasMore)
        {
            cancellationToken.ThrowIfCancellationRequested();

            pageNumber++;
            var url = BuildGetFeatureUrl(serviceUri, version, resource.Name, startIndex, pageSize);
            FeaturePage page;
            try
            {
                page = await FetchFeaturePageAsync(url, request, cancellationToken).ConfigureAwait(false);
            }
            catch (FormatException ex)
            {
                perTypeWarnings.Add(ex.Message);
                classification = MigrationFidelityAutomationStatuses.ManualReview;
                break;
            }

            if (page.Features.Count == 0)
            {
                break;
            }

            var (pageInserted, pageFailed, pageMixed) = await InsertFeaturesAsync(
                    connection,
                    targetSchema,
                    tableName,
                    fields,
                    page.Features,
                    resource.GeometryType,
                    srid,
                    cancellationToken)
                .ConfigureAwait(false);

            inserted += pageInserted;
            failed += pageFailed;
            mixedGeometryDetected |= pageMixed;

            startIndex += page.Features.Count;
            hasMore = page.Features.Count >= pageSize && (page.NumberMatched == null || startIndex < page.NumberMatched);
            Log.BatchInserted(_logger, resource.Name, pageNumber, pageInserted, pageFailed, inserted);
        }

        await CreateSpatialIndexAsync(connection, targetSchema, tableName, cancellationToken).ConfigureAwait(false);

        if (mixedGeometryDetected)
        {
            perTypeWarnings.Add("Source features contained mixed geometry types; coercion to declared geometry type may have occurred.");
        }

        if (failed > 0)
        {
            perTypeWarnings.Add($"{failed} feature(s) failed to insert.");
            classification = MigrationFidelityAutomationStatuses.ManualReview;
        }

        return new OgcWfsImportedFeatureType
        {
            SourceName = resource.Name,
            TargetSchema = targetSchema,
            TargetTable = tableName,
            GeometryType = resource.GeometryType,
            Srid = srid,
            FeaturesCopied = inserted,
            FeaturesFailed = failed,
            Classification = classification,
            Warnings = [.. perTypeWarnings]
        };
    }

    private async Task<FeaturePage> FetchFeaturePageAsync(
        Uri url,
        OgcWfsImportRequest request,
        CancellationToken cancellationToken)
    {
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        requestMessage.Headers.Accept.Clear();
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/geo+json"));
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            var headerValue = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{request.Username}:{request.Password ?? string.Empty}"));
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Basic", headerValue);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.RequestTimeoutSeconds <= 0 ? 120 : request.RequestTimeoutSeconds));

        using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            return ParseFeatureCollection(document);
        }
        catch (JsonException)
        {
            throw new FormatException(
                "WFS GetFeature response was not a valid GeoJSON FeatureCollection. The source may not advertise the GeoJSON output format.");
        }
    }

    private static FeaturePage ParseFeatureCollection(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("WFS GetFeature GeoJSON payload did not contain a FeatureCollection.features array.");
        }

        int? numberMatched = null;
        if (root.TryGetProperty("numberMatched", out var matched))
        {
            if (matched.ValueKind == JsonValueKind.Number && matched.TryGetInt32(out var value))
            {
                numberMatched = value;
            }
        }

        var list = new List<JsonElement>(features.GetArrayLength());
        foreach (var feature in features.EnumerateArray())
        {
            list.Add(feature.Clone());
        }

        return new FeaturePage(list, numberMatched);
    }

    private async Task<(int inserted, int failed, bool mixedGeometry)> InsertFeaturesAsync(
        NpgsqlConnection connection,
        string targetSchema,
        string tableName,
        MigrationInventoryField[] fields,
        IReadOnlyList<JsonElement> features,
        string? declaredGeometryType,
        int srid,
        CancellationToken cancellationToken)
    {
        var hasGeometry = !string.IsNullOrWhiteSpace(declaredGeometryType);
        var columnNames = string.Join(", ", fields.Select(f => $"\"{SanitizeColumnName(f.Name)}\""));
        var parameterPlaceholders = string.Join(", ", fields.Select((_, i) => $"@p{i}"));

        if (hasGeometry)
        {
            if (columnNames.Length > 0)
            {
                columnNames += ", ";
                parameterPlaceholders += ", ";
            }

            columnNames += "geom";
            parameterPlaceholders += $"{BuildGeometryInsertExpression(declaredGeometryType!, srid)}";
        }

        var insertSql = $"INSERT INTO {QuoteIdentifier(targetSchema)}.{QuoteIdentifier(tableName)} ({columnNames}) VALUES ({parameterPlaceholders})";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = insertSql;

        for (var i = 0; i < fields.Length; i++)
        {
            cmd.Parameters.AddWithValue($"p{i}", DBNull.Value);
        }

        if (hasGeometry)
        {
            cmd.Parameters.Add("geom", NpgsqlDbType.Text).Value = DBNull.Value;
        }

        await cmd.PrepareAsync(cancellationToken).ConfigureAwait(false);

        var inserted = 0;
        var failed = 0;
        var mixed = false;
        string? firstError = null;

        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                feature.TryGetProperty("properties", out var properties);
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    object? value = null;
                    if (properties.ValueKind == JsonValueKind.Object &&
                        properties.TryGetProperty(field.Name, out var jsonValue) &&
                        jsonValue.ValueKind != JsonValueKind.Null)
                    {
                        value = ConvertJsonValue(jsonValue, field.FieldType);
                    }

                    cmd.Parameters[$"p{i}"].Value = value ?? DBNull.Value;
                }

                if (hasGeometry)
                {
                    if (feature.TryGetProperty("geometry", out var geometry) &&
                        geometry.ValueKind == JsonValueKind.Object)
                    {
                        var geomJson = geometry.GetRawText();
                        var sourceType = geometry.TryGetProperty("type", out var typeElement)
                            ? typeElement.GetString()
                            : null;
                        if (sourceType != null && !GeometryTypeMatches(declaredGeometryType!, sourceType))
                        {
                            mixed = true;
                        }

                        cmd.Parameters["geom"].Value = geomJson;
                    }
                    else
                    {
                        cmd.Parameters["geom"].Value = DBNull.Value;
                    }
                }

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                inserted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstError ??= ex.Message;
                failed++;
            }
        }

        if (firstError != null)
        {
            Log.FeatureInsertFailures(_logger, tableName, failed, firstError);
        }

        return (inserted, failed, mixed);
    }

    private static object? ConvertJsonValue(JsonElement element, string sourceType)
    {
        var normalized = (sourceType ?? string.Empty).ToLowerInvariant();
        return element.ValueKind switch
        {
            JsonValueKind.String when normalized.Contains("date") || normalized.Contains("time") =>
                DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
                    ? dt
                    : element.GetString(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when normalized.Contains("int") =>
                element.TryGetInt64(out var i) ? i : (object?)element.GetDouble(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }

    private async Task CreateTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        MigrationInventoryField[] fields,
        string? geometryType,
        int srid,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} (");
        sb.AppendLine("    honua_objectid BIGSERIAL PRIMARY KEY");

        foreach (var field in fields)
        {
            var pgType = MapFieldType(field.FieldType);
            sb.AppendLine(CultureInfo.InvariantCulture, $"    , \"{SanitizeColumnName(field.Name)}\" {pgType}");
        }

        if (!string.IsNullOrWhiteSpace(geometryType))
        {
            var pgGeometryType = MapGeometryType(geometryType);
            sb.AppendLine(CultureInfo.InvariantCulture, $"    , geom geometry({pgGeometryType}, {srid})");
        }

        sb.AppendLine(");");

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        Log.TableCreated(_logger, schemaName, tableName);
    }

    private static async Task EnsureSchemaAsync(NpgsqlConnection connection, string schemaName, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(schemaName)}";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DropTableAsync(NpgsqlConnection connection, string schemaName, string tableName, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} CASCADE";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string schemaName, string tableName, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table LIMIT 1";
        cmd.Parameters.AddWithValue("schema", schemaName);
        cmd.Parameters.AddWithValue("table", tableName);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result != null && result != DBNull.Value;
    }

    private static async Task CreateSpatialIndexAsync(NpgsqlConnection connection, string schemaName, string tableName, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS {QuoteIdentifier(tableName + "_geom_idx")} ON {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} USING GIST (geom)";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MigrationInventoryResource[] SelectResources(
        MigrationSourceInventoryArtifact inventory,
        string[]? requestedFeatureTypes)
    {
        var allFeatureTypes = inventory.Resources
            .Where(r => string.Equals(r.Kind, "feature-type", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (requestedFeatureTypes == null || requestedFeatureTypes.Length == 0)
        {
            return allFeatureTypes;
        }

        var requested = new HashSet<string>(requestedFeatureTypes, StringComparer.OrdinalIgnoreCase);
        return allFeatureTypes.Where(r => requested.Contains(r.Name)).ToArray();
    }

    private static bool IsUnsupported(MigrationInventoryResource resource)
        => string.Equals(resource.Compatibility.Level, "incompatible", StringComparison.OrdinalIgnoreCase) ||
           string.IsNullOrWhiteSpace(resource.GeometryType);

    private static bool IsGeometryField(MigrationInventoryField field)
    {
        var type = (field.FieldType ?? string.Empty).ToLowerInvariant();
        return type.Contains("gml:") || type.Contains("geometry") || type.Contains("point") ||
               type.Contains("line") || type.Contains("polygon") || type.Contains("surface") ||
               type.Contains("curve");
    }

    private static int ResolveSrid(MigrationInventoryResource resource, int? requested)
    {
        if (requested is > 0)
        {
            return requested.Value;
        }

        var resolved = resource.SpatialReferences
            .Select(sr => sr.Srid)
            .FirstOrDefault(srid => srid is > 0);
        return resolved ?? 4326;
    }

    private string ResolveTargetSchema(string? requestedSchema)
    {
        var schema = string.IsNullOrWhiteSpace(requestedSchema)
            ? _schemaConfiguration.DefaultOperationalSchema
            : requestedSchema.Trim();

        if (!SchemaSearchPath.IsValidIdentifier(schema))
        {
            throw new ArgumentException("Target schema contains invalid characters.", nameof(requestedSchema));
        }

        return schema;
    }

    private static Uri BuildGetFeatureUrl(Uri serviceUri, string version, string featureTypeName, int startIndex, int pageSize)
    {
        var useV2 = version.StartsWith("2.", StringComparison.Ordinal);
        var query = new List<KeyValuePair<string, string?>>
        {
            new("service", "WFS"),
            new("version", version),
            new("request", "GetFeature"),
            new(useV2 ? "typeNames" : "typeName", featureTypeName),
            new("outputFormat", DefaultGeoJsonOutputFormat),
            new(useV2 ? "count" : "maxFeatures", pageSize.ToString(CultureInfo.InvariantCulture)),
            new("startIndex", startIndex.ToString(CultureInfo.InvariantCulture))
        };

        var builder = new UriBuilder(serviceUri)
        {
            Query = MergeQuery(serviceUri.Query, query)
        };
        return builder.Uri;
    }

    private static string MergeQuery(string existingQuery, IReadOnlyList<KeyValuePair<string, string?>> overrides)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var trimmed = existingQuery.TrimStart('?');
        if (trimmed.Length > 0)
        {
            foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);
                var key = separator < 0 ? Uri.UnescapeDataString(part) : Uri.UnescapeDataString(part[..separator]);
                if (overrides.Any(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        foreach (var pair in overrides)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                pairs.Add(new KeyValuePair<string, string>(pair.Key, pair.Value));
            }
        }

        return string.Join("&", pairs.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }

    private static string ToSafeSourceUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string BuildTableName(string featureTypeName)
    {
        var withoutPrefix = featureTypeName;
        var colonIndex = withoutPrefix.LastIndexOf(':');
        if (colonIndex >= 0 && colonIndex < withoutPrefix.Length - 1)
        {
            withoutPrefix = withoutPrefix[(colonIndex + 1)..];
        }

        var slug = WfsIdentifierPattern().Replace(withoutPrefix, "_").Trim('_').ToLowerInvariant();
        if (slug.Length == 0)
        {
            slug = "wfs_layer";
        }

        if (!char.IsLetter(slug[0]) && slug[0] != '_')
        {
            slug = "wfs_" + slug;
        }

        const int MaxIdentifierLength = 60;
        if (slug.Length > MaxIdentifierLength)
        {
            slug = slug[..MaxIdentifierLength];
        }

        return $"wfs_{slug}";
    }

    private static string SanitizeColumnName(string fieldName)
    {
        var slug = WfsIdentifierPattern().Replace(fieldName, "_").Trim('_');
        if (slug.Length == 0)
        {
            slug = "field";
        }

        if (!char.IsLetter(slug[0]) && slug[0] != '_')
        {
            slug = "_" + slug;
        }

        const int MaxIdentifierLength = 60;
        if (slug.Length > MaxIdentifierLength)
        {
            slug = slug[..MaxIdentifierLength];
        }

        return slug;
    }

    [GeneratedRegex("[^A-Za-z0-9_]+")]
    private static partial Regex WfsIdentifierPattern();

    private static string MapFieldType(string sourceType)
    {
        var normalized = (sourceType ?? "string").ToLowerInvariant();
        if (normalized.Contains("int")) return "BIGINT";
        if (normalized.Contains("long")) return "BIGINT";
        if (normalized.Contains("short")) return "INTEGER";
        if (normalized.Contains("decimal")) return "NUMERIC";
        if (normalized.Contains("double")) return "DOUBLE PRECISION";
        if (normalized.Contains("float")) return "DOUBLE PRECISION";
        if (normalized.Contains("bool")) return "BOOLEAN";
        if (normalized.Contains("datetime") || normalized.Contains("timestamp")) return "TIMESTAMPTZ";
        if (normalized.Contains("date")) return "DATE";
        if (normalized.Contains("time")) return "TIME";
        return "TEXT";
    }

    private static string MapGeometryType(string sourceType)
    {
        var normalized = (sourceType ?? "GEOMETRY").ToLowerInvariant();
        if (normalized.Contains("multipoint")) return "MULTIPOINT";
        if (normalized.Contains("point")) return "POINT";
        if (normalized.Contains("multilinestring") || normalized.Contains("multicurve")) return "MULTILINESTRING";
        if (normalized.Contains("linestring") || normalized.Contains("curve")) return "LINESTRING";
        if (normalized.Contains("multipolygon") || normalized.Contains("multisurface")) return "MULTIPOLYGON";
        if (normalized.Contains("polygon") || normalized.Contains("surface")) return "POLYGON";
        return "GEOMETRY";
    }

    private static bool GeometryTypeMatches(string declared, string actual)
    {
        var d = MapGeometryType(declared);
        var a = MapGeometryType(actual);
        if (string.Equals(d, a, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (d == "GEOMETRY" || a == "GEOMETRY")
        {
            return true;
        }

        return false;
    }

    private static string BuildGeometryInsertExpression(string geometryType, int srid)
    {
        var setSrid = $"ST_SetSRID(ST_GeomFromGeoJSON(@geom), {srid})";
        var declared = MapGeometryType(geometryType);
        return declared switch
        {
            "MULTIPOLYGON" => $"ST_Multi(ST_CollectionExtract(ST_MakeValid({setSrid}), 3))",
            "MULTILINESTRING" => $"ST_Multi(ST_CollectionExtract(ST_MakeValid({setSrid}), 2))",
            "MULTIPOINT" => $"ST_Multi({setSrid})",
            _ => setSrid
        };
    }

    private static string ToSafeFailureReason(Exception ex)
        => ex switch
        {
            HttpRequestException => "WFS endpoint could not be reached or returned an unsupported response.",
            TaskCanceledException => "WFS request timed out.",
            JsonException => "WFS response could not be parsed.",
            NpgsqlException => "PostGIS write failed for this feature type.",
            FormatException => ex.Message,
            _ => "WFS feature type import failed."
        };

    private static OgcWfsImportResult CreateFailureResult(
        OgcWfsImportRequest request,
        string jobId,
        string errorMessage,
        TimeSpan duration,
        MigrationSourceInventoryArtifact? inventory = null)
    {
        var safeSourceUrl = ToSafeSourceUrl(request.ServiceUrl);
        inventory ??= new MigrationSourceInventoryArtifact
        {
            SourceKind = SourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = "WFS",
                BaseUrl = safeSourceUrl,
                Product = "OGC Web Feature Service",
                ServiceType = "WFS"
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = string.IsNullOrEmpty(request.Username) ? "anonymous" : "basic"
            },
            ScanCompleteness = new MigrationInventoryCompleteness
            {
                Status = "failed",
                Warnings = [errorMessage]
            },
            Summary = new MigrationInventorySummary(),
            OverallCompatibility = new MigrationCompatibilityAssessment
            {
                Level = "incompatible",
                Reason = errorMessage
            }
        };

        var manifest = MigrationManifestTranslator.Translate(inventory);
        return new OgcWfsImportResult
        {
            Success = false,
            WasDryRun = request.DryRun,
            SourceServiceUrl = safeSourceUrl,
            ErrorMessage = errorMessage,
            FeatureTypes = [],
            Warnings = [errorMessage],
            Manifest = manifest,
            ParityEvidence = MigrationParityEvidenceGenerator.Generate(inventory, manifest),
            Duration = duration
        };
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private sealed record FeaturePage(IReadOnlyList<JsonElement> Features, int? NumberMatched);

    private static partial class Log
    {
        [LoggerMessage(7900, LogLevel.Information,
            "Starting WFS import from {SafeUrl} (job {JobId}, dryRun {DryRun})")]
        public static partial void ImportStarting(ILogger logger, string safeUrl, string jobId, bool dryRun);

        [LoggerMessage(7901, LogLevel.Information,
            "WFS import complete for {SafeUrl}: imported {Imported}, skipped {Skipped}, features {Copied}/{Failed} in {ElapsedSeconds:F1}s")]
        public static partial void ImportCompleted(ILogger logger, string safeUrl, int imported, int skipped, int copied, int failed, double elapsedSeconds);

        [LoggerMessage(7902, LogLevel.Warning, "WFS inventory scan failed for {SafeUrl}")]
        public static partial void InventoryScanFailed(ILogger logger, string safeUrl, Exception exception);

        [LoggerMessage(7903, LogLevel.Warning, "WFS feature type import failed for {FeatureType}")]
        public static partial void FeatureTypeImportFailed(ILogger logger, string featureType, Exception exception);

        [LoggerMessage(7904, LogLevel.Information,
            "WFS table {Schema}.{Table} created")]
        public static partial void TableCreated(ILogger logger, string schema, string table);

        [LoggerMessage(7905, LogLevel.Information,
            "WFS batch {FeatureType} #{Page}: inserted {Inserted}, failed {Failed}, running total {RunningTotal}")]
        public static partial void BatchInserted(ILogger logger, string featureType, int page, int inserted, int failed, int runningTotal);

        [LoggerMessage(7906, LogLevel.Warning,
            "WFS feature insert produced {FailedCount} failures into {Table}. First error: {FirstError}")]
        public static partial void FeatureInsertFailures(ILogger logger, string table, int failedCount, string firstError);

        [LoggerMessage(7907, LogLevel.Information,
            "WFS import skipped data copy for existing table {Schema}.{Table}")]
        public static partial void IdempotentSkip(ILogger logger, string schema, string table);
    }
}
