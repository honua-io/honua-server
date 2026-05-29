// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Infrastructure.Middleware;

/// <summary>
/// Validates request Host headers against an allowlist to reduce DNS rebinding risk.
/// </summary>
internal sealed class HostValidationMiddleware(
    RequestDelegate next,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<HostValidationMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<HostValidationMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly bool _enabled = ResolveEnabled(configuration, environment);
    private readonly string[] _allowedHosts = ResolveAllowedHosts(configuration);
    private readonly string? _publicBaseUrlHost = ResolvePublicBaseUrlHost(configuration);
    private readonly bool _allowNullLocalIpForLocalhost = environment.IsEnvironment("Test");

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsHealthProbeRequest(context.Request.Path) ||
            !_enabled ||
            IsRequestHostAllowed(context, context.Request.Host))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        HostValidationLog.RejectedUntrustedHost(
            _logger,
            context.Request.Host.Value,
            context.Request.Path.Value);

        await StandardErrorHelpers.CreateBadRequest(context, "Invalid Host header.")
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    private static bool IsHealthProbeRequest(PathString path)
        => path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase);

    private bool IsRequestHostAllowed(HttpContext context, HostString requestHost)
    {
        var host = requestHost.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (_allowedHosts.Length > 0)
        {
            return IsAllowedByConfiguredHosts(host, _allowedHosts);
        }

        if (!string.IsNullOrWhiteSpace(_publicBaseUrlHost) &&
            string.Equals(host, _publicBaseUrlHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsFallbackAllowedHost(context, host, _allowNullLocalIpForLocalhost);
    }

    private static bool IsAllowedByConfiguredHosts(string requestHost, IReadOnlyList<string> allowedHosts)
    {
        foreach (var allowedHost in allowedHosts)
        {
            if (allowedHost.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = allowedHost[2..];
                if (requestHost.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(requestHost, allowedHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFallbackAllowedHost(HttpContext context, string host, bool allowNullLocalIpForLocalhost)
    {
        var localIp = context.Connection.LocalIpAddress;
        var isInMemoryConnection = localIp == null && context.Connection.RemoteIpAddress == null;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IsLoopbackConnection(localIp) ||
                   (allowNullLocalIpForLocalhost && localIp == null) ||
                   isInMemoryConnection;
        }

        var normalized = host.Trim('[', ']');
        if (!IPAddress.TryParse(normalized, out var requestedIp))
        {
            return false;
        }

        if (localIp == null)
        {
            if (isInMemoryConnection)
            {
                return IPAddress.IsLoopback(requestedIp);
            }

            return false;
        }

        if (IPAddress.IsLoopback(requestedIp) && IsLoopbackConnection(localIp))
        {
            return true;
        }

        requestedIp = NormalizeIpAddress(requestedIp);
        localIp = NormalizeIpAddress(localIp);

        if (localIp.Equals(IPAddress.Any) || localIp.Equals(IPAddress.IPv6Any))
        {
            return IPAddress.IsLoopback(requestedIp);
        }

        return requestedIp.Equals(localIp);
    }

    private static bool IsLoopbackConnection(IPAddress? address)
    {
        if (address == null)
        {
            return false;
        }

        return IPAddress.IsLoopback(NormalizeIpAddress(address));
    }

    private static IPAddress NormalizeIpAddress(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool ResolveEnabled(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration.GetValue<bool?>("HostValidation:Enabled");
        if (configured.HasValue)
        {
            return configured.Value;
        }

        return !environment.IsDevelopment() && !environment.IsEnvironment("Test");
    }

    private static string[] ResolveAllowedHosts(IConfiguration configuration)
    {
        var values = new List<string>();

        AddAllowedHosts(values, configuration.GetSection("HostValidation:AllowedHosts").Get<string[]>());

        var allowedHostsRaw = configuration["AllowedHosts"];
        if (!string.IsNullOrWhiteSpace(allowedHostsRaw))
        {
            AddAllowedHosts(
                values,
                allowedHostsRaw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddAllowedHosts(List<string> target, IEnumerable<string>? hosts)
    {
        if (hosts == null)
        {
            return;
        }

        foreach (var hostEntry in hosts)
        {
            if (string.IsNullOrWhiteSpace(hostEntry))
            {
                continue;
            }

            var trimmed = hostEntry.Trim();
            if (trimmed == "*")
            {
                continue;
            }

            var normalized = NormalizeAllowedHost(trimmed);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                target.Add(normalized);
            }
        }
    }

    private static string? NormalizeAllowedHost(string hostEntry)
    {
        if (hostEntry.StartsWith("*.", StringComparison.Ordinal))
        {
            return hostEntry[2..].Length > 0 ? hostEntry : null;
        }

        if (Uri.TryCreate(hostEntry, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrWhiteSpace(uri.Host) ? null : uri.Host;
        }

        if (Uri.TryCreate($"http://{hostEntry}", UriKind.Absolute, out var hostOnlyUri))
        {
            return string.IsNullOrWhiteSpace(hostOnlyUri.Host) ? null : hostOnlyUri.Host;
        }

        return hostEntry;
    }

    private static string? ResolvePublicBaseUrlHost(IConfiguration configuration)
    {
        var configuredBaseUrl = configuration["Public:BaseUrl"] ?? configuration["PUBLIC_BASE_URL"];
        if (string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return null;
        }

        var trimmed = configuredBaseUrl.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
    }
}

internal static class HostValidationMiddlewareExtensions
{
    public static IApplicationBuilder UseHostValidation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<HostValidationMiddleware>();
    }
}
