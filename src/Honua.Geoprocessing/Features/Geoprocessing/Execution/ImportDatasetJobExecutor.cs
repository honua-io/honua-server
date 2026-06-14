// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.ControlPlane;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>import.dataset</c> process (#1630).
///
/// Owns the full import pipeline as one durable, async GP job rather than the
/// half-orchestrated remote-import endpoint that only wrote a generic
/// <c>imported_&lt;table&gt;</c>. This executor ORCHESTRATES the existing
/// per-step services — it does not re-implement any of them — in this order:
///
/// <list type="number">
///   <item><description><b>fetch/stage</b> — resolves the catalog connection and validates the staged source path.</description></item>
///   <item><description><b>validate + chunk</b> — delegated to <see cref="IFileImportService"/>, which runs the
///     <c>ImportGeometrySizeGuard</c> vertex/ring guard per feature during streaming import (#1626).</description></item>
///   <item><description><b>import</b> — <see cref="IFileImportService.ImportFileAsync(ImportRequest, IProgress{ImportProgress}?, CancellationToken)"/>
///     streams the source into the generic <c>imported_&lt;table&gt;</c>.</description></item>
///   <item><description><b>flatten</b> — <see cref="ILayerPublishingService.PublishLayerAsync"/> introspects the imported
///     table and materializes the typed layer schema; the same call rebuilds the MVT materialization (#1628).</description></item>
///   <item><description><b>tile</b> — when a raster layer is supplied,
///     <see cref="IRasterImportService.ImportAsync"/> loads the raster, computes statistics, and pre-generates tiles/overviews (#1625).</description></item>
///   <item><description><b>extent + MVT refresh</b> — <see cref="ILayerPublishingService.RefreshLayerExtentsAsync"/>
///     recomputes the layer and service extents from the now-typed source.</description></item>
///   <item><description><b>provenance</b> — a structured provenance record is appended to the execution log and
///     published as a JSON data-URI artifact (source, table, layer, counts, timings).</description></item>
/// </list>
///
/// Like <c>sink.external-postgis</c>, the executor resolves provider services
/// (which live in the active data-provider assembly, not in Honua.Geoprocessing)
/// at execution time through an <see cref="IServiceScopeFactory"/> scope. The job
/// runs through the canonical durable substrate (ADR-0031), so progress polling,
/// heartbeats, retry, cancellation, and the #1624 in-memory single-node fallback
/// are inherited rather than reimplemented.
///
/// <para>
/// Scope realism (#1630): this lands the durable job SKELETON that runs every
/// step end-to-end in order with progress reporting and provenance. Full
/// mid-step resume (re-entering a partially-completed import after a crash)
/// re-runs from the staged source under <c>OverwriteExisting</c> idempotency
/// rather than checkpointing each row; finer-grained resume is deferred.
/// </para>
/// </summary>
internal sealed partial class ImportDatasetJobExecutor : IJobExecutor
{
    /// <summary>
    /// The single process id this executor handles. Matches the catalog entry
    /// in <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
    internal const string HandledProcessId = "import.dataset";

    private const string ProvenanceDataUriPrefix = "data:application/json;base64,";

    // The fixed shape honua.create_import_table materializes for the generic
    // imported_<table> (id SERIAL PRIMARY KEY, geometry GEOMETRY, properties JSONB).
    private const string ImportTableGeometryColumn = "geometry";
    private const string ImportTablePrimaryKey = "id";
    private static readonly string[] ImportTableFields = ["id", "properties"];

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<ImportDatasetJobExecutor> _logger;

    public ImportDatasetJobExecutor(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<ImportDatasetJobExecutor> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var resolvedProcessId = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);
        if (!string.Equals(resolvedProcessId, HandledProcessId, StringComparison.Ordinal))
        {
            Log.UnsupportedProcessId(_logger, job.OperationId, resolvedProcessId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{resolvedProcessId ?? "<none>"}' is not handled by the {HandledProcessId} executor.");
        }

        var inputs = new StepInputReader(job.Spec.Parameters);
        if (!TryReadInputs(inputs, out var request, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {inputError}");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var warnings = new List<string>();

        // Step 1 — fetch/stage. Resolve the catalog connection and confirm the
        // staged source file exists before doing any DB work.
        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Staging source", cancellationToken).ConfigureAwait(false);

        if (!File.Exists(request.SourcePath))
        {
            return JobExecutionResult.Failed(
                $"Invalid {HandledProcessId} inputs: staged source file was not found.");
        }

        using var scope = _serviceScopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var connectionResolver = services.GetService<ISecureConnectionResolver>();
        if (connectionResolver is null)
        {
            return JobExecutionResult.Failed($"{HandledProcessId} failed: secure connection resolver is not configured.");
        }

        string connectionString;
        try
        {
            connectionString = Guid.TryParse(request.ConnectionRef, out var connectionId)
                ? await connectionResolver.ResolveConnectionStringAsync(connectionId, cancellationToken).ConfigureAwait(false)
                : await connectionResolver.ResolveConnectionStringAsync(request.ConnectionRef, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ConnectionResolveFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"{HandledProcessId} failed: the target connection could not be resolved.");
        }

        var importService = services.GetService<IFileImportService>();
        var publishingService = services.GetService<ILayerPublishingService>();
        if (importService is null || publishingService is null)
        {
            return JobExecutionResult.Failed(
                $"{HandledProcessId} failed: import/publishing services are not configured for the active provider.");
        }

        // Step 2 + 3 — validate+chunk and import. The size guard (#1626) runs
        // inside the streaming importer per feature; the executor does not
        // duplicate that logic.
        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(15, "Validating and importing source", cancellationToken).ConfigureAwait(false);

        ImportResult importResult;
        try
        {
            // Pass the staged file by path (not an open stream): this mirrors the
            // admin upload path and keeps the import re-runnable from the staged
            // source under overwrite idempotency, which is the resume contract.
            var importRequest = new ImportRequest
            {
                LocalFilePath = request.SourcePath,
                FileName = request.FileName,
                TableName = request.TableName,
                TargetSchema = request.TargetSchema,
                SourceSrid = request.SourceSrid,
                TargetSrid = request.TargetSrid,
                OverwriteExisting = true,
                SourceKind = request.SourceUrl is null ? "file" : "url",
                SourceUrl = request.SourceUrl
            };

            importResult = await importService
                .ImportFileAsync(importRequest, progress: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ImportFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"{HandledProcessId} failed during import: {ex.GetType().Name}.");
        }

        if (!importResult.Success)
        {
            return JobExecutionResult.Failed(
                $"{HandledProcessId} import step failed: {importResult.ErrorMessage ?? importResult.ErrorCode ?? "unknown error"}.");
        }

        warnings.AddRange(importResult.Warnings);
        await context.ReportProgressAsync(45, "Imported source rows", cancellationToken).ConfigureAwait(false);

        // Step 4 — flatten to typed schema. PublishLayerAsync introspects the
        // imported table and materializes the typed layer; the same call rebuilds
        // the MVT materialization (#1628).
        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(55, "Flattening to typed layer", cancellationToken).ConfigureAwait(false);

        // The streaming importer is the single source of truth for the physical staging
        // table it actually created (provider-prefixed, e.g. imported_<table>) and the
        // operational-data schema that owns it (default honua_data). Publish exactly those
        // rather than reconstructing the provider's naming/schema convention here, which
        // would drift from the importer. Fall back to the logical name/resolved schema only
        // when an older provider does not surface the physical identifiers.
        var physicalTable = string.IsNullOrWhiteSpace(importResult.PhysicalTableName)
            ? importResult.TableName
            : importResult.PhysicalTableName!;
        var resolvedSchema = importResult.Schema is { Length: > 0 } importedSchema
            ? importedSchema
            : string.IsNullOrWhiteSpace(request.TargetSchema)
                ? ResolveOperationalSchema(services)
                : request.TargetSchema!;
        var detectedSrid = importResult.DetectedSrid ?? request.TargetSrid;

        PublishedLayerSummary publishedLayer;
        try
        {
            // The streaming importer materializes the generic imported_<table> with a
            // fixed shape (id SERIAL PRIMARY KEY, geometry GEOMETRY(Geometry, srid),
            // properties JSONB) via honua.create_import_table. Pass those known
            // conventions through to publish so the flatten step does not depend on
            // best-effort column auto-discovery; an explicit caller override still wins.
            var publishRequest = new LayerPublishRequest
            {
                Schema = resolvedSchema,
                Table = physicalTable,
                LayerName = request.LayerName,
                Description = request.Description,
                GeometryColumn = string.IsNullOrWhiteSpace(request.GeometryColumn)
                    ? ImportTableGeometryColumn
                    : request.GeometryColumn,
                PrimaryKey = ImportTablePrimaryKey,
                Fields = ImportTableFields,
                Srid = detectedSrid,
                ServiceName = request.ServiceName,
                ConnectionId = Guid.TryParse(request.ConnectionRef, out var pubConnectionId)
                    ? pubConnectionId
                    : null,
                Enabled = true
            };

            publishedLayer = await publishingService
                .PublishLayerAsync(connectionString, publishRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LayerPublishingException ex)
        {
            // LayerPublishingException carries a client-safe, sanitized message
            // (no SQL/provider internals), so it is safe to surface to the caller.
            Log.FlattenFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"{HandledProcessId} failed during flatten: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not surface raw provider/SQL exception text to callers; the full
            // exception is captured in the structured log for diagnosis.
            Log.FlattenFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"{HandledProcessId} failed during flatten: {ex.GetType().Name}.");
        }

        await context.ReportProgressAsync(70, "Published typed layer", cancellationToken).ConfigureAwait(false);

        // Step 5 — raster tiling/overviews (optional). Only runs when the source
        // is a raster and a raster import service is configured.
        var tilesGenerated = 0;
        if (request.RasterLayerId is { } rasterLayerId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(78, "Tiling raster", cancellationToken).ConfigureAwait(false);

            var rasterService = services.GetService<IRasterImportService>();
            if (rasterService is null)
            {
                warnings.Add("raster tiling was requested but no raster import service is configured; skipped.");
            }
            else
            {
                var rasterFormat = rasterService.DetectFormat(request.FileName);
                if (rasterFormat is null)
                {
                    warnings.Add($"raster tiling skipped: '{request.FileName}' is not a supported raster format.");
                }
                else
                {
                    try
                    {
                        var rasterResult = await rasterService.ImportAsync(
                            new RasterImportRequest
                            {
                                LayerId = rasterLayerId,
                                Name = request.LayerName,
                                Description = request.Description,
                                FilePath = request.SourcePath,
                                FileName = request.FileName,
                                Format = rasterFormat.Value,
                                Srid = request.SourceSrid
                            },
                            progress: null,
                            cancellationToken).ConfigureAwait(false);

                        if (rasterResult.Success)
                        {
                            tilesGenerated = rasterResult.TilesGenerated;
                            warnings.AddRange(rasterResult.Warnings);
                        }
                        else
                        {
                            return JobExecutionResult.Failed(
                                $"{HandledProcessId} failed during raster tiling: {rasterResult.ErrorMessage ?? "unknown error"}.");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.TileFailed(_logger, job.OperationId, ex);
                        return JobExecutionResult.Failed($"{HandledProcessId} failed during raster tiling: {ex.GetType().Name}.");
                    }
                }
            }
        }

        // Step 6 — extent + MVT materialization refresh. Recomputes the layer and
        // service extents from the now-typed source.
        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(88, "Refreshing extents", cancellationToken).ConfigureAwait(false);

        var serviceName = string.IsNullOrWhiteSpace(request.ServiceName) ? "default" : request.ServiceName!;
        try
        {
            await publishingService
                .RefreshLayerExtentsAsync(connectionString, serviceName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Extent refresh is a best-effort finishing step; a failure here does
            // not invalidate the imported/typed data, so surface it as a warning
            // rather than failing the whole job.
            Log.ExtentRefreshFailed(_logger, job.OperationId, ex);
            warnings.Add("extent refresh did not complete; layer extents may be stale until the next refresh.");
        }

        // Step 7 — provenance. Record the import lineage through the canonical
        // log + artifact substrate (no bespoke provenance schema).
        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(96, "Recording provenance", cancellationToken).ConfigureAwait(false);

        var completedAt = DateTimeOffset.UtcNow;
        var provenance = BuildProvenance(
            job.OperationId,
            request,
            importResult,
            physicalTable,
            publishedLayer,
            tilesGenerated,
            startedAt,
            completedAt,
            warnings);

        await context.AppendLogAsync(
            new ExecutionLogEntry
            {
                Timestamp = completedAt,
                Level = ExecutionLogLevel.Info,
                Message = $"Imported '{request.FileName}' into layer {publishedLayer.LayerId} "
                    + $"({importResult.FeatureCount} features, {tilesGenerated} raster tiles).",
                Phase = "Provenance",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sourceFile"] = request.FileName,
                    ["table"] = physicalTable,
                    ["layerId"] = publishedLayer.LayerId.ToString(CultureInfo.InvariantCulture),
                    ["serviceName"] = serviceName,
                    ["featureCount"] = importResult.FeatureCount.ToString(CultureInfo.InvariantCulture),
                    ["tilesGenerated"] = tilesGenerated.ToString(CultureInfo.InvariantCulture)
                }
            },
            cancellationToken).ConfigureAwait(false);

        await context.PublishArtifactAsync(provenance, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Import completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded(warnings.Count > 0 ? warnings : null);
    }

    private static bool TryReadInputs(StepInputReader inputs, out ImportDatasetInputs request, out string error)
    {
        request = default!;

        if (!inputs.TryGetRequired("connection", out var connectionRef, out var connectionError))
        {
            error = connectionError;
            return false;
        }

        if (!inputs.TryGetRequired("sourcePath", out var sourcePath, out var sourcePathError))
        {
            error = sourcePathError;
            return false;
        }

        if (!inputs.TryGetRequired("fileName", out var fileName, out var fileNameError))
        {
            error = fileNameError;
            return false;
        }

        if (!inputs.TryGetRequired("tableName", out var tableName, out var tableNameError))
        {
            error = tableNameError;
            return false;
        }

        if (!inputs.TryGetRequired("layerName", out var layerName, out var layerNameError))
        {
            error = layerNameError;
            return false;
        }

        int? sourceSrid = null;
        if (inputs.TryGet("sourceSrid", out var sourceSridRaw)
            && int.TryParse(sourceSridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSourceSrid))
        {
            sourceSrid = parsedSourceSrid;
        }

        var targetSrid = 4326;
        if (inputs.TryGet("targetSrid", out var targetSridRaw)
            && int.TryParse(targetSridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTargetSrid)
            && parsedTargetSrid > 0)
        {
            targetSrid = parsedTargetSrid;
        }

        int? rasterLayerId = null;
        if (inputs.TryGet("rasterLayerId", out var rasterLayerIdRaw)
            && int.TryParse(rasterLayerIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRasterLayerId)
            && parsedRasterLayerId > 0)
        {
            rasterLayerId = parsedRasterLayerId;
        }

        request = new ImportDatasetInputs
        {
            ConnectionRef = connectionRef,
            SourcePath = sourcePath,
            FileName = fileName,
            TableName = tableName,
            LayerName = layerName,
            Description = inputs.TryGet("description", out var description) ? description : null,
            GeometryColumn = inputs.TryGet("geometryColumn", out var geometryColumn) ? geometryColumn : null,
            ServiceName = inputs.TryGet("serviceName", out var serviceName) ? serviceName : null,
            TargetSchema = inputs.TryGet("targetSchema", out var targetSchema) ? targetSchema : null,
            SourceUrl = inputs.TryGet("sourceUrl", out var sourceUrl) ? sourceUrl : null,
            SourceSrid = sourceSrid,
            TargetSrid = targetSrid,
            RasterLayerId = rasterLayerId
        };

        error = "";
        return true;
    }

    private static string ResolveOperationalSchema(IServiceProvider services)
    {
        // Mirror PostgresSchemaConfiguration.FromConfiguration: imports default
        // to the configured operational-data schema, falling back to honua_data.
        const string defaultDataSchema = "honua_data";
        var configuration = services.GetService<IConfiguration>();
        var configured = configuration?["Database:DefaultOperationalSchema"];
        return string.IsNullOrWhiteSpace(configured) ? defaultDataSchema : configured.Trim();
    }

    private static string BuildProvenance(
        string operationId,
        ImportDatasetInputs request,
        ImportResult importResult,
        string physicalTable,
        PublishedLayerSummary publishedLayer,
        int tilesGenerated,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        IReadOnlyList<string> warnings)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "ImportProvenance");
            writer.WriteString("processId", HandledProcessId);
            writer.WriteString("operationId", operationId);
            writer.WriteString("sourceFile", request.FileName);
            if (request.SourceUrl is not null)
            {
                writer.WriteString("sourceUrl", request.SourceUrl);
            }

            writer.WriteString("importedTable", physicalTable);
            writer.WriteString("format", importResult.Format.ToString());
            writer.WriteNumber("featureCount", importResult.FeatureCount);
            if (importResult.DetectedSrid is { } detectedSrid)
            {
                writer.WriteNumber("detectedSrid", detectedSrid);
            }

            writer.WriteNumber("layerId", publishedLayer.LayerId);
            writer.WriteString("layerName", publishedLayer.LayerName);
            writer.WriteString("serviceName", string.IsNullOrWhiteSpace(request.ServiceName) ? "default" : request.ServiceName!);
            writer.WriteNumber("tilesGenerated", tilesGenerated);
            writer.WriteString("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("completedAt", completedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("durationSeconds", (completedAt - startedAt).TotalSeconds);

            writer.WriteStartArray("warnings");
            foreach (var warning in warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var sb = new StringBuilder(ProvenanceDataUriPrefix.Length + ((int)buffer.Length * 2));
        sb.Append(ProvenanceDataUriPrefix);
        sb.Append(Convert.ToBase64String(buffer.ToArray()));
        return sb.ToString();
    }

    private sealed record ImportDatasetInputs
    {
        public required string ConnectionRef { get; init; }

        public required string SourcePath { get; init; }

        public required string FileName { get; init; }

        public required string TableName { get; init; }

        public required string LayerName { get; init; }

        public string? Description { get; init; }

        public string? GeometryColumn { get; init; }

        public string? ServiceName { get; init; }

        public string? TargetSchema { get; init; }

        public string? SourceUrl { get; init; }

        public int? SourceSrid { get; init; }

        public int TargetSrid { get; init; }

        public int? RasterLayerId { get; init; }
    }

    private static partial class Log
    {
        [LoggerMessage(9200, LogLevel.Warning,
            "Import dataset executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9201, LogLevel.Warning,
            "Import dataset executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9202, LogLevel.Error,
            "Import dataset executor failed job {OperationId}: connection resolution failed")]
        public static partial void ConnectionResolveFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9203, LogLevel.Error,
            "Import dataset executor failed job {OperationId} during import")]
        public static partial void ImportFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9204, LogLevel.Error,
            "Import dataset executor failed job {OperationId} during flatten/publish")]
        public static partial void FlattenFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9205, LogLevel.Error,
            "Import dataset executor failed job {OperationId} during raster tiling")]
        public static partial void TileFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9206, LogLevel.Warning,
            "Import dataset executor job {OperationId}: extent refresh did not complete")]
        public static partial void ExtentRefreshFailed(ILogger logger, string operationId, Exception exception);
    }
}
