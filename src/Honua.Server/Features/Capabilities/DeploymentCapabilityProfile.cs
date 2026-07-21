// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.AspNetCore.Routing.Template;

namespace Honua.Server.Features.Capabilities;

/// <summary>
/// Immutable, validated deployment-profile selection. An absent profile preserves the
/// historical full-surface behavior; a configured profile is an allowlist only and never
/// grants license entitlements.
/// </summary>
internal sealed class DeploymentCapabilityProfile
{
    internal const string EnabledCapabilitiesKey = "DeploymentProfile:EnabledCapabilities";
    internal const string SchemaVersionKey = "DeploymentProfile:SchemaVersion";
    internal const string SupportedSchemaVersion = "1.0.0";

    private readonly HashSet<string> _enabled;

    public DeploymentCapabilityProfile(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var rawCapabilities = configuration[EnabledCapabilitiesKey];
        var schemaVersion = configuration[SchemaVersionKey];

        if (string.IsNullOrWhiteSpace(rawCapabilities) && string.IsNullOrWhiteSpace(schemaVersion))
        {
            EnabledCapabilities = [];
            _enabled = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        if (string.IsNullOrWhiteSpace(rawCapabilities) || string.IsNullOrWhiteSpace(schemaVersion))
        {
            throw new InvalidOperationException(
                $"{EnabledCapabilitiesKey} and {SchemaVersionKey} must be configured together.");
        }

        if (!string.Equals(schemaVersion.Trim(), SupportedSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported deployment profile schema version '{schemaVersion}'. Expected '{SupportedSchemaVersion}'.");
        }

        var requested = rawCapabilities.Split(',', StringSplitOptions.TrimEntries);
        if (requested.Length == 0 || requested.Any(static key => key.Length == 0))
        {
            throw new InvalidOperationException("Deployment profile capability keys must be non-empty.");
        }

        var known = CapabilityKeyCatalog.All
            .Select(static capability => capability.Key)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = requested.Where(key => !known.Contains(key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"Unknown deployment profile capability key(s): {string.Join(", ", unknown)}.");
        }

        var duplicates = requested.GroupBy(static key => key, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate deployment profile capability key(s): {string.Join(", ", duplicates)}.");
        }

        SchemaVersion = schemaVersion.Trim();
        EnabledCapabilities = requested.Order(StringComparer.Ordinal).ToArray();
        _enabled = EnabledCapabilities.ToHashSet(StringComparer.Ordinal);
        IsConfigured = true;
    }

    public bool IsConfigured { get; }

    public string? SchemaVersion { get; }

    public IReadOnlyList<string> EnabledCapabilities { get; }

    public bool IsEnabled(string capability) => _enabled.Contains(capability);
}

/// <summary>Resolves routed HTTP endpoints to canonical capability keys.</summary>
internal sealed class DeploymentCapabilityRouteCatalog
{
    private const string CatalogResourceName = "Honua.Server.DeploymentProfiles.feature-catalog.json";
    private readonly Dictionary<string, RouteCapability[]> _routesByMethod;
    private readonly ConcurrentDictionary<Endpoint, RouteResolution> _endpointCache = new();

    public DeploymentCapabilityRouteCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(CatalogResourceName)
            ?? throw new InvalidOperationException($"Embedded capability route catalog '{CatalogResourceName}' was not found.");
        using var document = JsonDocument.Parse(stream);
        var routes = new List<RouteCapability>();
        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            var method = entry.GetProperty("method").GetString();
            var route = entry.GetProperty("route").GetString();
            var capability = entry.GetProperty("capability").GetString();
            if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(capability))
            {
                throw new InvalidOperationException("Embedded capability route catalog contains an incomplete entry.");
            }

            routes.Add(new RouteCapability(
                method.ToUpperInvariant(),
                new TemplateMatcher(TemplateParser.Parse(route.TrimStart('/')), new RouteValueDictionary()),
                capability,
                Specificity(route)));
        }

        _routesByMethod = routes
            .GroupBy(static route => route.Method, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(route => route.Specificity).ToArray(),
                StringComparer.Ordinal);
    }

    public RouteResolution Resolve(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            return RouteResolution.NotRouted;
        }

        return _endpointCache.GetOrAdd(endpoint, _ => ResolveUncached(context));
    }

    private RouteResolution ResolveUncached(HttpContext context)
    {
        var method = HttpMethods.IsHead(context.Request.Method)
            ? HttpMethods.Get
            : context.Request.Method.ToUpperInvariant();
        if (!_routesByMethod.TryGetValue(method, out var candidates))
        {
            return RouteResolution.Unmapped;
        }

        var match = candidates.FirstOrDefault(candidate =>
            candidate.Matcher.TryMatch(context.Request.Path, new RouteValueDictionary()));
        return match is null
            ? RouteResolution.Unmapped
            : new RouteResolution(true, match.Capability);
    }

    private static int Specificity(string route)
        => route.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Sum(static segment => segment.StartsWith('{') ? 1 : 10);

    private sealed record RouteCapability(
        string Method,
        TemplateMatcher Matcher,
        string Capability,
        int Specificity);

    public readonly record struct RouteResolution(bool IsRouted, string? Capability)
    {
        public static RouteResolution NotRouted => new(false, null);
        public static RouteResolution Unmapped => new(true, null);
    }
}

internal sealed class DeploymentCapabilityProfileMiddleware(
    RequestDelegate next,
    DeploymentCapabilityProfile profile,
    DeploymentCapabilityRouteCatalog routeCatalog)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!profile.IsConfigured)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var resolution = routeCatalog.Resolve(context);
        if (!resolution.IsRouted)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (resolution.Capability is null || !profile.IsEnabled(resolution.Capability))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}

internal static class DeploymentCapabilityProfileApplicationBuilderExtensions
{
    public static IApplicationBuilder UseDeploymentCapabilityProfile(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<DeploymentCapabilityProfileMiddleware>();
    }
}
