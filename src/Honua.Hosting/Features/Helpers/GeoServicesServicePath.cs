// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.Helpers;

internal sealed record GeoServicesServicePath(
    string Root,
    string? FolderName,
    string PublicName,
    MetadataV2GraphSnapshot Snapshot,
    IReadOnlySet<string> TargetFacetIds)
{
    private static readonly object BindingKey = new();

    public string QualifiedName => FolderName is null ? PublicName : $"{FolderName}/{PublicName}";

    public static GeoServicesServicePath? FromRequest(HttpContext context)
        => context.Items.TryGetValue(BindingKey, out var value) ? value as GeoServicesServicePath : null;

    public static bool TargetsService(HttpContext context, MetadataV2Service service)
        => FromRequest(context) is not { } path || path.TargetFacetIds.Contains(service.Metadata.Id);

    internal static async Task ResolveAsync(HttpContext context, RequestDelegate next)
    {
        if (FromRequest(context) is not null ||
            context.GetEndpoint() is not RouteEndpoint endpoint ||
            GeoServicesServiceRoutes.Match(endpoint.RoutePattern) is not { } route)
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        var incoming = GeoServicesServiceRoutes.FromRequest(context);
        var publicName = incoming?.PublicName ?? context.Request.RouteValues[route.ServiceParameter]?.ToString() ?? string.Empty;
        var folder = incoming?.FolderName;
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        MetadataV2GraphSnapshot snapshot;
        try
        {
            snapshot = await provider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (ServiceResourceValidationHelpers.IsCatalogStorageUnavailable(exception))
        {
            await StandardErrorHelpers.CreateServiceUnavailable(context,
                ServiceResourceValidationHelpers.ServiceCatalogUnavailableMessage, retryable: true)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        var publicPath = new MetadataV2ServicePathAlias
        {
            Tenant = TenantScopeHelpers.ResolveRequestTenantId(context),
            FolderName = folder,
            PublicName = publicName
        };
        var services = MetadataV2ServicePaths.Resolve(snapshot.Graph, publicPath);
        var names = services.Select(service => service.Metadata.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length != 1)
        {
            if (names.Length == 0 && folder is null && IsStandaloneRoute(route, publicPath, snapshot.Graph))
            {
                await next(context).ConfigureAwait(false);
                return;
            }
            await StandardErrorHelpers.CreateNotFound(context, "Service was not found.")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        context.Items[BindingKey] = new GeoServicesServicePath(route.Root, folder, publicName, snapshot,
            services.Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal));
        context.Request.RouteValues[route.ServiceParameter] = names[0];
        await next(context).ConfigureAwait(false);
    }

    private static bool IsStandaloneRoute(GeoServicesServiceRoutes.ServiceRoute route, MetadataV2ServicePathAlias path, MetadataV2Graph graph)
    {
        // The provider Routing namespace defers stable-ID collisions to the GPServer validator.
        var isRoutingUtilityNamespace = route.ServiceType == "GPServer" &&
            string.Equals(path.PublicName, "Routing", StringComparison.OrdinalIgnoreCase);
        return route.ServiceType is "GPServer" or "NAServer" &&
            !graph.ServicePathAliases.Any(alias => MetadataV2ServicePaths.SamePath(alias, path) &&
                MetadataV2TenantVisibility.IsVisibleToTenant(alias.Tenant, path.Tenant)) &&
            !graph.Services.Any(service =>
                string.Equals(service.Metadata.Name, path.PublicName, StringComparison.OrdinalIgnoreCase)) &&
            (isRoutingUtilityNamespace || !graph.Services.Any(service =>
                string.Equals(service.Metadata.Id, path.PublicName, StringComparison.Ordinal)));
    }
}
