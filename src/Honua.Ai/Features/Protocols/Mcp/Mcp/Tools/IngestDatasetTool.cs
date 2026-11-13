// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Security;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Ai.Protocols.Mcp.Location;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal sealed partial class IngestDatasetTool : IMcpTool
{
    public const string ToolName = "honua_ingest_dataset";

    // Payloads above this cap must use the REST upload, which streams instead of buffering JSON-RPC arguments.
    public const int MaxInlineDataBytes = 4 * 1024 * 1024;

    public const string GeocodeEntitlementKey = GeocodeAddressesTool.EntitlementKey;

    internal const string AddressColumnNotEntitledMessage =
        "'addressColumn' geocodes during ingest and requires the Enterprise edition ('" + GeocodeEntitlementKey
        + "' entitlement), which is not active on this server. Provide coordinate columns "
        + "('longitudeColumn'/'latitudeColumn') or a GeoJSON payload instead, or install a licence that includes '"
        + GeocodeEntitlementKey + "'.";

    public const string ImportedGeometryColumn = "geometry";

    public const string ImportedPrimaryKey = "id";

    private const int GeocodedSourceSrid = 4326;

    private const string CsvFormat = "csv";
    private const string GeoJsonFormat = "geojson";

    private readonly ILogger<IngestDatasetTool> _logger;

    public IngestDatasetTool(ILogger<IngestDatasetTool> logger)
    {
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Ingest dataset",
        Description = "Load inline GeoJSON or CSV (up to 4 MB) into a database table for honua_publish_service. "
            + "GeoJSON must be a FeatureCollection with WGS84 (EPSG:4326) lon/lat coordinates per RFC 7946. "
            + "CSV needs a header and longitude/latitude columns (auto-detected: lon/lng/long/longitude/x and lat/latitude/y; "
            + "override with longitudeColumn/latitudeColumn), or a WKT column named wkt/geom/geometry/shape. "
            + "Undeclared CRS requires sourceSrid. Omit targetSrid to keep the source CRS. "
            + "For address-only CSV, first use honua_geocode_addresses, or set addressColumn for server-side geocoding "
            + "(up to 100 rows; requires Enterprise 'geocoding.batch'; Community and Pro must use coordinates or GeoJSON). "
            + "Returns row count, per-row errors, and connectionId/schema/table/geometryColumn/primaryKey for honua_publish_service. "
            + "Re-ingesting the same datasetName replaces it. Above 4 MB, use POST /api/v1/admin/import/upload.",
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
        if (!await ImportAdminAuthorization.IsAuthorizedAsync(
                httpContext, principal, cancellationToken).ConfigureAwait(false))
        {
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                message: "Caller requires admin write authority to import a dataset.");
        }

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
                SourceSrid = validated.SourceSrid ?? (validated.AddressColumn is not null ? GeocodedSourceSrid : null),
                TargetSrid = validated.TargetSrid,
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

        var sourceSrid = argument.SourceSrid;
        if (sourceSrid is <= 0)
        {
            throw new GeoprocessingValidationException("'sourceSrid' must be a positive SRID/WKID.");
        }

        if (argument.TargetSrid is <= 0)
        {
            throw new GeoprocessingValidationException("'targetSrid' must be a positive SRID/WKID.");
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
            argument.TargetSrid,
            longitudeColumn,
            latitudeColumn,
            addressColumn);
    }

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
            throw new GeoprocessingPreconditionFailedException(AddressColumnNotEntitledMessage);
        }

        var allowFailover = LicenseGate
            .CheckEntitlement(httpContext.RequestServices, GeocodeTool.FailoverEntitlementKey)
            .IsActive;
        var sourceSrid = validated.SourceSrid ?? GeocodedSourceSrid;
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
                    var result = await coordinator
                        .ForwardGeocodeAsync(request, providerName: null, allowFailover, cancellationToken)
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
                catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
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
        var catalogConnection = await TryResolveCatalogConnectionIdAsync(services, cancellationToken).ConfigureAwait(false);
        if (catalogConnection.ConnectionId is null)
        {
            warnings.Add(catalogConnection.Ambiguous
                ? "More than one registered secure connection matches the server's catalog database, so 'connectionId' is null. "
                    + "Choose the intended registered connection name or id explicitly for honua_publish_service."
                : "No unique registered secure connection could be resolved for the server's catalog database, so 'connectionId' is null. "
                    + "Register a connection for the catalog database and pass its name or id as connectionId to honua_publish_service.");
        }

        return new McpIngestDatasetOutput
        {
            Success = true,
            DatasetName = validated.DatasetName,
            RowCount = importResult.FeatureCount,
            ConnectionId = catalogConnection.ConnectionId,
            Schema = importResult.Schema,
            Table = importResult.PhysicalTableName ?? importResult.TableName,
            Srid = importResult.StoredSrid,
            SourceSrid = importResult.SourceSrid,
            StoredSrid = importResult.StoredSrid,
            Reprojected = importResult.Reprojected,
            GeometryColumn = ImportedGeometryColumn,
            PrimaryKey = ImportedPrimaryKey,
            Warnings = warnings,
            RowErrors = rowErrors
        };
    }

    // Lookup failures return a CatalogConnectionMatch with a null connection id so the ingest can still succeed, but cancellation is not swallowed.
    private static async Task<CatalogConnectionMatch> TryResolveCatalogConnectionIdAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var configuration = services.GetService<IConfiguration>();
        var match = await CatalogConnectionDiscovery.FindUniqueAsync(
            services, configuration?["ConnectionStrings:DefaultConnection"], cancellationToken).ConfigureAwait(false);
        return new CatalogConnectionMatch(match.Connection?.Name, match.Ambiguous);
    }

    private readonly record struct CatalogConnectionMatch(string? ConnectionId, bool Ambiguous);

    private static string? NormalizeOption(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex DatasetNameRegex();

    private sealed record ValidatedIngestArguments(
        string Format,
        string Data,
        string DatasetName,
        int? SourceSrid,
        int? TargetSrid,
        string? LongitudeColumn,
        string? LatitudeColumn,
        string? AddressColumn);
}
