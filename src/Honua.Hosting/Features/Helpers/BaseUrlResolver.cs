// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Helpers;

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

    /// <summary>
    /// Carries the request's URL credential onto an advertised operation URL.
    /// </summary>
    public static string PreserveToken(HttpRequest request, string url)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var token = ReadSingleToken(request.Query["token"]);
        if (string.IsNullOrEmpty(token))
        {
            return url;
        }

        var separator = url.Contains('?') ? '&' : '?';
        return string.Concat(url, separator, "token=", Uri.EscapeDataString(token));
    }

    private static string? ReadSingleToken(StringValues values)
    {
        // Mirror PortalTokenAuthenticationHandler: repeated identical tokens are one
        // credential, while conflicting values are no credential. StringValues.ToString
        // would join duplicates as "abc,abc" and advertise a token that never authenticates.
        if (values.Count == 0)
        {
            return null;
        }

        var candidate = values[0]?.Trim();
        for (var index = 1; index < values.Count; index++)
        {
            if (!string.Equals(candidate, values[index]?.Trim(), StringComparison.Ordinal))
            {
                return null;
            }
        }

        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
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

    internal static bool TryGetConfiguredBaseUrl(IConfiguration configuration, out string baseUrl)
    {
        baseUrl = string.Empty;
        var configured = GetFirstNonEmpty(
            configuration[BaseUrlConfigKey],
            configuration[BaseUrlEnvKey],
            Environment.GetEnvironmentVariable(BaseUrlEnvKey));

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

    private static string? GetFirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
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
