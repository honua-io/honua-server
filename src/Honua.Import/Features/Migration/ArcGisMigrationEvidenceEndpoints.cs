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
