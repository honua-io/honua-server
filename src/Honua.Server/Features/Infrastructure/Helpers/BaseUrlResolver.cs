// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Helpers;

/// <summary>
/// Resolves the public-facing base URL for link generation.
/// </summary>
internal static class BaseUrlResolver
{
    private const string BaseUrlConfigKey = "Public:BaseUrl";
    private const string BaseUrlEnvKey = "PUBLIC_BASE_URL";

    public static string GetBaseUrl(HttpContext context)
    {
        return GetBaseUrl(context.Request);
    }

    public static string GetBaseUrl(HttpRequest request)
    {
        var configuration = request.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var configured = configuration[BaseUrlConfigKey] ?? configuration[BaseUrlEnvKey];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return $"{request.Scheme}://{request.Host}";
    }
}
