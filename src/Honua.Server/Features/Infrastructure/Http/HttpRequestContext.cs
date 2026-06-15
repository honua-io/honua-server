// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Http;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Http;

/// <summary>
/// ASP.NET Core implementation of IRequestContext that adapts HttpContext to the Core abstraction.
/// </summary>
public sealed class HttpRequestContext : IRequestContext
{
    private readonly HttpContext _httpContext;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _queryParameters;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _headers;

    public HttpRequestContext(HttpContext httpContext)
    {
        _httpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));

        _queryParameters = new Lazy<IReadOnlyDictionary<string, string>>(() =>
        {
            var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var param in httpContext.Request.Query)
            {
                queryParams[param.Key] = param.Value.ToString();
            }
            return queryParams;
        });

        _headers = new Lazy<IReadOnlyDictionary<string, string>>(() =>
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in httpContext.Request.Headers)
            {
                headers[header.Key] = header.Value.ToString();
            }
            return headers;
        });
    }

    public string BaseUrl
    {
        get
        {
            var request = _httpContext.Request;
            return $"{request.Scheme}://{request.Host}{request.PathBase}";
        }
    }

    public string RequestUrl => $"{BaseUrl}{_httpContext.Request.Path}{_httpContext.Request.QueryString}";

    public string Scheme => _httpContext.Request.Scheme;

    public string Host => _httpContext.Request.Host.ToString();

    public string Path => _httpContext.Request.Path.ToString();

    public IReadOnlyDictionary<string, string> QueryParameters => _queryParameters.Value;

    public IReadOnlyDictionary<string, string> Headers => _headers.Value;

    public string? UserAgent => _httpContext.Request.Headers.UserAgent.FirstOrDefault();

    public string? ClientIP
    {
        get
        {
            // Trust the connection IP only — raw X-Forwarded-For / X-Real-IP headers
            // are client-controlled and trivially spoofable, which would let callers
            // forge the IP recorded in audit/security telemetry. When forwarded
            // headers are enabled, the ASP.NET Core forwarded-headers middleware has
            // already rewritten RemoteIpAddress after validating the proxy against
            // the known-proxy list (mirrors RateLimitingMiddleware.GetClientIpAddress).
            return _httpContext.Connection.RemoteIpAddress?.ToString();
        }
    }

    public string? GetQueryParameter(string name)
    {
        return QueryParameters.TryGetValue(name, out var value) ? value : null;
    }

    public string? GetHeader(string name)
    {
        return Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// Creates an IRequestContext from an HttpContext.
    /// </summary>
    /// <param name="httpContext">The HttpContext to adapt</param>
    /// <returns>An IRequestContext implementation</returns>
    public static IRequestContext FromHttpContext(HttpContext httpContext)
    {
        return new HttpRequestContext(httpContext);
    }
}
