// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// A read-only projection of the ArcGIS Server Admin API service resource,
/// <c>/admin/services/{serviceName}.{serviceType}</c> (#5036).
/// </summary>
/// <remarks>
/// <para>
/// arcpy's branch-versioning tools (CreateVersion, ReconcileVersions, DeleteVersion)
/// accept a feature service URL as their workspace and validate it through this Admin
/// API resource before they will treat the service as branch versioned; with the
/// resource absent they fail "ERROR 000301: The workspace is of the wrong type"
/// (ArcGIS Pro 3.7.1, 2026-09-19, fourteen GETs of the resource recorded before the
/// error). <c>MakeWCSLayer</c> probes the same document with the coverage URL's integer
/// layer id (<c>/rest/admin/0.MapServer</c>) and stops on a 404 envelope. The resource
/// is the service definition an ArcGIS Server administrator sees; this projection
/// publishes only what the public service root already states (protocols, capabilities,
/// branch-versioning availability, a WCS extension when the service serves coverages)
/// and nothing about hosting.
/// </para>
/// <para>
/// Visibility is decided by the same portal item projector the Sharing facade uses, so
/// a service the caller cannot see stays hidden (404, never 403) and the capabilities
/// are computed only from the layers that caller may read. Reads are <b>not</b>
/// additionally gated on being authenticated: arcpy composes this request itself, so a
/// token on the workspace URL never reaches it, and Pro attaches a portal session token
/// only to federated servers - so an authenticated-only resource is one arcpy can never
/// read, which is what left #5036 failing after the resource existed. Serving it
/// anonymously publishes no more than the public FeatureServer root already does.
/// </para>
/// </remarks>
public static class ArcGisServerAdminEndpoints
{
    private const string JsonContentType = "application/json";
    private const string RouteTemplate = "/admin/services/{serviceName}.{serviceType}";

    /// <summary>
    /// The spelling ArcGIS Pro 3.7.1 uses when the server connection has no site segment:
    /// <c>/rest/admin/{service}.MapServer</c> (three reads per layer add, observed live).
    /// With an <c>/arcgis</c> site segment it asks <c>/arcgis/admin/services/...</c> instead.
    /// </summary>
    private const string SitelessRouteTemplate = "/rest/admin/{serviceName}.{serviceType}";

    /// <summary>Maps the Admin API service resource.</summary>
    public static IEndpointRouteBuilder MapArcGisServerAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMethods(RouteTemplate, ["GET", "POST"], HandleServiceResourceAsync)
            .WithDisplayName("ArcGIS Server Admin Service Resource")
            .WithName("ArcGisServerAdminServiceResource")
            .WithSummary("Read-only ArcGIS Server Admin API service resource")
            .WithDescription("Describes a published service the way the ArcGIS Server Admin API does (type, capabilities, extensions, branch-versioning availability); arcpy's branch-versioning tools validate a feature-service workspace through it.")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces(StatusCodes.Status200OK, contentType: JsonContentType);

        endpoints.MapMethods(SitelessRouteTemplate, ["GET", "POST"], HandleServiceResourceAsync)
            .WithDisplayName("ArcGIS Server Admin Service Resource (siteless spelling)")
            .WithName("ArcGisServerAdminServiceResourceSiteless")
            .WithSummary("Read-only ArcGIS Server Admin API service resource, as ArcGIS Pro requests it from a connection without a site segment")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces(StatusCodes.Status200OK, contentType: JsonContentType);

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
        var service = ResolveService(snapshot, serviceName, requestedType);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        if (service is null || projector.ProjectItem(snapshot, context.User, service.Metadata.Id, baseUrl) is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Service '{serviceName}.{serviceType}' was not found.");
        }

        var protocols = new HashSet<string>(service.Protocols, StringComparer.OrdinalIgnoreCase);
        var servesRequestedType = requestedType.Equals(ServiceProtocols.MapServer, StringComparison.OrdinalIgnoreCase)
            ? protocols.Contains(ServiceProtocols.MapServer)
                || protocols.Contains(ServiceProtocols.FeatureServer)
                || protocols.Contains(ServiceProtocols.ImageServer)
                || protocols.Contains(ServiceProtocols.Wcs)
            : protocols.Contains(requestedType);
        if (!servesRequestedType)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Service '{serviceName}.{serviceType}' was not found.");
        }

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
                FeatureServerEndpoints.GetRoutableFeaturePublicationsV2(service, snapshot),
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
            ["serviceName"] = service.Metadata.Name,
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

    /// <summary>
    /// What the VersionManagementServer resource advertises, mirrored onto its admin
    /// extension entry so the two documents cannot disagree.
    /// </summary>
    private const string VersionManagementCapabilities = "Create,Delete,Alter,Reconcile,Post";

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
                // The extension has to state what the VersionManagementServer can do. An
                // empty capabilities string here says "this extension supports nothing",
                // and a client deciding whether a feature-service workspace is versioned
                // reads the ADMIN document rather than the VMS resource - which is why
                // arcpy.management.CreateVersion still answered "ERROR 000301: The
                // workspace is of the wrong type" after it began reaching the
                // VersionManagementServer successfully (honua-server#5036). The value is
                // the same one the VMS resource itself advertises, so the two agree.
                extensions.Add(Extension(
                    "VersionManagementServer",
                    VersionManagementCapabilities,
                    new JsonObject()));
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

    /// <summary>The Admin API spells booleans as strings.</summary>
    private static JsonValue Flag(bool value) => JsonValue.Create(value ? "true" : "false");

    /// <summary>
    /// Resolves the admin service key. A service name or id wins. An integer that matches
    /// neither is the storage-layer id arcpy copies out of <c>/rest/services/{id}/ImageServer/WCS</c>
    /// when it asks for <c>{id}.MapServer</c>.
    /// </summary>
    private static MetadataV2Service? ResolveService(
        MetadataV2GraphSnapshot snapshot, string serviceName, string requestedType)
    {
        if (snapshot.Index.ServicesByName.TryGetValue(serviceName, out var byName))
        {
            return byName;
        }

        if (snapshot.Index.ServicesById.TryGetValue(serviceName, out var byId))
        {
            return byId;
        }

        if (!int.TryParse(serviceName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return null;
        }

        MetadataV2Service? exact = null;
        MetadataV2Service? feature = null;
        MetadataV2Service? coverage = null;
        var mapServerRequest = requestedType.Equals(ServiceProtocols.MapServer, StringComparison.OrdinalIgnoreCase);
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (publication.LayerIndex != layerId || !snapshot.IsRoutable(publication))
            {
                continue;
            }

            if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service))
            {
                continue;
            }

            if (ServiceProtocols.IsProtocolEnabled(service, requestedType))
            {
                exact ??= service;
                continue;
            }

            if (!mapServerRequest)
            {
                continue;
            }

            if (ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.FeatureServer))
            {
                feature ??= service;
            }
            else if (ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.ImageServer)
                || ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.Wcs))
            {
                coverage ??= service;
            }
        }

        return exact ?? feature ?? coverage;
    }
}
