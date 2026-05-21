// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Startup;

/// <summary>
/// Static-asset wiring + endpoint filtering for the small hosted Blazor shells that ship
/// inside the server (currently just the optional STAC ops demo at <c>/samples/stac-ops</c>).
/// The in-tree admin UI was extracted to the sibling <c>honua-server-admin</c> repo and is
/// permanently blocked here so its leftover endpoint metadata cannot resurface.
/// </summary>
internal static class HostedBlazorAssetHelpers
{
    public static void ConfigureHostedBlazorAssets(
        IApplicationBuilder app,
        PathString pathPrefix)
    {
        var prefix = pathPrefix.Value?.Trim('/') ??
            throw new InvalidOperationException("Hosted Blazor path prefix is required.");
        var webRootFileProvider = app.ApplicationServices
            .GetRequiredService<IWebHostEnvironment>()
            .WebRootFileProvider;

        // Keep hosted shells isolated to their configured prefix so enabling one
        // does not expose another shell's static web assets.
        app.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = pathPrefix,
            FileProvider = new PathPrefixFileProvider(webRootFileProvider, prefix)
        });
    }

    public static void MapHostedBlazorFallback(
        IEndpointRouteBuilder endpoints,
        PathString pathPrefix)
    {
        var prefix = pathPrefix.Value?.TrimEnd('/') ??
            throw new InvalidOperationException("Hosted Blazor path prefix is required.");
        var fallbackRoute = $"{prefix}/{{*path:nonfile}}";
        var fallbackFile = $"{prefix.TrimStart('/')}/index.html";

        endpoints.MapFallbackToFile(fallbackRoute, fallbackFile);
    }

    public static void FilterHostedBlazorStaticAssetEndpoints(
        WebApplication app,
        bool allowStacOpsDemoAssets)
    {
        var blockedPrefixes = new List<string>(1);
        // Always block the `admin/` static-asset prefix — the in-tree Blazor admin UI
        // moved to the sibling `honua-server-admin` repo and ships as a separately
        // deployed static site.
        blockedPrefixes.Add("admin/");

        if (!allowStacOpsDemoAssets)
        {
            blockedPrefixes.Add("samples/stac-ops/");
        }

        var routeBuilder = (IEndpointRouteBuilder)app;
        var dataSources = routeBuilder.DataSources.ToArray();

        routeBuilder.DataSources.Clear();
        foreach (var dataSource in dataSources)
        {
            routeBuilder.DataSources.Add(new HostedBlazorStaticAssetFilterDataSource(
                dataSource,
                blockedPrefixes));
        }
    }

    public static void MapDisabledHostedBlazorPrefix(
        IApplicationBuilder app,
        PathString pathPrefix)
    {
        app.Map(pathPrefix, disabledApp =>
        {
            disabledApp.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
        });
    }

    private sealed class PathPrefixFileProvider(
        IFileProvider innerProvider,
        string pathPrefix) : IFileProvider
    {
        private readonly IFileProvider _innerProvider = innerProvider;
        private readonly string _pathPrefix = pathPrefix;

        public IDirectoryContents GetDirectoryContents(string subpath)
            => _innerProvider.GetDirectoryContents(ApplyPrefix(subpath));

        public IFileInfo GetFileInfo(string subpath)
            => _innerProvider.GetFileInfo(ApplyPrefix(subpath));

        public IChangeToken Watch(string filter)
            => _innerProvider.Watch(ApplyPrefix(filter));

        private string ApplyPrefix(string subpath)
        {
            var trimmedSubpath = subpath.TrimStart('/');
            return string.IsNullOrEmpty(trimmedSubpath)
                ? _pathPrefix
                : $"{_pathPrefix}/{trimmedSubpath}";
        }
    }

    private sealed class HostedBlazorStaticAssetFilterDataSource(
        EndpointDataSource innerDataSource,
        IReadOnlyCollection<string> blockedPrefixes) : EndpointDataSource
    {
        private readonly EndpointDataSource _innerDataSource = innerDataSource;
        private readonly IReadOnlyCollection<string> _blockedPrefixes = blockedPrefixes;

        public override IReadOnlyList<Endpoint> Endpoints => FilterEndpoints();

        public override IChangeToken GetChangeToken() => _innerDataSource.GetChangeToken();

        private IReadOnlyList<Endpoint> FilterEndpoints()
        {
            if (_blockedPrefixes.Count == 0)
            {
                return _innerDataSource.Endpoints;
            }

            return _innerDataSource.Endpoints
                .Where(ShouldKeepEndpoint)
                .ToArray();
        }

        private bool ShouldKeepEndpoint(Endpoint endpoint)
        {
            if (endpoint is not RouteEndpoint routeEndpoint)
            {
                return true;
            }

            var route = routeEndpoint.RoutePattern.RawText;
            if (string.IsNullOrEmpty(route))
            {
                return true;
            }

            foreach (var blockedPrefix in _blockedPrefixes)
            {
                if (route.StartsWith(blockedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
