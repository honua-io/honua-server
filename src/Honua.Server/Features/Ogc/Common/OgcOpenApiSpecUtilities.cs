// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Ogc.Common;

/// <summary>
/// Shared helper for serving OpenAPI specs in OGC endpoints.
/// </summary>
internal static class OgcOpenApiSpecUtilities
{
    private const int MaxCacheEntries = 50;

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

        if (!string.IsNullOrWhiteSpace(formatParameter) &&
            !string.Equals(formatParameter, "json", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Unsupported format '{formatParameter}'");
        }

        if (!IsOpenApiAcceptable(request))
        {
            return StandardErrorHelpers.CreateNotAcceptable(context, "Requested format is not acceptable.");
        }

        string? openApiContent = null;
        try
        {
            openApiContent = await GetOpenApiContentAsync(environment.ContentRootPath, openApiFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            openApiContent = null;
        }

        return Results.Content(string.IsNullOrWhiteSpace(openApiContent) ? fallbackSpec : openApiContent, MediaTypes.OpenApi);
    }

    private static bool IsOpenApiAcceptable(HttpRequest request)
    {
        var acceptHeader = request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            return true;
        }

        return acceptHeader.Contains("*/*", StringComparison.OrdinalIgnoreCase) ||
               acceptHeader.Contains("application/vnd.oai.openapi+json", StringComparison.OrdinalIgnoreCase) ||
               acceptHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
               acceptHeader.Contains("+json", StringComparison.OrdinalIgnoreCase);
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
        var openApiPath = Path.Combine(contentRootPath, openApiFileName);
        if (!File.Exists(openApiPath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(openApiPath);
    }
}
