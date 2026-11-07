// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Portal.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.FeatureServer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.Sharing;

// arcpy reads this admin resource without the workspace token, so an unauthenticated read is allowed and a hidden service is 404 rather than 403, publishing no more than the public service root already does.
public static class GeoServicesServerAdminEndpoints
{
    private const string JsonContentType = "application/json";
    private const string RouteTemplate = "/admin/services/{serviceName}.{serviceType}";

    private const string SitelessRouteTemplate = "/rest/admin/{serviceName}.{serviceType}";

    public static IEndpointRouteBuilder MapGeoServicesServerAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMethods(RouteTemplate, ["GET", "POST"], HandleServiceResourceAsync)
            .WithDisplayName("ArcGIS Server Admin Service Resource")
            .WithName("GeoServicesServerAdminServiceResource")
            .WithSummary("Read-only ArcGIS Server Admin API service resource")
            .WithDescription("Describes a published service the way the ArcGIS Server Admin API does (type, capabilities, extensions, branch-versioning availability); arcpy's branch-versioning tools validate a feature-service workspace through it.")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces(StatusCodes.Status200OK, contentType: JsonContentType);

        endpoints.MapMethods(SitelessRouteTemplate, ["GET", "POST"], HandleServiceResourceAsync)
            .WithDisplayName("ArcGIS Server Admin Service Resource (siteless spelling)")
            .WithName("GeoServicesServerAdminServiceResourceSiteless")
            .WithSummary("Read-only ArcGIS Server Admin API service resource, as ArcGIS Pro requests it from a connection without a site segment")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces(StatusCodes.Status200OK, contentType: JsonContentType);

        foreach (var (path, name) in new[]
        {
            ("/admin/services/{folderName}/{serviceName}.{serviceType}", "GeoServicesFolderAdminServiceResource"),
            ("/rest/admin/{folderName}/{serviceName}.{serviceType}", "GeoServicesFolderAdminServiceResourceSiteless")
        })
        {
            endpoints.MapMethods(path, ["GET", "POST"], HandleServiceResourceAsync)
                .WithName(name)
                .WithTags("GeoServices Sharing")
                .AllowAnonymous()
                .Produces(StatusCodes.Status200OK, contentType: JsonContentType);
        }

        return endpoints;
    }

    private static async Task<IResult> HandleServiceResourceAsync(
        HttpContext context,
        string serviceName,
        string serviceType,
        string? f,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] IPortalItemProjector projector)
    {
        if (!string.IsNullOrWhiteSpace(f)
            && !f.Equals("json", StringComparison.OrdinalIgnoreCase)
            && !f.Equals("pjson", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson.");
        }

        // Deliberately NOT refusing an anonymous caller here. The original rule assumed a
        // client holding a portal token would retry with token=; measurement says
        // otherwise. arcpy composes this request itself, so a token appended to the
        // workspace URL never reaches it, and SignInToPortal does not help either -
        // ArcGIS Pro attaches a portal session token only to servers the portal declares
        // federated, and a stand-alone Honua site is not one. A single MakeWCSLayer call
        // was recorded issuing GET /rest/admin/{service}.MapServer and taking the 401 as
        // final, after a successful SignInToPortal. CreateVersion does the same and stops
        // at "ERROR 000301: The workspace is of the wrong type" - which is the whole of
        // #5036.
        //
        // Nothing is widened by serving it. Visibility is still decided per principal,
        // twice and below: ProjectItem answers null for a service this caller cannot see
        // (404, never 403), and FilterAccessibleResourcesAsync reduces the layer set the
        // capabilities are computed from. What is left to publish - protocols,
        // capabilities, maxRecordCount, branch-versioning availability - is exactly what
        // /rest/services/{service}/FeatureServer already states to the same caller, and
        // the remaining fields are the fixed strings an Admin API response carries.

        var snapshot = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var requestedType = serviceType.Trim();
        var service = ResolveService(snapshot, serviceName, requestedType, context);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        if (service is null || projector.ProjectItem(snapshot, context.User, service.Metadata.Id, baseUrl) is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Service '{serviceName}.{serviceType}' was not found.");
        }

        var protocols = new HashSet<string>(service.Protocols, StringComparer.OrdinalIgnoreCase);
        var branchVersioning = await FeatureServerEndpoints.HasAccessibleBranchVersionedPublicationsAsync(
            context, service, snapshot, context.RequestAborted).ConfigureAwait(false);
        var versionManagement = FeatureServerEndpoints.IsVersionManagementAvailable(context, branchVersioning);
        var featureServer = protocols.Contains("FeatureServer");
        var queryLimits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Query;
        var maxRecordCount = queryLimits.MaxRecordCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? featureCapabilities = null;
        var allowGeometryUpdates = false;
        if (featureServer)
        {
            var (visiblePairs, accessError) = await AccessPolicyHelpers.FilterAccessibleResourcesAsync(
                context,
                FeatureServerEndpoints.GetRoutableFeaturePublicationsV2(service, snapshot, context),
                static pair => pair.Resource,
                service,
                AuthorizationOperation.Metadata,
                context.RequestAborted).ConfigureAwait(false);
            if (accessError is not null)
            {
                return accessError;
            }

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var effectiveFeatureMetadata = FeatureServerEndpoints.MapServiceToResponseV2(
                service,
                visiblePairs,
                snapshot,
                queryLimits,
                supportsGeobufOutput: featureReader is IGeobufFeatureStore,
                supportsAttachmentUploads: FeatureServerEndpoints.HasAttachmentSurface(context.RequestServices),
                branchVersioningEnabled: branchVersioning,
                versionManagementEnabled: versionManagement,
                offlineSyncEnabled: CapabilityFlagOptions.IsExperimentalEnabled(
                    context.RequestServices.GetRequiredService<IConfiguration>(), "sync.offline"));
            featureCapabilities = effectiveFeatureMetadata.Capabilities;
            allowGeometryUpdates = effectiveFeatureMetadata.AllowGeometryUpdates;
        }
        var document = new JsonObject
        {
            ["serviceName"] = service.EffectivePublicName,
            ["type"] = requestedType,
            ["description"] = service.Metadata.Description ?? string.Empty,
            ["capabilities"] = requestedType.Equals("MapServer", StringComparison.OrdinalIgnoreCase) ? "Map,Query,Data" : "Query",
            ["provider"] = "SDE",
            ["clusterName"] = "default",
            ["configuredState"] = "STARTED",
            ["isDefault"] = false,
            ["isPrivate"] = false,
            ["properties"] = new JsonObject
            {
                ["isBranchVersioned"] = Flag(branchVersioning && featureServer),
                ["isDataVersioned"] = Flag(branchVersioning && featureServer),
                ["maxRecordCount"] = maxRecordCount,
            },
            ["extensions"] = BuildExtensions(protocols, branchVersioning, versionManagement,
                featureCapabilities, maxRecordCount, allowGeometryUpdates),
            ["datasets"] = new JsonArray(),
            ["portalProperties"] = null,
        };

        var pretty = f is not null && f.Equals("pjson", StringComparison.OrdinalIgnoreCase);
        return Results.Text(document.ToJsonString(new JsonSerializerOptions { WriteIndented = pretty }), JsonContentType);
    }

    private static JsonArray BuildExtensions(HashSet<string> protocols, bool branchVersioning, bool versionManagement,
        string? featureCapabilities, string maxRecordCount, bool allowGeometryUpdates)
    {
        var extensions = new JsonArray();
        if (protocols.Contains("FeatureServer"))
        {
            extensions.Add(Extension("FeatureServer", featureCapabilities ?? string.Empty, new JsonObject
            {
                ["maxRecordCount"] = maxRecordCount,
                ["allowGeometryUpdates"] = Flag(allowGeometryUpdates),
                ["enableZDefaults"] = "false",
                ["isBranchVersioned"] = Flag(branchVersioning),
                ["allowTrueCurvesUpdates"] = "false",
            }));
            if (versionManagement)
            {
                extensions.Add(Extension("VersionManagementServer", string.Empty, new JsonObject()));
            }
        }

        if (protocols.Contains("Wfs20"))
        {
            extensions.Add(Extension("WFSServer", string.Empty, new JsonObject()));
        }

        if (protocols.Contains("Wms"))
        {
            extensions.Add(Extension("WMSServer", string.Empty, new JsonObject()));
        }

        if (protocols.Contains(ServiceProtocols.Wcs) || protocols.Contains(ServiceProtocols.ImageServer))
        {
            extensions.Add(Extension("WCSServer", string.Empty, new JsonObject()));
        }

        if (protocols.Contains("VectorTileServer"))
        {
            extensions.Add(Extension("VectorTileServer", string.Empty, new JsonObject()));
        }

        return extensions;
    }

    private static JsonObject Extension(string typeName, string capabilities, JsonObject properties)
        => new()
        {
            ["typeName"] = typeName,
            ["capabilities"] = capabilities,
            ["enabled"] = "true",
            ["maxUploadFileSize"] = 0,
            ["allowedUploadFileTypes"] = string.Empty,
            ["properties"] = properties,
        };

    // The Admin API spells booleans as strings.
    private static JsonValue Flag(bool value) => JsonValue.Create(value ? "true" : "false");

    private static MetadataV2Service? ResolveService(
        MetadataV2GraphSnapshot snapshot, string serviceName, string requestedType, HttpContext context)
    {
        var folder = context.Request.RouteValues["folderName"]?.ToString();
        var tenant = TenantScopeHelpers.ResolveRequestTenantId(context);
        var path = new MetadataV2ServicePathAlias { FolderName = folder, PublicName = serviceName, Tenant = tenant };
        var candidates = MetadataV2ServicePaths.Resolve(snapshot.Graph, path);
        if (candidates.Count > 0 || folder is not null)
            return SelectService(candidates, requestedType);
        if (snapshot.Graph.ServicePathAliases.Any(alias => MetadataV2ServicePaths.SamePath(alias, path) &&
            MetadataV2TenantVisibility.IsVisibleToTenant(alias.Tenant, tenant)))
            return null;

        // Desktop probes omit the folder, so only one logical service may own the requested public leaf.
        var folders = MetadataV2ServicePaths.CanonicalPaths(snapshot.Graph).Concat(snapshot.Graph.ServicePathAliases)
            .Where(candidate => string.Equals(candidate.PublicName, serviceName, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.FolderName).Distinct(StringComparer.OrdinalIgnoreCase);
        var leafCandidates = folders.SelectMany(folderName => MetadataV2ServicePaths.Resolve(snapshot.Graph,
            path with { FolderName = folderName })).DistinctBy(service => service.Metadata.Id).ToArray();
        if (leafCandidates.Length > 0)
        {
            return leafCandidates.Select(service => service.Metadata.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() == 1
                ? SelectService(leafCandidates, requestedType) : null;
        }

        var identityMatch = snapshot.Index.ServicesByName.GetValueOrDefault(serviceName)
            ?? snapshot.Index.ServicesById.GetValueOrDefault(serviceName);
        if (identityMatch is not null && TenantScopeHelpers.IsTenantVisible(context, null, identityMatch))
            return SelectService([identityMatch], requestedType);
        if (!int.TryParse(serviceName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
            return null;
        var layerServices = snapshot.Graph.Publications
            .Where(publication => publication.LayerIndex == layerId && snapshot.IsRoutable(publication))
            .Select(publication => (Publication: publication, Service: snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ? service : null))
            .Where(entry => entry.Service is not null && TenantScopeHelpers.IsPublicationVisible(
                context, entry.Publication, snapshot.ResolveResource(entry.Publication), entry.Service))
            .Select(entry => entry.Service!);
        return SelectService(layerServices, requestedType);
    }

    private static MetadataV2Service? SelectService(IEnumerable<MetadataV2Service> services, string requestedType)
    {
        MetadataV2Service? feature = null;
        MetadataV2Service? coverage = null;
        var mapServerRequest = requestedType.Equals(ServiceProtocols.MapServer, StringComparison.OrdinalIgnoreCase);
        foreach (var service in services)
        {
            if (ServiceProtocols.IsProtocolEnabled(service, requestedType)) return service;
            if (!mapServerRequest) continue;
            if (ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.FeatureServer)) feature ??= service;
            else if (ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.ImageServer) ||
                ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.Wcs)) coverage ??= service;
        }
        return feature ?? coverage;
    }
}
