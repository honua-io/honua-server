// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Honua.Infrastructure.Helpers;

internal static class GeoServicesServiceRoutes
{
    private const string FolderRoutingServiceName = "__honua_folder_service__";
    private static readonly object IncomingPathKey = new();
    private static readonly HashSet<string> NamedServiceTypes = new HashSet<string>(
        ["FeatureServer", "MapServer", "ImageServer", "VectorTileServer", "GPServer", "NAServer", "VersionManagementServer"],
        StringComparer.OrdinalIgnoreCase);

    internal sealed record ServiceRoute(string Root, string ServiceType, string ServiceParameter);
    internal sealed record CatalogRoute;
    internal sealed record IncomingPath(PathString PublicPath, PathString RoutingPath, string FolderName, string PublicName);

    public static IncomingPath? FromRequest(HttpContext context)
        => context.Items.TryGetValue(IncomingPathKey, out var value) ? value as IncomingPath : null;

    public static PathString RoutingPath(HttpContext context) => FromRequest(context)?.RoutingPath ?? context.Request.Path;

    public static IApplicationBuilder UseGeoServicesFolderPaths(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            if (ParseFolderPath(context.Request.Path) is not { } path)
            {
                await next(context).ConfigureAwait(false);
                return;
            }
            context.Items[IncomingPathKey] = path;
            context.Request.Path = path.RoutingPath;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                context.Request.Path = path.PublicPath;
            }
        });

    public static IApplicationBuilder UseGeoServicesPublicPaths(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            if (FromRequest(context) is { } path)
            {
                context.Request.Path = path.PublicPath;
                if (context.Request.RouteValues.ContainsKey("serviceId"))
                    context.Request.RouteValues["serviceId"] = path.PublicName;
            }
            await next(context).ConfigureAwait(false);
        });

    public static IApplicationBuilder UseGeoServicesServicePaths(this IApplicationBuilder app)
        => app.Use(GeoServicesServicePath.ResolveAsync);

    internal static ServiceRoute? Match(RoutePattern pattern)
    {
        var segments = pattern.PathSegments;
        var offset = segments.Count > 0 && Literal(segments[0]) == "rest" ? 2 : 1;
        if (segments.Count <= offset + 1 || Literal(segments[offset - 1]) != "services") return null;
        if (segments[offset].Parts is not [RoutePatternParameterPart parameter] ||
            parameter.Name != "serviceId" || parameter.ParameterPolicies.Any(policy => policy.Content == "int")) return null;
        var serviceType = Literal(segments[offset + 1]);
        return serviceType is not null && NamedServiceTypes.Contains(serviceType)
            ? new(offset == 2 ? "/rest/services" : "/services", serviceType, parameter.Name) : null;
    }

    private static IncomingPath? ParseFolderPath(PathString path)
    {
        var segments = (path.Value ?? string.Empty).Split('/');
        var offset = segments.Length > 2 && string.Equals(segments[1], "rest", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
        if (segments.Length <= offset + 2 || !string.Equals(segments[offset - 1], "services", StringComparison.OrdinalIgnoreCase)) return null;
        var folder = segments[offset];
        var publicName = segments[offset + 1];
        if (!NamedServiceTypes.Contains(segments[offset + 2]) || !MetadataV2ServicePaths.IsFolderName(folder) ||
            !MetadataV2ServicePaths.IsIdentifier(publicName) || MetadataV2ServicePaths.ServiceTypes.Contains(publicName)) return null;
        var routingSegments = segments.Take(offset).Append(FolderRoutingServiceName).Concat(segments.Skip(offset + 2));
        return new(path, new PathString(string.Join('/', routingSegments)), folder, publicName);
    }

    private static string? Literal(RoutePatternPathSegment segment)
        => segment.Parts is [RoutePatternLiteralPart literal] ? literal.Content : null;
}
