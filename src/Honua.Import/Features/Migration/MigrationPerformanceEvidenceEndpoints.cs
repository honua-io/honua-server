// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Server.Features.Import;
using Honua.Server.Features.Migration;
using Honua.Server.Features.FileImport;
using Honua.Server.Features.RasterImport;

namespace Honua.Server.Features.Migration;

/// <summary>
/// Admin / SDK HTTP endpoints exposing published migration performance evidence (#1033 slice 5).
/// </summary>
/// <remarks>
/// <para>
/// Three read-only endpoints are mapped under <c>/api/v1/admin/migration/performance-evidence</c>:
/// </para>
/// <list type="bullet">
///   <item>
///     <c>GET /latest</c> — the most recent record whose status is <c>Pass</c>, or
///     <c>204 No Content</c> when no passing record has been published yet.
///   </item>
///   <item>
///     <c>GET /history?sourceFamily=&amp;size=&amp;limit=</c> — per-fixture history page
///     in newest-first order with optional source-family and fixture-size filters.
///   </item>
///   <item>
///     <c>GET /{evidenceId}</c> — a single record by stable evidence identifier; <c>404</c>
///     when no matching record exists.
///   </item>
/// </list>
/// <para>
/// The endpoints render the full <see cref="MigrationPerformanceEvidenceArtifact"/> the way slice 4
/// emits it; no truncation, no thresholds re-derived server-side. The admin UI and SDKs render
/// the artifact directly so the website link and the API surface stay consistent.
/// </para>
/// </remarks>
internal static class MigrationPerformanceEvidenceEndpoints
{
    private const int DefaultHistoryLimit = 25;
    private const int MaxHistoryLimit = 200;

    /// <summary>
    /// Maps the migration performance evidence endpoints.
    /// </summary>
    public static void MapMigrationPerformanceEvidenceEndpoints(this WebApplication app)
    {
        var group = app
            .MapGroup("/api/v{version:apiVersion}/admin/migration/performance-evidence")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Migration")
            .RequireAdminAuthorization();

        _ = group.MapGet("/latest", HandleGetLatestAsync)
            .WithName("GetLatestMigrationPerformanceEvidence")
            .WithSummary("Return the most recent passing migration performance evidence artifact.");

        _ = group.MapGet("/history", HandleGetHistoryAsync)
            .WithName("ListMigrationPerformanceEvidenceHistory")
            .WithSummary("Return migration performance evidence history filtered by fixture.");

        _ = group.MapGet("/{evidenceId}", HandleGetByIdAsync)
            .WithName("GetMigrationPerformanceEvidenceById")
            .WithSummary("Return a single migration performance evidence artifact by id.");
    }

    private static async Task HandleGetLatestAsync(HttpContext context)
    {
        var store = context.RequestServices.GetRequiredService<IMigrationPerformanceEvidenceStore>();
        var record = await store.GetLatestPassingAsync(context.RequestAborted).ConfigureAwait(false);

        if (record is null)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await AdminResponseWriter
            .WriteJsonAsync(context, record, ImportJsonContext.Default.MigrationPerformanceEvidenceRecord)
            .ConfigureAwait(false);
    }

    private static async Task HandleGetHistoryAsync(HttpContext context)
    {
        var sourceFamily = ReadFilter(context, "sourceFamily");
        var fixtureSize = ReadFilter(context, "size");
        if (!TryReadLimit(context, out var limit, out var error))
        {
            await AdminResponseWriter
                .WriteErrorAsync(context, StatusCodes.Status400BadRequest, error)
                .ConfigureAwait(false);
            return;
        }

        var store = context.RequestServices.GetRequiredService<IMigrationPerformanceEvidenceStore>();
        var records = await store
            .GetHistoryAsync(sourceFamily, fixtureSize, limit, context.RequestAborted)
            .ConfigureAwait(false);

        var response = new MigrationPerformanceEvidenceHistoryResponse
        {
            SourceFamily = sourceFamily,
            FixtureSize = fixtureSize,
            Limit = limit,
            Count = records.Count,
            Items = records,
        };

        await AdminResponseWriter
            .WriteJsonAsync(context, response, ImportJsonContext.Default.MigrationPerformanceEvidenceHistoryResponse)
            .ConfigureAwait(false);
    }

    private static async Task HandleGetByIdAsync(HttpContext context, string evidenceId)
    {
        if (string.IsNullOrWhiteSpace(evidenceId))
        {
            await AdminResponseWriter
                .WriteErrorAsync(context, StatusCodes.Status400BadRequest, "evidenceId is required")
                .ConfigureAwait(false);
            return;
        }

        var store = context.RequestServices.GetRequiredService<IMigrationPerformanceEvidenceStore>();
        var record = await store.GetByIdAsync(evidenceId, context.RequestAborted).ConfigureAwait(false);

        if (record is null)
        {
            await AdminResponseWriter
                .WriteErrorAsync(context, StatusCodes.Status404NotFound, "Migration performance evidence not found.")
                .ConfigureAwait(false);
            return;
        }

        await AdminResponseWriter
            .WriteJsonAsync(context, record, ImportJsonContext.Default.MigrationPerformanceEvidenceRecord)
            .ConfigureAwait(false);
    }

    private static string? ReadFilter(HttpContext context, string name)
    {
        if (!context.Request.Query.TryGetValue(name, out var values))
        {
            return null;
        }

        foreach (var candidate in values)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }

    private static bool TryReadLimit(HttpContext context, out int limit, out string error)
    {
        limit = DefaultHistoryLimit;
        error = string.Empty;

        if (!context.Request.Query.TryGetValue("limit", out var values) || values.Count == 0)
        {
            return true;
        }

        var raw = values[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            error = "limit must be a positive integer.";
            return false;
        }

        limit = Math.Min(parsed, MaxHistoryLimit);
        return true;
    }
}

/// <summary>
/// Response envelope returned by the history endpoint. Wrapping the items in an object
/// (rather than returning a bare array) lets the API evolve with cursor pagination or
/// filter echoes without breaking callers.
/// </summary>
internal sealed record MigrationPerformanceEvidenceHistoryResponse
{
    /// <summary>Echoed source-family filter, if any.</summary>
    public string? SourceFamily { get; init; }

    /// <summary>Echoed fixture-size filter, if any.</summary>
    public string? FixtureSize { get; init; }

    /// <summary>Effective record cap applied by the store.</summary>
    public required int Limit { get; init; }

    /// <summary>Count of records returned in this response.</summary>
    public required int Count { get; init; }

    /// <summary>Records in newest-first order.</summary>
    public required IReadOnlyList<MigrationPerformanceEvidenceRecord> Items { get; init; }
}
