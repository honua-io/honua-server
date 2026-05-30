// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.Ogc.Common;

/// <summary>
/// Shared helper for serving OpenAPI specs in OGC endpoints.
/// </summary>
internal static class OgcOpenApiSpecUtilities
{
    private const int MaxCacheEntries = 50;
    private static readonly IReadOnlyDictionary<string, string> _openApiFormatParameters =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["json"] = MediaTypes.OpenApi
        };
    private static readonly string[] _openApiSupportedMediaTypes =
    [
        MediaTypes.OpenApi,
        MediaTypes.Json
    ];

    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _openApiCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IResult> GetOpenApiSpecAsync(
        HttpContext context,
        string? formatParameter,
        IWebHostEnvironment environment,
        IReadOnlySet<string> allowedQueryParameters,
        string openApiFileName,
        string fallbackSpec)
    {
        var request = context.Request;
        var validationError = OgcCommonUtilities.ValidateQueryParameters(request, allowedQueryParameters);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(
                formatParameter,
                context,
                _openApiFormatParameters,
                _openApiSupportedMediaTypes,
                MediaTypes.OpenApi,
                out _,
                out var formatError))
        {
            return OgcCommonUtilities.CreateFormatError(context, formatError);
        }

        string? openApiContent = null;
        try
        {
            openApiContent = await GetOpenApiContentAsync(environment.ContentRootPath, openApiFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HonuaTelemetry.RecordException(Activity.Current, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while loading the OpenAPI document.");
        }

        return Results.Content(string.IsNullOrWhiteSpace(openApiContent) ? fallbackSpec : openApiContent, MediaTypes.OpenApi);
    }

    private static Task<string?> GetOpenApiContentAsync(string contentRootPath, string openApiFileName)
    {
        var rootPath = Path.GetFullPath(contentRootPath);
        var cacheKey = $"{rootPath}|{openApiFileName}";

        // Use GetOrAdd first - this is atomic and handles the common case without racing
        if (_openApiCache.TryGetValue(cacheKey, out var existing))
        {
            return existing.Value;
        }

        // Only evict when at capacity and the key is genuinely new.
        // Eviction is best-effort; concurrent adds may slightly exceed MaxCacheEntries.
        if (_openApiCache.Count >= MaxCacheEntries)
        {
            // Remove one arbitrary entry rather than clearing the whole cache
            foreach (var kvp in _openApiCache)
            {
                if (_openApiCache.TryRemove(kvp))
                {
                    break;
                }
            }
        }

        var cacheEntry = _openApiCache.GetOrAdd(cacheKey, _ => new Lazy<Task<string?>>(
            () => ReadOpenApiContentAsync(rootPath, openApiFileName)));
        return cacheEntry.Value;
    }

    private static async Task<string?> ReadOpenApiContentAsync(string contentRootPath, string openApiFileName)
    {
        var searchPaths = new[]
        {
            Path.Combine(contentRootPath, openApiFileName),
            Path.Combine(AppContext.BaseDirectory, openApiFileName)
        };

        foreach (var openApiPath in searchPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(openApiPath))
            {
                continue;
            }

            return await File.ReadAllTextAsync(openApiPath);
        }

        return null;
    }
}
