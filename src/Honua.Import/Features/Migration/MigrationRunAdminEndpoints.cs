// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.Http;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Migration;

internal static partial class MigrationRunAdminEndpoints
{
    private const int DefaultPageLimit = 25;
    private const int MaxPageLimit = 100;

    public static void MapMigrationRunAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/migration/runs")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Import")
            .RequireAdminAuthorization();

        _ = group.MapGet("/", HandleListAsync)
            .WithName("ListMigrationRuns")
            .WithSummary("List GeoServer migration runs (most recent first, paged)");

        _ = group.MapPost("/", HandleRecordStartedAsync)
            .WithName("RecordMigrationRunStarted")
            .WithSummary("Record the start of a migration run");

        _ = group.MapGet("/{runId}", HandleGetAsync)
            .WithName("GetMigrationRun")
            .WithSummary("Return a single GeoServer migration run by id");

        _ = group.MapGet("/{runId}/evidence-pack", HandleGetEvidencePackAsync)
            .WithName("GetMigrationRunEvidencePack")
            .WithSummary("Download the slice-4 evidence pack JSON for a migration run");

        _ = group.MapGet("/{runId}/scorecard", HandleGetScorecardAsync)
            .WithName("GetMigrationRunScorecard")
            .WithSummary("Download the signed reconciliation scorecard JSON for a migration run");

        _ = group.MapPost("/{runId}/complete", HandleCompleteAsync)
            .WithName("RecordMigrationRunCompleted")
            .WithSummary("Record the terminal state of a migration run");

        _ = group.MapPost("/{runId}/scorecard", HandleRecordScorecardAsync)
            .WithName("RecordMigrationRunScorecard")
            .WithSummary("Persist the signed reconciliation scorecard JSON for a migration run");

        _ = group.MapPost("/{runId}/cancel", HandleCancelAsync)
            .WithName("CancelMigrationRun")
            .WithSummary("Mark a running migration run as cancelled");
    }

    private static async Task HandleListAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var catalog = context.RequestServices.GetRequiredService<IMigrationRunCatalog>();

        var query = ParseListQuery(context.Request.Query, out var error);
        if (error is not null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, error, StatusCodes.Status400BadRequest);
            return;
        }

        var page = await catalog.ListAsync(query!, cancellationToken).ConfigureAwait(false);
        var response = new MigrationRunListResponse
        {
            Items = page.Items.Select(ToDto).ToArray(),
            TotalCount = page.TotalCount,
            Limit = page.Limit,
            Offset = page.Offset
        };

        await Results.Json(response, ImportJsonContext.Default.MigrationRunListResponse)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleRecordStartedAsync(HttpContext context)
    {
        MigrationRunStartRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.MigrationRunStartRequest,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: ReadFromJsonAsync can throw JsonException, NotSupportedException,
        // or IOException for malformed/unreadable request bodies; map all of them to a 400 response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.RequestBodyReadFailed(GetLogger(context), "RecordMigrationRunStarted", ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body.", StatusCodes.Status400BadRequest);
            return;
        }

        if (request is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required.", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "runId is required.", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.SourceKind))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "sourceKind is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var record = new MigrationRunRecord
        {
            RunId = request.RunId.Trim(),
            SourceKind = request.SourceKind.Trim(),
            SourceUrl = TrimToEmpty(request.SourceUrl),
            SourceDisplayName = TrimToNull(request.SourceDisplayName),
            Status = MigrationRunStatus.Running,
            StartedAt = request.StartedAt ?? DateTimeOffset.UtcNow,
            StatusNote = TrimToNull(request.StatusNote)
        };

        var catalog = context.RequestServices.GetRequiredService<IMigrationRunCatalog>();
        var persisted = await catalog.RecordStartedAsync(record, context.RequestAborted).ConfigureAwait(false);

        await Results.Json(ToDto(persisted), ImportJsonContext.Default.MigrationRunDto)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleGetAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var runId = context.Request.RouteValues["runId"]?.ToString();
        if (string.IsNullOrWhiteSpace(runId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Run id is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<IMigrationRunCatalog>();
        var record = await catalog.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, $"Migration run '{runId}' was not found.", StatusCodes.Status404NotFound);
            return;
        }

        await Results.Json(ToDto(record), ImportJsonContext.Default.MigrationRunDto)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleGetEvidencePackAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var runId = context.Request.RouteValues["runId"]?.ToString();
        if (string.IsNullOrWhiteSpace(runId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Run id is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<IMigrationRunCatalog>();
        var record = await catalog.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, $"Migration run '{runId}' was not found.", StatusCodes.Status404NotFound);
            return;
        }

        var body = await catalog.GetEvidencePackAsync(runId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                $"Migration run '{runId}' has no evidence pack persisted yet (status={MigrationRunSerialization.StatusToText(record.Status)}).",
                StatusCodes.Status404NotFound);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        var filename = $"migration-run-{MigrationScannerEndpoints.SanitizeFilenameSlug(record.RunId)}-evidence-pack.json";

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!string.IsNullOrEmpty(record.EvidencePackFingerprint))
        {
            // Quoted ETag per RFC 7232 so HTTP caches treat the fingerprint
            // as opaque rather than weak.
            context.Response.Headers.ETag = $"\"{record.EvidencePackFingerprint}\"";
        }

        await context.Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task HandleGetScorecardAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var runId = context.Request.RouteValues["runId"]?.ToString();
        if (string.IsNullOrWhiteSpace(runId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Run id is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<IMigrationRunCatalog>();
        var record = await catalog.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, $"Migration run '{runId}' was not found.", StatusCodes.Status404NotFound);
            return;
        }

        var body = await catalog.GetScorecardAsync(runId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                $"Migration run '{runId}' has no reconciliation scorecard persisted yet (status={MigrationRunSerialization.StatusToText(record.Status)}).",
                StatusCodes.Status404NotFound);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        var filename = $"migration-run-{MigrationScannerEndpoints.SanitizeFilenameSlug(record.RunId)}-reconciliation-scorecard.json";

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!string.IsNullOrEmpty(record.ReconciliationScorecardFingerprint))
        {
            // Quoted ETag per RFC 7232 so HTTP caches treat the fingerprint as opaque.
            context.Response.Headers.ETag = $"\"{record.ReconciliationScorecardFingerprint}\"";
        }

        await context.Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task HandleCompleteAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var runId = context.Request.RouteValues["runId"]?.ToString();
        if (string.IsNullOrWhiteSpace(runId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Run id is required.", StatusCodes.Status400BadRequest);
            return;
        }

        MigrationRunCompletionRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.MigrationRunCompletionRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: ReadFromJsonAsync can throw JsonException, NotSupportedException,
        // or IOException for malformed/unreadable request bodies; map all of them to a 400 response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.RequestBodyReadFailedForRun(GetLogger(context), "RecordMigrationRunCompleted", runId, ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body.", StatusCodes.Status400BadRequest);
            return;
        }

        if (request is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required.", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Status)
            || !MigrationRunSerialization.TryParseStatus(request.Status, out var status)
            || status == MigrationRunStatus.Running)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "status must be one of: succeeded, failed, cancelled.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.EvidencePackBody) && !IsValidJson(request.EvidencePackBody!))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "evidencePackBody must be valid JSON when supplied.",
                StatusCodes.Status400BadRequest);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<IMigrationRunCatalog>();
        var completed = await catalog.RecordCompletedAsync(
            runId,
            status,
            request.CompletedAt ?? DateTimeOffset.UtcNow,
            TrimToNull(request.EvidencePackRef),
            TrimToNull(request.EvidencePackFingerprint),
            string.IsNullOrWhiteSpace(request.EvidencePackBody) ? null : request.EvidencePackBody,
            TrimToNull(request.StatusNote),
            cancellationToken).ConfigureAwait(false);

        if (completed is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, $"Migration run '{runId}' was not found.", StatusCodes.Status404NotFound);
            return;
        }

        await Results.Json(ToDto(completed), ImportJsonContext.Default.MigrationRunDto)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleRecordScorecardAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var runId = context.Request.RouteValues["runId"]?.ToString();
        if (string.IsNullOrWhiteSpace(runId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Run id is required.", StatusCodes.Status400BadRequest);
            return;
        }

        MigrationReconciliationScorecard? scorecard;
        try
        {
            scorecard = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.MigrationReconciliationScorecard,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: ReadFromJsonAsync can throw JsonException, NotSupportedException,
        // or IOException for malformed/unreadable request bodies; map all of them to a 400 response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.RequestBodyReadFailedForRun(GetLogger(context), "RecordMigrationRunScorecard", runId, ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body.", StatusCodes.Status400BadRequest);
            return;
        }

        if (scorecard is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required.", StatusCodes.Status400BadRequest);
            return;
        }

        if (!string.Equals(scorecard.RunId, runId, StringComparison.Ordinal))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "scorecard.runId must match the route run id.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(scorecard.Fingerprint))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "scorecard.fingerprint is required.",
                StatusCodes.Status400BadRequest);
            return;
        }

        var scorecardBody = JsonSerializer.Serialize(
            scorecard,
            ImportJsonContext.Default.MigrationReconciliationScorecard);

        var catalog = context.RequestServices.GetRequiredService<IMigrationRunCatalog>();
        var updated = await catalog.RecordScorecardAsync(
            runId,
            scorecard.Fingerprint,
            scorecardBody,
            cancellationToken).ConfigureAwait(false);

        if (updated is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, $"Migration run '{runId}' was not found.", StatusCodes.Status404NotFound);
            return;
        }

        await Results.Json(ToDto(updated), ImportJsonContext.Default.MigrationRunDto)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleCancelAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var runId = context.Request.RouteValues["runId"]?.ToString();
        if (string.IsNullOrWhiteSpace(runId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Run id is required.", StatusCodes.Status400BadRequest);
            return;
        }

        MigrationRunCancelRequest? request = null;
        if (context.Request.ContentLength.GetValueOrDefault() > 0)
        {
            try
            {
                request = await context.Request.ReadFromJsonAsync(
                    ImportJsonContext.Default.MigrationRunCancelRequest,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            // Intentionally generic: ReadFromJsonAsync can throw JsonException, NotSupportedException,
            // or IOException for malformed/unreadable request bodies; map all of them to a 400 response.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.RequestBodyReadFailedForRun(GetLogger(context), "CancelMigrationRun", runId, ex);
                await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body.", StatusCodes.Status400BadRequest);
                return;
            }
        }

        var catalog = context.RequestServices.GetRequiredService<IMigrationRunCatalog>();
        var existing = await catalog.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, $"Migration run '{runId}' was not found.", StatusCodes.Status404NotFound);
            return;
        }

        if (existing.Status != MigrationRunStatus.Running)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                $"Migration run '{runId}' is already terminal (status={MigrationRunSerialization.StatusToText(existing.Status)}). Cancel is a no-op.",
                StatusCodes.Status409Conflict);
            return;
        }

        var cancelled = await catalog.CancelAsync(
            runId,
            DateTimeOffset.UtcNow,
            request?.Reason,
            cancellationToken).ConfigureAwait(false);

        if (cancelled is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, $"Migration run '{runId}' was not found.", StatusCodes.Status404NotFound);
            return;
        }

        await Results.Json(ToDto(cancelled), ImportJsonContext.Default.MigrationRunDto)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static MigrationRunListQuery ParseListQuery(IQueryCollection query, out string? error)
    {
        error = null;
        var limit = DefaultPageLimit;
        var offset = 0;
        string? sourceKind = null;
        MigrationRunStatus? status = null;

        if (query.TryGetValue("limit", out var limitValues) && limitValues.Count > 0 && !string.IsNullOrWhiteSpace(limitValues[0]))
        {
            if (!int.TryParse(limitValues[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit <= 0)
            {
                error = "limit must be a positive integer.";
                return new MigrationRunListQuery();
            }
            limit = Math.Min(limit, MaxPageLimit);
        }

        if (query.TryGetValue("offset", out var offsetValues) && offsetValues.Count > 0 && !string.IsNullOrWhiteSpace(offsetValues[0]) &&
            (!int.TryParse(offsetValues[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0))
        {
            error = "offset must be a non-negative integer.";
            return new MigrationRunListQuery();
        }

        if (query.TryGetValue("sourceKind", out var sourceValues) && sourceValues.Count > 0 && !string.IsNullOrWhiteSpace(sourceValues[0]))
        {
            sourceKind = sourceValues[0];
        }

        if (query.TryGetValue("status", out var statusValues) && statusValues.Count > 0 && !string.IsNullOrWhiteSpace(statusValues[0]))
        {
            if (!MigrationRunSerialization.TryParseStatus(statusValues[0]!, out var parsedStatus))
            {
                error = "status must be one of: running, succeeded, failed, cancelled.";
                return new MigrationRunListQuery();
            }
            status = parsedStatus;
        }

        return new MigrationRunListQuery
        {
            Limit = limit,
            Offset = offset,
            SourceKind = sourceKind,
            Status = status
        };
    }

    internal static MigrationRunDto ToDto(MigrationRunRecord record) => new()
    {
        RunId = record.RunId,
        SourceKind = record.SourceKind,
        SourceUrl = record.SourceUrl,
        SourceDisplayName = record.SourceDisplayName,
        Status = MigrationRunSerialization.StatusToText(record.Status),
        StartedAt = record.StartedAt,
        CompletedAt = record.CompletedAt,
        EvidencePackRef = record.EvidencePackRef,
        EvidencePackFingerprint = record.EvidencePackFingerprint,
        StatusNote = record.StatusNote,
        HasEvidencePack = !string.IsNullOrEmpty(record.EvidencePackFingerprint),
        ReconciliationScorecardFingerprint = record.ReconciliationScorecardFingerprint,
        HasReconciliationScorecard = !string.IsNullOrEmpty(record.ReconciliationScorecardFingerprint)
    };

    private static bool IsValidJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string TrimToEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string? TrimToNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    // PA-164: this class is static (no constructor/DI-injected ILogger<T> field is
    // possible), so each catch resolves a logger from the current request's
    // service provider instead — the same pattern used by the sibling
    // GeoServerImportEndpoints/GeoservicesImportEndpoints static endpoint classes.
    private static ILogger<MigrationRunAdminEndpointsLog> GetLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILogger<MigrationRunAdminEndpointsLog>>();

    internal sealed class MigrationRunAdminEndpointsLog;

    private static partial class Log
    {
        [LoggerMessage(7988, LogLevel.Warning, "Failed to read request body for {Operation}")]
        public static partial void RequestBodyReadFailed(ILogger logger, string operation, Exception exception);

        [LoggerMessage(7989, LogLevel.Warning, "Failed to read request body for {Operation} (run {RunId})")]
        public static partial void RequestBodyReadFailedForRun(ILogger logger, string operation, string runId, Exception exception);
    }
}

public sealed record MigrationRunStartRequest
{
    public string? RunId { get; init; }

    public string? SourceKind { get; init; }

    public string? SourceUrl { get; init; }

    public string? SourceDisplayName { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public string? StatusNote { get; init; }
}

public sealed record MigrationRunCompletionRequest
{
    public string? Status { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? EvidencePackRef { get; init; }

    public string? EvidencePackFingerprint { get; init; }

    public string? EvidencePackBody { get; init; }

    public string? StatusNote { get; init; }
}

public sealed record MigrationRunDto
{
    public required string RunId { get; init; }

    public required string SourceKind { get; init; }

    public string SourceUrl { get; init; } = string.Empty;

    public string? SourceDisplayName { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? EvidencePackRef { get; init; }

    public string? EvidencePackFingerprint { get; init; }

    public string? StatusNote { get; init; }

    public bool HasEvidencePack { get; init; }

    public string? ReconciliationScorecardFingerprint { get; init; }

    public bool HasReconciliationScorecard { get; init; }
}

public sealed record MigrationRunListResponse
{
    public required MigrationRunDto[] Items { get; init; }

    public required long TotalCount { get; init; }

    public required int Limit { get; init; }

    public required int Offset { get; init; }
}

public sealed record MigrationRunCancelRequest
{
    public string? Reason { get; init; }
}

internal static class MigrationRunSerialization
{
    public static string StatusToText(MigrationRunStatus status) => status switch
    {
        MigrationRunStatus.Running => "running",
        MigrationRunStatus.Succeeded => "succeeded",
        MigrationRunStatus.Failed => "failed",
        MigrationRunStatus.Cancelled => "cancelled",
        _ => "unknown"
    };

    public static bool TryParseStatus(string text, out MigrationRunStatus status)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "running":
                status = MigrationRunStatus.Running;
                return true;
            case "succeeded":
                status = MigrationRunStatus.Succeeded;
                return true;
            case "failed":
                status = MigrationRunStatus.Failed;
                return true;
            case "cancelled":
            case "canceled":
                status = MigrationRunStatus.Cancelled;
                return true;
            default:
                status = MigrationRunStatus.Running;
                return false;
        }
    }
}
