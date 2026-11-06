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
            context.GetEndpoint()?.Metadata.GetMetadata<GeoServicesServiceRoutes.ServiceRoute>() is not { } route)
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        var publicName = context.Request.RouteValues[route.ServiceParameter]?.ToString() ?? string.Empty;
        var folder = context.Request.RouteValues["folderName"]?.ToString();
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
        var services = MetadataV2ServicePaths.Resolve(snapshot.Graph, new()
        {
            Tenant = TenantScopeHelpers.ResolveRequestTenantId(context),
            FolderName = folder,
            PublicName = publicName
        });
        var names = services.Select(service => service.Metadata.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length != 1)
        {
            if (names.Length == 0 && folder is null && IsStandaloneRoute(route, publicName, snapshot.Graph))
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

    private static bool IsStandaloneRoute(GeoServicesServiceRoutes.ServiceRoute route, string name, MetadataV2Graph graph)
        => route.ServiceType is "GPServer" or "NAServer" && !graph.Services.Any(service =>
            string.Equals(service.Metadata.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service.Metadata.Id, name, StringComparison.Ordinal));
}
