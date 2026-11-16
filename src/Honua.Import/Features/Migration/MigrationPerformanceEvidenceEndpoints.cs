// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Core.Features.Import.Abstractions;
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

internal static class MigrationPerformanceEvidenceEndpoints
{
    private const int DefaultHistoryLimit = 25;
    private const int MaxHistoryLimit = 200;

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

        return values
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!.Trim())
            .FirstOrDefault();
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

internal sealed record MigrationPerformanceEvidenceHistoryResponse
{
    public string? SourceFamily { get; init; }

    public string? FixtureSize { get; init; }

    public required int Limit { get; init; }

    public required int Count { get; init; }

    public required IReadOnlyList<MigrationPerformanceEvidenceRecord> Items { get; init; }
}
