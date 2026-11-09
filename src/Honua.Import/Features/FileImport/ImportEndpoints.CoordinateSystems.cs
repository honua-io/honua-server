// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Helpers;

namespace Honua.Import.FileImport;

internal static partial class ImportEndpoints
{
    private static async Task HandleCoordinateSystemsAsync(HttpContext context)
    {
        var query = context.Request.Query["q"].ToString().Trim();
        var limitText = context.Request.Query["limit"].ToString();
        var limit = 20;
        if (query.Length is < 1 or > 128 ||
            (limitText.Length > 0 && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 50)))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Supply q (1-128 characters) and limit (1-50).", StatusCodes.Status400BadRequest);
            return;
        }
        try
        {
            var registry = context.RequestServices.GetRequiredService<ICrsRegistry>();
            var result = await registry.SearchAsync(query, limit, context.RequestAborted);
            await Results.Json(result, ImportJsonContext.Default.CoordinateSystemSearchResult).ExecuteAsync(context);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.PreviewFailed(logger, "coordinate-system catalog", ex);
            await AdminResponseWriter.WriteErrorAsync(context, "The coordinate-system catalog is unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<FilePreview> EnrichPreviewCoordinateSystemAsync(
        HttpContext context, FilePreview preview, CancellationToken cancellationToken)
    {
        if (preview.CoordinateSystem?.DetectedSrid is not { } srid) return preview;
        var registry = context.RequestServices.GetRequiredService<ICrsRegistry>();
        var definition = await registry.ResolveBySridAsync(srid, cancellationToken);
        return definition.HasValue
            ? preview with { CoordinateSystem = preview.CoordinateSystem with { DetectedCrs = CoordinateSystemEntry.FromDefinition(definition.Value) } }
            : preview;
    }
}
