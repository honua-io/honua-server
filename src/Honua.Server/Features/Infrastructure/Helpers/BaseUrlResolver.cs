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
        if (TryGetConfiguredBaseUrl(configuration, out var configuredBaseUrl))
        {
            return configuredBaseUrl;
        }

        return $"{request.Scheme}://{request.Host}";
    }

    public static bool TryGetConfiguredBaseUrl(HttpContext context, out string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryGetConfiguredBaseUrl(context.Request, out baseUrl);
    }

    public static bool TryGetConfiguredBaseUrl(HttpRequest request, out string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = request.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        return TryGetConfiguredBaseUrl(configuration, out baseUrl);
    }

    private static bool TryGetConfiguredBaseUrl(IConfiguration configuration, out string baseUrl)
    {
        baseUrl = string.Empty;
        var configured = configuration[BaseUrlConfigKey] ?? configuration[BaseUrlEnvKey];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = configured.TrimEnd('/');
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                baseUrl = trimmed;
                return true;
            }
        }

        return false;
    }
}
