// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Licensing;
using Honua.Ai.Protocols.Mcp.Location;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that loads a small inline CSV or GeoJSON dataset into the catalog
/// database as a table that <see cref="PublishServiceTool"/> can publish. Thin
/// adapter over the canonical <see cref="IFileImportService"/> import pipeline
/// (the same streaming importer the REST admin import surface uses) — the MCP
/// surface never grows its own format readers. CSV geometry options
/// (explicit longitude/latitude columns or a server-geocoded address column)
/// ride through <see cref="CsvImportOptions"/> on the shared pipeline; address
/// geocoding routes through the canonical
/// <see cref="IGeocodeCoordinatorService"/>.
/// </summary>
internal sealed partial class IngestDatasetTool : IMcpTool
{
    public const string ToolName = "honua_ingest_dataset";

    /// <summary>
    /// Inline payload cap. Larger datasets must use the REST import upload
    /// surface, which streams multipart bodies instead of buffering JSON-RPC
    /// arguments in memory.
    /// </summary>
    public const int MaxInlineDataBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Entitlement key required when <c>addressColumn</c> is used (the ingest
    /// then performs batch geocoding); mirrors <see cref="GeocodeAddressesTool.EntitlementKey"/>.
    /// </summary>
    public const string GeocodeEntitlementKey = GeocodeAddressesTool.EntitlementKey;

    /// <summary>
    /// Geometry column of the imported staging table (fixed shape:
    /// <c>id SERIAL PRIMARY KEY, geometry, properties JSONB</c>) — pass to
    /// <c>honua_publish_service</c> as <c>geometryColumn</c>.
    /// </summary>
    public const string ImportedGeometryColumn = "geometry";

    /// <summary>Primary key column of the imported staging table.</summary>
    public const string ImportedPrimaryKey = "id";

    private const string CsvFormat = "csv";
    private const string GeoJsonFormat = "geojson";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<IngestDatasetTool> _logger;

    public IngestDatasetTool(IGeoprocessingJobService jobService, ILogger<IngestDatasetTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Ingest dataset",
        Description = "Load a small inline dataset (up to 4 MB) into the server as a database table that honua_publish_service can immediately "
            + "publish as a queryable layer. Accepts format \"geojson\" (a GeoJSON FeatureCollection; coordinates are WGS84 lon/lat per RFC 7946) "
            + "or \"csv\" (text with a header row; point geometry is read from longitude/latitude columns — auto-detected names "
            + "lon/lng/long/longitude/x and lat/latitude/y, or set longitudeColumn/latitudeColumn explicitly — or from a WKT column named "
            + "wkt/geom/geometry/shape; coordinate order is lon/lat and the coordinate CRS defaults to EPSG:4326, override with sourceSrid). "
            + "For a CSV that only has addresses, either geocode it first with honua_geocode_addresses and ingest the returned lon/lat values, or "
            + "set addressColumn to geocode server-side during ingest (up to 100 rows; requires the 'geocoding.batch' entitlement). "
            + "Returns the {connectionId, schema, table} triple plus row count and per-row errors — pass those values straight to "
            + "honua_publish_service (with the returned geometryColumn and primaryKey) to finish the CSV → geocode → ingest → publish workflow. "
            + "Re-ingesting the same datasetName replaces that dataset. For data larger than 4 MB use the REST upload path "
            + "(POST /api/v1/admin/import/upload).",
        InputSchema = McpToolSchemas.IngestDatasetArgumentSchema,
        OutputSchema = McpToolOutputSchemas.IngestDatasetOutputSchema,
        // Write tool: it creates (or replaces) a table in the catalog database.
        // Not destructive (it loads data rather than destroying published state).
        // Not idempotent: a replay re-runs the import (and any address geocoding)
        // and replaces the staged dataset.
        Annotations = McpToolAnnotationSets.Write("Ingest dataset", destructive: false, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("IngestDataset");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Workspace, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpIngestDatasetArgument);
        var validated = ValidateArguments(argument);

        var importService = httpContext.RequestServices.GetService<IFileImportService>();
        if (importService is null)
        {
            return McpToolHelpers.SuccessResult(
                new McpIngestDatasetOutput
                {
                    Success = false,
                    DatasetName = validated.DatasetName,
                    ErrorMessage = "The dataset import service is unavailable (no IFileImportService is registered in this composition)."
                },
                McpJsonContext.Default.McpIngestDatasetOutput);
        }

        var csvOptions = BuildCsvOptions(httpContext, validated);

        ImportResult importResult;
        using (var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(validated.Data), writable: false))
        {
            var importRequest = new ImportRequest
            {
                FileStream = dataStream,
                FileName = $"{validated.DatasetName}.{validated.Format}",
                TableName = validated.DatasetName,
                SourceKind = "mcp-inline",
                SourceSrid = validated.SourceSrid,
                TargetSrid = 4326,
                OverwriteExisting = true,
                CsvOptions = csvOptions
            };

            importResult = await importService.ImportFileAsync(importRequest, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(importResult.ErrorCode, ImportValidationErrorCodes.CsvOptionsInvalid, StringComparison.Ordinal))
        {
            // Option problems (missing columns, geocode row cap) are argument-shaped:
            // surface them as structured invalid_argument so the agent fixes the call.
            throw new GeoprocessingValidationException(
                importResult.ErrorMessage ?? "The CSV import options could not be applied.");
        }

        var output = await ProjectAsync(importResult, validated, httpContext.RequestServices, cancellationToken)
            .ConfigureAwait(false);
        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpIngestDatasetOutput);
    }

    private static ValidatedIngestArguments ValidateArguments(McpIngestDatasetArgument argument)
    {
        var format = argument.Format?.Trim().ToLowerInvariant();
        if (format is not (CsvFormat or GeoJsonFormat))
        {
            throw new GeoprocessingValidationException("'format' is required and must be \"csv\" or \"geojson\".");
        }

        if (string.IsNullOrWhiteSpace(argument.Data))
        {
            throw new GeoprocessingValidationException("'data' is required and must contain the inline dataset content.");
        }

        var dataByteCount = Encoding.UTF8.GetByteCount(argument.Data);
        if (dataByteCount > MaxInlineDataBytes)
        {
            throw new GeoprocessingValidationException(
                $"'data' is {dataByteCount:N0} bytes, above the {MaxInlineDataBytes:N0}-byte inline cap for this tool. "
                + "Upload larger datasets through the REST import surface (POST /api/v1/admin/import/upload) instead, "
                + "then publish the resulting table with honua_publish_service.");
        }

        var datasetName = argument.DatasetName?.Trim();
        if (string.IsNullOrWhiteSpace(datasetName) || datasetName.Length > 63 || !DatasetNameRegex().IsMatch(datasetName))
        {
            throw new GeoprocessingValidationException(
                "'datasetName' is required and must be 1-63 characters of letters, digits, or underscores, not starting with a digit.");
        }

        var sourceSrid = argument.SourceSrid ?? 4326;
        if (sourceSrid <= 0)
        {
            throw new GeoprocessingValidationException("'sourceSrid' must be a positive SRID/WKID.");
        }

        var longitudeColumn = NormalizeOption(argument.LongitudeColumn);
        var latitudeColumn = NormalizeOption(argument.LatitudeColumn);
        var addressColumn = NormalizeOption(argument.AddressColumn);

        if (format is GeoJsonFormat && (longitudeColumn is not null || latitudeColumn is not null || addressColumn is not null))
        {
            throw new GeoprocessingValidationException(
                "'longitudeColumn', 'latitudeColumn', and 'addressColumn' apply to CSV data only; a GeoJSON FeatureCollection carries its own geometry.");
        }

        if (addressColumn is not null && (longitudeColumn is not null || latitudeColumn is not null))
        {
            throw new GeoprocessingValidationException(
                "'addressColumn' and 'longitudeColumn'/'latitudeColumn' are mutually exclusive; specify one geometry source.");
        }

        if ((longitudeColumn is not null) != (latitudeColumn is not null))
        {
            throw new GeoprocessingValidationException(
                "'longitudeColumn' and 'latitudeColumn' must be specified together.");
        }

        return new ValidatedIngestArguments(
            format,
            argument.Data,
            datasetName,
            sourceSrid,
            longitudeColumn,
            latitudeColumn,
            addressColumn);
    }

    /// <summary>
    /// Builds the <see cref="CsvImportOptions"/> for the shared import pipeline.
    /// When an address column is requested, wires the canonical geocoding
    /// coordinator in as the pipeline's address resolver (entitlement-gated the
    /// same way as <see cref="GeocodeAddressesTool"/>).
    /// </summary>
    private static CsvImportOptions? BuildCsvOptions(HttpContext httpContext, ValidatedIngestArguments validated)
    {
        if (validated.Format is not CsvFormat)
        {
            return null;
        }

        if (validated.AddressColumn is null)
        {
            return validated.LongitudeColumn is null
                ? null
                : new CsvImportOptions
                {
                    LongitudeColumn = validated.LongitudeColumn,
                    LatitudeColumn = validated.LatitudeColumn
                };
        }

        // IGeocodeCoordinatorService is scoped, so it is resolved from the request
        // scope; the closure below runs within this request's lifetime only.
        var coordinator = httpContext.RequestServices.GetService<IGeocodeCoordinatorService>();
        if (coordinator is null)
        {
            throw new GeoprocessingPreconditionFailedException(
                "Address geocoding is not available on this server, so 'addressColumn' cannot be used. "
                + "Provide coordinate columns (or a GeoJSON payload) instead.");
        }

        var entitlement = LicenseGate.CheckEntitlement(httpContext.RequestServices, GeocodeEntitlementKey);
        if (!entitlement.IsActive)
        {
            throw new GeoprocessingPreconditionFailedException(entitlement.UpgradeMessage);
        }

        var sourceSrid = validated.SourceSrid;
        return new CsvImportOptions
        {
            AddressColumn = validated.AddressColumn,
            MaxGeocodedRows = LocationToolSchemas.MaxBatchAddresses,
            AddressGeocoder = async (address, cancellationToken) =>
            {
                try
                {
                    var request = new ForwardGeocodeRequest(
                        Query: address,
                        MaxResults: 1,
                        SpatialReferenceWkid: sourceSrid);
                    var result = await coordinator.ForwardGeocodeAsync(request, providerName: null, cancellationToken)
                        .ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return null;
                    }

                    var candidate = result.Data.FirstOrDefault(static c => c.IsMatch && !double.IsNaN(c.X) && !double.IsNaN(c.Y));
                    return candidate is null ? null : new CsvGeocodedAddress(candidate.X, candidate.Y);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // Intentionally generic: this is a per-row best-effort geocode call during a bulk
                // CSV import; a provider blow-up marks this row as not geocoded (surfaced as a
                // per-row issue) instead of failing the whole import.
                catch (Exception)
                {
                    return null;
                }
            }
        };
    }

    private static async Task<McpIngestDatasetOutput> ProjectAsync(
        ImportResult importResult,
        ValidatedIngestArguments validated,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var rowErrors = importResult.ValidationErrors.Select(static issue => new McpIngestRowError
        {
            Row = issue.FeatureIndex is { } index ? index + 1 : null,
            Code = issue.Code,
            Message = issue.Message,
            Field = issue.Field
        }).ToList();

        if (!importResult.Success)
        {
            return new McpIngestDatasetOutput
            {
                Success = false,
                DatasetName = validated.DatasetName,
                Warnings = importResult.Warnings,
                RowErrors = rowErrors,
                ErrorCode = importResult.ErrorCode,
                ErrorMessage = importResult.ErrorMessage
            };
        }

        var warnings = new List<string>(importResult.Warnings);
        var connectionId = await TryResolveCatalogConnectionIdAsync(services, cancellationToken).ConfigureAwait(false);
        if (connectionId is null)
        {
            warnings.Add(
                "No registered secure connection matches the server's catalog database, so 'connectionId' is null. "
                + "Register a connection for the catalog database and pass its name or id as connectionId to honua_publish_service.");
        }

        return new McpIngestDatasetOutput
        {
            Success = true,
            DatasetName = validated.DatasetName,
            RowCount = importResult.FeatureCount,
            ConnectionId = connectionId,
            Schema = importResult.Schema,
            Table = importResult.PhysicalTableName ?? importResult.TableName,
            Srid = importResult.DetectedSrid ?? 4326,
            GeometryColumn = ImportedGeometryColumn,
            PrimaryKey = ImportedPrimaryKey,
            Warnings = warnings,
            RowErrors = rowErrors
        };
    }

    /// <summary>
    /// Best-effort resolution of the registered secure connection that points at
    /// the catalog database the import wrote to, so the agent can chain the
    /// result straight into <c>honua_publish_service</c> (whose executor requires
    /// a registered connection name or GUID). The import pipeline always writes
    /// through the server's default catalog connection, so this matches the
    /// registered connections against the configured default connection string.
    /// Never fails the ingest: returns null when no match can be determined.
    /// </summary>
    private static async Task<string?> TryResolveCatalogConnectionIdAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var resolver = services.GetService<ISecureConnectionResolver>();
            var configuration = services.GetService<IConfiguration>();
            var defaultConnectionString = configuration?["ConnectionStrings:DefaultConnection"];
            if (resolver is null || string.IsNullOrWhiteSpace(defaultConnectionString))
            {
                return null;
            }

            var normalizedDefault = NormalizeConnectionString(defaultConnectionString);
            var names = await resolver.GetAvailableConnectionsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var connectionString = await resolver.ResolveConnectionStringAsync(name, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(NormalizeConnectionString(connectionString), normalizedDefault, StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A single unresolvable connection (missing secret, bad reference)
                    // must not abort the scan for a matching one.
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The connection hint is best-effort; the ingest result stays valid
            // without it (the output carries an explanatory warning instead).
            return null;
        }
    }

    private static string NormalizeConnectionString(string connectionString)
    {
        var segments = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.OrdinalIgnoreCase);
        return string.Join(';', segments);
    }

    private static string? NormalizeOption(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex DatasetNameRegex();

    private sealed record ValidatedIngestArguments(
        string Format,
        string Data,
        string DatasetName,
        int SourceSrid,
        string? LongitudeColumn,
        string? LatitudeColumn,
        string? AddressColumn);
}
