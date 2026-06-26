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
///   <item><description><c>POST /api/v1/admin/import/arcgis/migrations</c> — ingest a run record plus manifest artifact (#1598). Called by <c>honua-migrate</c> after a codemod/translate run.</description></item>
///   <item><description><c>POST /api/v1/admin/import/arcgis/migrations/{runId}/parity</c> — ingest the parity artifact for a previously ingested run (#1598).</description></item>
/// </list>
/// </remarks>
internal static class ArcGisMigrationEvidenceEndpoints
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

        _ = group.MapGet("/migrations/{runId}/manifest", HandleGetManifest)
            .WithName("GetArcGisMigrationManifest")
            .WithSummary("Get the persisted MigrationManifestArtifact for an ArcGIS migration run.");

        _ = group.MapGet("/migrations/{runId}/parity", HandleGetParity)
            .WithName("GetArcGisMigrationParity")
            .WithSummary("Get the persisted ArcGisMigrationParityArtifact for an ArcGIS migration run.");

        _ = group.MapPost("/migrations", HandleIngestManifest)
            .WithName("IngestArcGisMigrationManifest")
            .WithSummary("Persist an ArcGIS migration run record plus manifest artifact as migration evidence.");

        _ = group.MapPost("/migrations/{runId}/parity", HandleIngestParity)
            .WithName("IngestArcGisMigrationParity")
            .WithSummary("Persist the parity artifact for a previously ingested ArcGIS migration run.");
    }

    private static async Task HandleIngestManifest(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        ArcGisMigrationManifestIngestRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.ArcGisMigrationManifestIngestRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
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

        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "runId is required.", StatusCodes.Status400BadRequest);
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

        var store = context.RequestServices.GetRequiredService<IArcGisMigrationEvidenceStore>();
        var record = new ArcGisMigrationRunRecord
        {
            RunId = request.RunId.Trim(),
            SourceUrl = RedactUrl(request.SourceUrl.Trim()),
            SourceDisplayName = request.SourceDisplayName,
            SourceVersion = request.SourceVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Actor = request.Actor
        };

        await store.SaveManifestAsync(record, request.Manifest, cancellationToken).ConfigureAwait(false);

        var response = new ArcGisMigrationManifestIngestResponse
        {
            RunId = record.RunId,
            Status = ArcGisMigrationRunStatuses.ManifestOnly
        };
        await Results.Json(response, ImportJsonContext.Default.ArcGisMigrationManifestIngestResponse, statusCode: StatusCodes.Status201Created)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleIngestParity(HttpContext context)
    {
        var runId = context.GetRouteValue("runId")?.ToString();
        if (string.IsNullOrWhiteSpace(runId))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "runId is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var cancellationToken = context.RequestAborted;

        ArcGisMigrationParityArtifact? parity;
        try
        {
            parity = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.ArcGisMigrationParityArtifact,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "Invalid request body.", StatusCodes.Status400BadRequest);
            return;
        }

        if (parity is null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "Request body is required.", StatusCodes.Status400BadRequest);
            return;
        }

        if (!IsKnownClassification(parity.Classification))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "classification must be one of: pass, warn, fail.",
                StatusCodes.Status400BadRequest);
            return;
        }

        var store = context.RequestServices.GetRequiredService<IArcGisMigrationEvidenceStore>();
        var manifest = await store.GetManifestAsync(runId, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "ArcGIS migration run not found.", StatusCodes.Status404NotFound);
            return;
        }

        try
        {
            await store.SaveParityAsync(runId, parity, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The manifest disappeared between the existence check and the write
            // (concurrent delete or backing-store reset).
            await AdminResponseWriter.WriteErrorAsync(
                context, "ArcGIS migration run not found.", StatusCodes.Status404NotFound);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static bool IsKnownClassification(string? classification)
        => string.Equals(classification, ArcGisMigrationParityClassifications.Pass, StringComparison.Ordinal)
            || string.Equals(classification, ArcGisMigrationParityClassifications.Warn, StringComparison.Ordinal)
            || string.Equals(classification, ArcGisMigrationParityClassifications.Fail, StringComparison.Ordinal);

    /// <summary>
    /// Strips userinfo, query, and fragment from the supplied source URL before persistence,
    /// matching the privacy posture of the batch orchestrator's evidence writes.
    /// </summary>
    private static string RedactUrl(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl) || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return sourceUrl;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.ToString();
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
    {
        foreach (var known in KnownStatuses)
        {
            if (string.Equals(status, known, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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
}

/// <summary>
/// Request body for <c>POST /api/v1/admin/import/arcgis/migrations</c> (#1598). Carries the
/// run identity plus the slice 2-4 manifest artifact produced by <c>honua-migrate</c> so the
/// server can persist migration evidence for later parity verification.
/// </summary>
public sealed record ArcGisMigrationManifestIngestRequest
{
    /// <summary>Stable run identifier (typically a GUID string).</summary>
    public string? RunId { get; init; }

    /// <summary>Canonical source URL the manifest was generated from. Userinfo, query, and fragment are stripped before persistence.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Optional human-readable source display name.</summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>Optional source version reported by the scan.</summary>
    public string? SourceVersion { get; init; }

    /// <summary>Optional actor identifier (operator id or service account) that triggered the run.</summary>
    public string? Actor { get; init; }

    /// <summary>Manifest artifact emitted by the scan/translate stages.</summary>
    public MigrationManifestArtifact? Manifest { get; init; }
}

/// <summary>
/// Response body for <c>POST /api/v1/admin/import/arcgis/migrations</c>.
/// </summary>
public sealed record ArcGisMigrationManifestIngestResponse
{
    /// <summary>Echo of the persisted run identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>Parity status after ingest. Always <c>manifest-only</c> until a parity artifact is ingested.</summary>
    public required string Status { get; init; }
}
