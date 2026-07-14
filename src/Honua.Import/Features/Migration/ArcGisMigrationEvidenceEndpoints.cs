// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
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
/// Admin endpoints that surface ArcGIS migration evidence (slices 1-5) for SDKs and
/// admin UIs (#1025 slice 6).
/// </summary>
/// <remarks>
/// Routes:
/// <list type="bullet">
///   <item><description><c>GET /api/v1/admin/import/arcgis/migrations</c> — paged run list with optional <c>sourceUrl</c>/<c>status</c> filters.</description></item>
///   <item><description><c>GET /api/v1/admin/import/arcgis/migrations/{runId}/manifest</c> — slice 2-4 manifest artifact.</description></item>
///   <item><description><c>GET /api/v1/admin/import/arcgis/migrations/{runId}/parity</c> — slice 5 parity artifact.</description></item>
/// </list>
/// </remarks>
internal static partial class ArcGisMigrationEvidenceEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    /// <summary>
    /// Maps the ArcGIS migration evidence endpoints.
    /// </summary>
    public static void MapArcGisMigrationEvidenceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import/arcgis")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Import")
            .RequireAdminAuthorization();

        _ = group.MapGet("/migrations", HandleListRuns)
            .WithName("ListArcGisMigrationRuns")
            .WithSummary("List ArcGIS migration runs with persisted manifest and parity evidence.");

        _ = group.MapPost("/migrations/{runId}/manifest", HandleSaveManifest)
            .WithName("RecordArcGisMigrationManifest")
            .WithSummary("Persist the manifest evidence artifact for an ArcGIS migration run.");

        _ = group.MapGet("/migrations/{runId}/manifest", HandleGetManifest)
            .WithName("GetArcGisMigrationManifest")
            .WithSummary("Get the persisted MigrationManifestArtifact for an ArcGIS migration run.");

        _ = group.MapPost("/migrations/{runId}/parity", HandleSaveParity)
            .WithName("RecordArcGisMigrationParity")
            .WithSummary("Persist the parity evidence artifact for an ArcGIS migration run.");

        _ = group.MapGet("/migrations/{runId}/parity", HandleGetParity)
            .WithName("GetArcGisMigrationParity")
            .WithSummary("Get the persisted ArcGisMigrationParityArtifact for an ArcGIS migration run.");
    }

    private static async Task HandleListRuns(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var store = context.RequestServices.GetRequiredService<IArcGisMigrationEvidenceStore>();
        var query = context.Request.Query;

        var sourceUrl = TrimToNull(query["sourceUrl"]);
        var status = TrimToNull(query["status"]);

        if (status != null && !IsKnownStatus(status))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                $"status must be one of: {string.Join(", ", KnownStatuses)}.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryParseInt(query["page"], 0, out var page) || page < 0)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "page must be a non-negative integer.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryParseInt(query["pageSize"], DefaultPageSize, out var pageSize) || pageSize <= 0)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                $"pageSize must be a positive integer (max {MaxPageSize}).",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (pageSize > MaxPageSize)
        {
            pageSize = MaxPageSize;
        }

        var filter = new ArcGisMigrationRunFilter
        {
            SourceUrl = sourceUrl,
            Status = status,
            Page = page,
            PageSize = pageSize
        };

        var result = await store.ListAsync(filter, cancellationToken).ConfigureAwait(false);

        await Results.Json(result, ImportJsonContext.Default.ArcGisMigrationRunListResult)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleSaveManifest(HttpContext context)
    {
        var runId = context.GetRouteValue("runId")?.ToString();
        if (string.IsNullOrWhiteSpace(runId))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "runId is required.", StatusCodes.Status400BadRequest);
            return;
        }

        ArcGisMigrationManifestIngestionRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.ArcGisMigrationManifestIngestionRequest,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: ReadFromJsonAsync can throw JsonException, NotSupportedException,
        // or IOException for malformed/unreadable request bodies; map all of them to a 400 response.
        catch (Exception ex)
        {
            Log.RequestDeserializationFailed(GetLogger(context), runId, ex);
            await AdminResponseWriter.WriteErrorAsync(
                context, "Invalid request body.", StatusCodes.Status400BadRequest);
            return;
        }

        if (request is null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "Request body is required.", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "sourceUrl is required.", StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Manifest is null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "manifest is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var record = new ArcGisMigrationRunRecord
        {
            RunId = runId,
            SourceUrl = request.SourceUrl.Trim(),
            SourceDisplayName = TrimToNull(request.SourceDisplayName),
            SourceVersion = TrimToNull(request.SourceVersion),
            CreatedAt = request.CreatedAt ?? DateTimeOffset.UtcNow,
            Actor = TrimToNull(request.Actor)
        };

        var store = context.RequestServices.GetRequiredService<IArcGisMigrationEvidenceStore>();
        await store.SaveManifestAsync(record, request.Manifest, context.RequestAborted)
            .ConfigureAwait(false);

        await Results.Json(record, ImportJsonContext.Default.ArcGisMigrationRunRecord)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleGetManifest(HttpContext context)
    {
        var runId = context.GetRouteValue("runId")?.ToString();
        if (string.IsNullOrWhiteSpace(runId))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "runId is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var store = context.RequestServices.GetRequiredService<IArcGisMigrationEvidenceStore>();
        var manifest = await store.GetManifestAsync(runId, cancellationToken).ConfigureAwait(false);

        if (manifest is null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "ArcGIS migration run not found.", StatusCodes.Status404NotFound);
            return;
        }

        await Results.Json(manifest, ImportJsonContext.Default.MigrationManifestArtifact)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleSaveParity(HttpContext context)
    {
        var runId = context.GetRouteValue("runId")?.ToString();
        if (string.IsNullOrWhiteSpace(runId))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "runId is required.", StatusCodes.Status400BadRequest);
            return;
        }

        ArcGisMigrationParityIngestionRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.ArcGisMigrationParityIngestionRequest,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: ReadFromJsonAsync can throw JsonException, NotSupportedException,
        // or IOException for malformed/unreadable request bodies; map all of them to a 400 response.
        catch (Exception ex)
        {
            Log.RequestDeserializationFailed(GetLogger(context), runId, ex);
            await AdminResponseWriter.WriteErrorAsync(
                context, "Invalid request body.", StatusCodes.Status400BadRequest);
            return;
        }

        if (request is null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "Request body is required.", StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Parity is null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "parity is required.", StatusCodes.Status400BadRequest);
            return;
        }

        if (!IsKnownParityClassification(request.Parity.Classification))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "parity.classification must be one of: pass, warn, fail.",
                StatusCodes.Status400BadRequest);
            return;
        }

        var store = context.RequestServices.GetRequiredService<IArcGisMigrationEvidenceStore>();
        var manifest = await store.GetManifestAsync(runId, context.RequestAborted).ConfigureAwait(false);
        if (manifest is null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "ArcGIS migration manifest must be recorded before parity.",
                StatusCodes.Status409Conflict);
            return;
        }

        try
        {
            await store.SaveParityAsync(runId, request.Parity, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "ArcGIS migration manifest must be recorded before parity.",
                StatusCodes.Status409Conflict);
            return;
        }

        await Results.Json(request.Parity, ImportJsonContext.Default.ArcGisMigrationParityArtifact)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleGetParity(HttpContext context)
    {
        var runId = context.GetRouteValue("runId")?.ToString();
        if (string.IsNullOrWhiteSpace(runId))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "runId is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var store = context.RequestServices.GetRequiredService<IArcGisMigrationEvidenceStore>();

        // The manifest must exist before parity can be looked up; load it first so we can
        // distinguish "unknown run" (404) from "manifest only, parity not run yet" (404 with
        // a more specific message).
        var manifest = await store.GetManifestAsync(runId, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "ArcGIS migration run not found.", StatusCodes.Status404NotFound);
            return;
        }

        var parity = await store.GetParityAsync(runId, cancellationToken).ConfigureAwait(false);
        if (parity is null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "ArcGIS migration run has no parity artifact yet.",
                StatusCodes.Status404NotFound);
            return;
        }

        await Results.Json(parity, ImportJsonContext.Default.ArcGisMigrationParityArtifact)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static readonly string[] KnownStatuses =
    [
        ArcGisMigrationRunStatuses.ManifestOnly,
        ArcGisMigrationRunStatuses.Pass,
        ArcGisMigrationRunStatuses.Warn,
        ArcGisMigrationRunStatuses.Fail
    ];

    private static bool IsKnownStatus(string status)
        => KnownStatuses.Any(known => string.Equals(status, known, StringComparison.OrdinalIgnoreCase));

    private static bool IsKnownParityClassification(string classification)
        => string.Equals(classification, ArcGisMigrationParityClassifications.Pass, StringComparison.OrdinalIgnoreCase)
            || string.Equals(classification, ArcGisMigrationParityClassifications.Warn, StringComparison.OrdinalIgnoreCase)
            || string.Equals(classification, ArcGisMigrationParityClassifications.Fail, StringComparison.OrdinalIgnoreCase);

    private static string? TrimToNull(Microsoft.Extensions.Primitives.StringValues values)
    {
        var first = values.ToString();
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        var trimmed = first.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool TryParseInt(
        Microsoft.Extensions.Primitives.StringValues values,
        int defaultValue,
        out int parsed)
    {
        var first = values.ToString();
        if (string.IsNullOrWhiteSpace(first))
        {
            parsed = defaultValue;
            return true;
        }

        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static string? TrimToNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static ILogger<ArcGisMigrationEvidenceEndpointsLog> GetLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILogger<ArcGisMigrationEvidenceEndpointsLog>>();

    /// <summary>Log category marker for ArcGIS migration evidence endpoint operations.</summary>
    internal sealed class ArcGisMigrationEvidenceEndpointsLog;

    private static partial class Log
    {
        [LoggerMessage(8000, LogLevel.Warning, "Failed to deserialize request body for ArcGIS migration run {RunId}")]
        public static partial void RequestDeserializationFailed(ILogger logger, string runId, Exception exception);
    }
}

/// <summary>
/// Request body for recording an ArcGIS migration manifest artifact.
/// </summary>
public sealed record ArcGisMigrationManifestIngestionRequest
{
    /// <summary>Canonical source URL the manifest was generated from.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Optional operator-visible source display name.</summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>Optional source system version.</summary>
    public string? SourceVersion { get; init; }

    /// <summary>UTC instant to store on the run record. Defaults to server receive time.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Optional operator or service account identifier that produced the run.</summary>
    public string? Actor { get; init; }

    /// <summary>Manifest artifact emitted by the ArcGIS migration planning pipeline.</summary>
    public MigrationManifestArtifact? Manifest { get; init; }
}

/// <summary>
/// Request body for recording an ArcGIS migration parity artifact.
/// </summary>
public sealed record ArcGisMigrationParityIngestionRequest
{
    /// <summary>Parity artifact emitted after the manifest has been applied.</summary>
    public ArcGisMigrationParityArtifact? Parity { get; init; }
}
