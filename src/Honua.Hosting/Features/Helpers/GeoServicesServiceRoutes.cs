// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Helpers;

internal static class GeoServicesServiceRoutes
{
    private const string NonNumericServiceConstraint = @"regex(^(?!\d+$).+$)";
    internal sealed record ServiceRoute(string Root, string ServiceType, string ServiceParameter);
    internal sealed record CatalogRoute;

    public static void MapGeoServicesServicePaths(this IEndpointRouteBuilder endpoints)
    {
        foreach (var source in endpoints.DataSources.ToArray())
        {
            if (source is ServicePathDataSource) continue;
            endpoints.DataSources.Remove(source);
            endpoints.DataSources.Add(new ServicePathDataSource(source));
        }
    }

    public static IApplicationBuilder UseGeoServicesServicePaths(this IApplicationBuilder app)
        => app.Use(GeoServicesServicePath.ResolveAsync);

    private static ServiceRoute? Match(RoutePattern pattern)
    {
        var segments = pattern.PathSegments;
        var offset = segments.Count > 0 && Literal(segments[0]) == "rest" ? 2 : 1;
        if (segments.Count <= offset + 1 || Literal(segments[offset - 1]) != "services") return null;
        if (segments[offset].Parts is not [RoutePatternParameterPart parameter] ||
            parameter.Name != "serviceId" ||
            parameter.ParameterPolicies.Any(policy => policy.Content == "int")) return null;
        var serviceType = Literal(segments[offset + 1]);
        return serviceType is "FeatureServer" or "MapServer" or "ImageServer" or "VectorTileServer" or
            "GPServer" or "NAServer" or "VersionManagementServer"
            ? new(offset == 2 ? "/rest/services" : "/services", serviceType, parameter.Name) : null;
    }

    private static string? Literal(RoutePatternPathSegment segment)
        => segment.Parts is [RoutePatternLiteralPart literal] ? literal.Content : null;

    private sealed class ServicePathDataSource(EndpointDataSource source) : EndpointDataSource
    {
        private readonly Lock _gate = new();
        private IReadOnlyList<Endpoint>? _original;
        private IReadOnlyList<Endpoint>? _projected;

        public override IChangeToken GetChangeToken() => source.GetChangeToken();

        public override IReadOnlyList<Endpoint> Endpoints
        {
            get
            {
                lock (_gate)
                {
                    var original = source.Endpoints;
                    if (!ReferenceEquals(original, _original))
                    {
                        _projected = original.SelectMany(Project).ToArray();
                        _original = original;
                    }
                    return _projected!;
                }
            }
        }

        private static IEnumerable<Endpoint> Project(Endpoint endpoint)
        {
            if (endpoint is not RouteEndpoint route || Match(route.RoutePattern) is not { } serviceRoute)
            {
                yield return endpoint;
                yield break;
            }
            yield return Copy(route, route.RoutePattern, serviceRoute);
            var template = route.RoutePattern.RawText!;
            var serviceParameterOffset = template.IndexOf("{serviceId", StringComparison.Ordinal);
            var qualified = template.Insert(serviceParameterOffset, "{folderName}/");
            if (serviceRoute.ServiceType == "ImageServer")
            {
                qualified = qualified.Replace($"{{serviceId:{NonNumericServiceConstraint}}}", "{serviceId}", StringComparison.Ordinal);
            }
            yield return Copy(route, RoutePatternFactory.Parse(qualified), serviceRoute);
        }

        private static RouteEndpoint Copy(RouteEndpoint original, RoutePattern pattern, ServiceRoute serviceRoute)
        {
            var builder = new RouteEndpointBuilder(
                context => GeoServicesServicePath.ResolveAsync(context, original.RequestDelegate!), pattern, original.Order)
            {
                DisplayName = original.DisplayName
            };
            var qualified = pattern.Parameters.Any(parameter => parameter.Name == "folderName");
            foreach (var metadata in original.Metadata)
            {
                builder.Metadata.Add(qualified ? metadata switch
                {
                    IEndpointNameMetadata name => new EndpointNameMetadata(name.EndpointName + "Folder"),
                    IRouteNameMetadata name => new RouteNameMetadata(name.RouteName + "Folder"),
                    _ => metadata
                } : metadata);
            }
            builder.Metadata.Add(serviceRoute);
            return (RouteEndpoint)builder.Build();
        }
    }
}
