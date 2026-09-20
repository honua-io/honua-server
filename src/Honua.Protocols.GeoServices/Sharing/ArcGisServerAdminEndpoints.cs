// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Portal.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

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
/// error). The resource is the service definition an ArcGIS Server administrator sees;
/// this projection publishes only what the public FeatureServer root already states
/// (protocols, capabilities, branch-versioning availability) and nothing about hosting.
/// </para>
/// <para>
/// Reads require an authenticated principal that can read the service, decided by the
/// same portal item projector the Sharing facade uses, so a hidden service stays hidden
/// (404, never 403) and an anonymous caller is refused (401), as on ArcGIS Server where
/// the Admin API requires a token. arcpy sends its portal token as <c>token=</c>, which
/// the portal token authentication handler accepts on every route.
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

        if (context.User.Identity?.IsAuthenticated != true)
        {
            // ArcGIS Server's Admin API refuses an unauthenticated read outright; clients
            // that hold a portal token retry with token= (query or form), never anonymously.
            return StandardErrorHelpers.CreateUnauthorized(context, "Authentication is required to read the service definition.");
        }

        var snapshot = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        if (!snapshot.Index.ServicesByName.TryGetValue(serviceName, out var service)
            && !snapshot.Index.ServicesById.TryGetValue(serviceName, out service))
        {
            service = null;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        if (service is null || projector.ProjectItem(snapshot, context.User, service.Metadata.Id, baseUrl) is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Service '{serviceName}.{serviceType}' was not found.");
        }

        var protocols = new HashSet<string>(service.Protocols, StringComparer.OrdinalIgnoreCase);
        var requestedType = serviceType.Trim();
        var servesRequestedType = requestedType.Equals("MapServer", StringComparison.OrdinalIgnoreCase)
            ? protocols.Contains("MapServer") || protocols.Contains("FeatureServer")
            : protocols.Contains(requestedType);
        if (!servesRequestedType)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Service '{serviceName}.{serviceType}' was not found.");
        }

        var branchVersioning = IsBranchVersioningAvailable(context);
        var featureServer = protocols.Contains("FeatureServer");
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
                ["maxRecordCount"] = "2000",
            },
            ["extensions"] = BuildExtensions(protocols, branchVersioning),
            ["datasets"] = new JsonArray(),
            ["portalProperties"] = null,
        };

        var pretty = f is not null && f.Equals("pjson", StringComparison.OrdinalIgnoreCase);
        return Results.Text(document.ToJsonString(new JsonSerializerOptions { WriteIndented = pretty }), JsonContentType);
    }

    private static JsonArray BuildExtensions(HashSet<string> protocols, bool branchVersioning)
    {
        var extensions = new JsonArray();
        if (protocols.Contains("FeatureServer"))
        {
            extensions.Add(Extension("FeatureServer", "Query,Create,Update,Delete,Uploads,Editing", new JsonObject
            {
                ["maxRecordCount"] = "2000",
                ["allowGeometryUpdates"] = "true",
                ["enableZDefaults"] = "false",
                ["isBranchVersioned"] = Flag(branchVersioning),
                ["allowTrueCurvesUpdates"] = "false",
            }));
            if (branchVersioning)
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

        if (protocols.Contains("Wcs"))
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
    /// The same decision the FeatureServer root makes for supportsBranchVersioning: a
    /// version manager that supports versioning plus the branch-versioning entitlement.
    /// </summary>
    private static bool IsBranchVersioningAvailable(HttpContext context)
    {
        IVersionManager? versionManager;
        try
        {
            versionManager = context.RequestServices.GetService<IVersionManager>();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (versionManager is not { SupportsVersioning: true })
        {
            return false;
        }

        return LicenseGate.IsEntitlementActive(context.RequestServices, FeatureCatalog.BranchVersioningKey);
    }
}
