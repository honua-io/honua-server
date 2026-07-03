// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Slice 5 of issue #1015. Admin orchestration endpoints that the migration
/// UI and SDK call to list runs, fetch a run, download the slice-4 evidence
/// pack for a run, or cancel a still-running run. The endpoints are a thin
/// adapter over <see cref="IMigrationRunCatalog"/>; all storage decisions
/// (Postgres today, alternate backends tomorrow) live behind the catalog.
/// </summary>
internal static class MigrationRunAdminEndpoints
{
    private const int DefaultPageLimit = 25;
    private const int MaxPageLimit = 100;

    /// <summary>
    /// Maps the four migration-run admin orchestration endpoints under
    /// <c>/api/v1/admin/migration/runs</c>.
    /// </summary>
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
        catch (Exception)
        {
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
        catch (Exception)
        {
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
        catch (Exception)
        {
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
            catch (Exception)
            {
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

        if (query.TryGetValue("offset", out var offsetValues) && offsetValues.Count > 0 && !string.IsNullOrWhiteSpace(offsetValues[0]))
        {
            if (!int.TryParse(offsetValues[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0)
            {
                error = "offset must be a non-negative integer.";
                return new MigrationRunListQuery();
            }
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
}

/// <summary>
/// Request body for recording a migration run start.
/// </summary>
public sealed record MigrationRunStartRequest
{
    /// <summary>Stable run identifier supplied by the migration producer.</summary>
    public string? RunId { get; init; }

    /// <summary>Source kind identifier, for example <c>arcgis-geoservices-rest</c>.</summary>
    public string? SourceKind { get; init; }

    /// <summary>Redacted source URL. Optional for offline-manifest runs.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Optional operator-visible source display name.</summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>UTC instant the run started. Defaults to server receive time.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Optional operator-visible note.</summary>
    public string? StatusNote { get; init; }
}

/// <summary>
/// Request body for recording the terminal state of a migration run.
/// </summary>
public sealed record MigrationRunCompletionRequest
{
    /// <summary>Terminal status: <c>succeeded</c>, <c>failed</c>, or <c>cancelled</c>.</summary>
    public string? Status { get; init; }

    /// <summary>UTC instant the run completed. Defaults to server receive time.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Optional storage reference for the evidence pack.</summary>
    public string? EvidencePackRef { get; init; }

    /// <summary>Optional evidence pack fingerprint.</summary>
    public string? EvidencePackFingerprint { get; init; }

    /// <summary>Optional evidence pack JSON body to persist inline.</summary>
    public string? EvidencePackBody { get; init; }

    /// <summary>Optional operator-visible note.</summary>
    public string? StatusNote { get; init; }
}

/// <summary>
/// Wire representation of a <see cref="MigrationRunRecord"/> returned by the
/// admin endpoints. <see cref="HasEvidencePack"/> is a convenience flag so
/// UIs can decide whether to render the download button without fetching the
/// pack body.
/// </summary>
public sealed record MigrationRunDto
{
    /// <summary>Stable run id.</summary>
    public required string RunId { get; init; }

    /// <summary>Source kind (e.g. <c>geoserver-rest</c>).</summary>
    public required string SourceKind { get; init; }

    /// <summary>Redacted source URL.</summary>
    public string SourceUrl { get; init; } = string.Empty;

    /// <summary>Operator-visible source display name.</summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>Lowercase status: running, succeeded, failed, cancelled.</summary>
    public required string Status { get; init; }

    /// <summary>UTC start instant.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>UTC completion instant (null while running).</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Storage reference for the slice-4 evidence pack.</summary>
    public string? EvidencePackRef { get; init; }

    /// <summary>Bundle fingerprint of the slice-4 evidence pack.</summary>
    public string? EvidencePackFingerprint { get; init; }

    /// <summary>Operator-visible note recorded on cancel or failure.</summary>
    public string? StatusNote { get; init; }

    /// <summary>True when the evidence pack is available for download.</summary>
    public bool HasEvidencePack { get; init; }

    /// <summary>Fingerprint of the signed reconciliation scorecard (issue #1381), when recorded.</summary>
    public string? ReconciliationScorecardFingerprint { get; init; }

    /// <summary>True when the reconciliation scorecard is available for download.</summary>
    public bool HasReconciliationScorecard { get; init; }
}

/// <summary>
/// Response envelope for <c>GET /api/v1/admin/migration/runs</c>.
/// </summary>
public sealed record MigrationRunListResponse
{
    /// <summary>Records in this page.</summary>
    public required MigrationRunDto[] Items { get; init; }

    /// <summary>Total matching records (pre-paging).</summary>
    public required long TotalCount { get; init; }

    /// <summary>Echo of the applied page limit.</summary>
    public required int Limit { get; init; }

    /// <summary>Echo of the applied page offset.</summary>
    public required int Offset { get; init; }
}

/// <summary>
/// Request body for <c>POST /api/v1/admin/migration/runs/{runId}/cancel</c>.
/// </summary>
public sealed record MigrationRunCancelRequest
{
    /// <summary>
    /// Optional operator-supplied note explaining why the run was cancelled.
    /// Persisted verbatim into <c>migration_runs.status_note</c>; no secrets
    /// expected.
    /// </summary>
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
