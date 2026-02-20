// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

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

        // Never trust request Host headers for link generation unless an explicit
        // public base URL is configured. Derive a safe local origin instead.
        if (TryGetLocalOriginBaseUrl(request, out var localOriginBaseUrl))
        {
            return localOriginBaseUrl;
        }

        return request.PathBase.HasValue ? request.PathBase.Value!.TrimEnd('/') : string.Empty;
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

    private static bool TryGetLocalOriginBaseUrl(HttpRequest request, out string baseUrl)
    {
        baseUrl = string.Empty;

        var scheme = string.IsNullOrWhiteSpace(request.Scheme) ? Uri.UriSchemeHttp : request.Scheme;
        var localIp = request.HttpContext.Connection.LocalIpAddress;
        var localPort = request.HttpContext.Connection.LocalPort;

        var host = ResolveLocalHost(localIp);
        var includePort = localPort > 0 && !IsDefaultPort(scheme, localPort);
        var authority = includePort ? $"{host}:{localPort}" : host;
        var pathBase = request.PathBase.HasValue ? request.PathBase.Value!.TrimEnd('/') : string.Empty;
        var candidate = $"{scheme}://{authority}{pathBase}";

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out _))
        {
            return false;
        }

        baseUrl = candidate;
        return true;
    }

    private static string ResolveLocalHost(IPAddress? localIp)
    {
        if (localIp is null ||
            localIp.Equals(IPAddress.Any) ||
            localIp.Equals(IPAddress.IPv6Any) ||
            IPAddress.IsLoopback(localIp))
        {
            return "localhost";
        }

        return localIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{localIp}]"
            : localIp.ToString();
    }

    private static bool IsDefaultPort(string scheme, int port)
    {
        return (string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && port == 80)
            || (string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && port == 443);
    }
}
