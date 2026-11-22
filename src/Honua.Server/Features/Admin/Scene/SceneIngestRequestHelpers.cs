// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Caching;
using Honua.Scene;
using Honua.Server.Features.Admin.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin.Scene;

internal static class SceneIngestRequestHelpers
{
    public static bool IsAsyncRequested(HttpContext context, IFormCollection? form = null, bool bodyAsync = false)
    {
        if (bodyAsync)
        {
            return true;
        }

        if (context.Request.Query.TryGetValue("async", out var queryValues)
            && bool.TryParse(queryValues.FirstOrDefault(), out var queryAsync)
            && queryAsync)
        {
            return true;
        }

        if (form is not null
            && form.TryGetValue("async", out var formValues)
            && bool.TryParse(formValues.FirstOrDefault(), out var formAsync)
            && formAsync)
        {
            return true;
        }

        if (context.Request.Headers.TryGetValue("Prefer", out var preferValues))
        {
            foreach (var value in preferValues)
            {
                if (value is not null && value.Contains("respond-async", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static IResult CreateAcceptedJobResult(HttpContext context, SceneJobSnapshot snapshot)
    {
        context.Response.Headers.Location = snapshot.StatusUrl;
        var response = new SceneJobAcceptedResponse
        {
            JobId = snapshot.JobId,
            Status = snapshot.Status,
            SourceKind = snapshot.SourceKind,
            SceneId = snapshot.SceneId,
            StatusUrl = snapshot.StatusUrl,
            CreatedAt = snapshot.CreatedAt,
        };
        return Results.Json(
            response,
            SceneWorkflowJsonContext.Default.SceneJobAcceptedResponse,
            statusCode: StatusCodes.Status202Accepted);
    }

    public static SceneJobStatusResponse ToStatusResponse(SceneJobSnapshot snapshot) => new()
    {
        JobId = snapshot.JobId,
        SourceKind = snapshot.SourceKind,
        Status = snapshot.Status,
        SceneId = snapshot.SceneId,
        StatusUrl = snapshot.StatusUrl,
        TilesetUrl = snapshot.TilesetUrl,
        I3sUrl = snapshot.I3sUrl,
        TileCount = snapshot.TileCount,
        FileCount = snapshot.FileCount,
        ByteSize = snapshot.ByteSize,
        Warnings = snapshot.Warnings,
        ErrorCode = snapshot.ErrorCode,
        ErrorMessage = snapshot.ErrorMessage,
        CreatedAt = snapshot.CreatedAt,
        StartedAt = snapshot.StartedAt,
        CompletedAt = snapshot.CompletedAt,
    };

    public static string BuildTilesetUrl(string baseUrl, string sceneId)
        => SceneUrls.AbsoluteTilesetUrl(baseUrl, sceneId);

    public static string BuildI3sUrl(string baseUrl, string sceneId)
        => string.Concat(baseUrl.AsSpan().TrimEnd('/'), "/rest/services/".AsSpan(), Uri.EscapeDataString(sceneId).AsSpan(), "/SceneServer".AsSpan());

    public static bool TryParseFormInt(IFormCollection form, string key, out int? value, out string? error)
    {
        value = null;
        error = null;
        var raw = form[key];
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0]))
        {
            return true;
        }

        if (!int.TryParse(raw[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{key} must be an integer.";
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryParseFormBool(IFormCollection form, string key, out bool? value, out string? error)
    {
        value = null;
        error = null;
        var raw = form[key];
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0]))
        {
            return true;
        }

        if (!bool.TryParse(raw[0], out var parsed))
        {
            error = $"{key} must be 'true' or 'false'.";
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryParseRequiredDouble(IFormCollection form, string key, out double value, out string? error)
    {
        value = 0;
        error = null;
        var raw = form[key];
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0]))
        {
            error = $"{key} is required.";
            return false;
        }

        if (!double.TryParse(raw[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed))
        {
            error = $"{key} must be a finite number.";
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryParseOptionalDouble(IFormCollection form, string key, double defaultValue, out double value, out string? error)
    {
        value = defaultValue;
        error = null;
        var raw = form[key];
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0]))
        {
            return true;
        }

        if (!double.TryParse(raw[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed))
        {
            error = $"{key} must be a finite number.";
            return false;
        }

        value = parsed;
        return true;
    }

    public static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static (int Status, string Detail) MapValidationStatus(string message)
    {
        if (message.StartsWith($"{SceneGenerationErrorCodes.LayerNotFound}:", StringComparison.Ordinal))
        {
            return (StatusCodes.Status404NotFound, message);
        }

        if (message.StartsWith($"{SceneGenerationErrorCodes.SceneRegistrationConflict}:", StringComparison.Ordinal))
        {
            return (StatusCodes.Status409Conflict, message);
        }

        return (StatusCodes.Status400BadRequest, message);
    }

    public static async Task InvalidateCacheBestEffortAsync(
        HttpContext context,
        string sceneId,
        Action<Exception> onError,
        CancellationToken cancellationToken)
    {
        var invalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (invalidator is null)
        {
            return;
        }

        try
        {
            await invalidator.InvalidateSceneAsync(sceneId, cancellationToken).ConfigureAwait(false);
        }
        // Best-effort cache invalidation must not fail an already-completed scene publish.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            onError(ex);
        }
    }

    public static SceneExtentDto? ToExtentDto(SceneExtent? extent)
        => extent is null
            ? null
            : new SceneExtentDto
            {
                XMin = extent.XMin,
                YMin = extent.YMin,
                XMax = extent.XMax,
                YMax = extent.YMax,
            };
}
