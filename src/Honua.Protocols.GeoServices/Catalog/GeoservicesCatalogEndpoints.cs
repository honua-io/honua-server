// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Honua.Protocols.GeoServices.Catalog;

/// <summary>
/// GeoServices catalog endpoints for service discovery.
/// </summary>
internal static class GeoservicesCatalogEndpoints
{
    private const string JsonFormat = "json";
    private const string PrettyJsonFormat = "pjson";
    private const string JsonContentType = "application/json";
    private const string FeatureServerProtocolName = "FeatureServer";
    private const string MapServerProtocolName = "MapServer";
    private const string ImageServerProtocolName = "ImageServer";
    private const string GPServerProtocolName = "GPServer";
    private const string SceneServerProtocolName = "SceneServer";
    private const string VectorTileServerProtocolName = "VectorTileServer";
    private const string Soap11ContentType = "text/xml; charset=utf-8";
    private const string Soap12ContentType = "application/soap+xml; charset=utf-8";
    private const string Soap11EnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Soap12EnvelopeNamespace = "http://www.w3.org/2003/05/soap-envelope";
    private const string ArcGisSoapNamespace = "http://www.esri.com/schemas/ArcGIS/10.8";
    private const int MaxSoapRequestCharacters = 1_048_576;

    /// <summary>
    /// Maps root catalog endpoints under /rest.
    /// </summary>
    public static IEndpointRouteBuilder MapGeoservicesCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rest/services", HandleGetServicesDirectory)
            .WithDisplayName("GeoServices Services Directory")
            .WithName("GeoServicesServicesDirectory")
            .WithSummary("List available GeoServices endpoints")
            .WithDescription("Returns FeatureServer, MapServer, ImageServer, GPServer, VectorTileServer, and (Enterprise) SceneServer service directory entries.")
            .WithTags("GeoServices Catalog")
            .CacheOutput("ServiceDirectory")
            .Produces<ServicesDirectoryResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest);

        endpoints.MapGet("/rest/info", HandleGetRestInfo)
            .WithDisplayName("GeoServices REST Info")
            .WithName("GeoServicesRestInfo")
            .WithSummary("Get REST root metadata")
            .WithDescription("Returns root-level GeoServices metadata.")
            .WithTags("GeoServices Catalog")
            .Produces<RestInfoResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/services", HandlePostSoapCatalog)
            .WithDisplayName("ArcGIS SOAP Services Catalog")
            .WithName("ArcGisSoapServicesCatalog")
            .WithSummary("Discover SOAP-compatible ImageServer services")
            .WithDescription("Implements ArcGIS Server SOAP catalog negotiation for raster-backed ImageServer services.")
            .WithTags("GeoServices Catalog")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["text/xml"] = new(),
                        ["application/soap+xml"] = new()
                    }
                };
                return Task.CompletedTask;
            })
            .Produces(StatusCodes.Status200OK, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status400BadRequest, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status415UnsupportedMediaType, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status503ServiceUnavailable, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> HandlePostSoapCatalog(
        HttpContext context,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] IRasterStore rasterStore,
        [FromServices] IOptions<PortalTokenAuthenticationOptions> tokenOptions,
        [FromServices] ILogger<GeoservicesCatalogLog> logger)
    {
        if (!IsSupportedSoapContentType(context.Request.ContentType))
        {
            var requestedSoap = RequestedSoapNamespace(context.Request);
            return CreateSoapFault(
                "Content-Type must be text/xml or application/soap+xml.",
                StatusCodes.Status415UnsupportedMediaType,
                requestedSoap);
        }

        XDocument request;
        try
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxSoapRequestCharacters
            };
            using var reader = XmlReader.Create(context.Request.Body, settings);
            request = await XDocument.LoadAsync(
                reader,
                LoadOptions.None,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Xml.XmlException)
        {
            return CreateSoapFault(
                "Malformed SOAP request.",
                StatusCodes.Status400BadRequest,
                RequestedSoapNamespace(context.Request));
        }

        var envelopeNamespace = request.Root?.Name.Namespace;
        if (request.Root?.Name.LocalName != "Envelope" ||
            (envelopeNamespace != Soap11EnvelopeNamespace && envelopeNamespace != Soap12EnvelopeNamespace))
        {
            return CreateSoapFault(
                "Unsupported SOAP envelope namespace.",
                StatusCodes.Status400BadRequest,
                RequestedSoapNamespace(context.Request));
        }

        XNamespace soap = envelopeNamespace;
        var bodies = request.Root.Elements(soap + "Body").Take(2).ToArray();
        if (bodies.Length != 1)
        {
            return CreateSoapFault(
                "SOAP envelope must contain exactly one Body element.",
                StatusCodes.Status400BadRequest,
                soap);
        }

        var operations = bodies[0].Elements().Take(2).ToArray();
        if (operations is not { Length: 1 })
        {
            return CreateSoapFault(
                "SOAP body must contain exactly one catalog operation.",
                StatusCodes.Status400BadRequest,
                soap);
        }

        var operation = operations[0];

        var operationNamespace = operation.Name.Namespace;
        if (operationNamespace != ArcGisSoapNamespace)
        {
            return CreateSoapFault(
                "Unsupported ArcGIS SOAP operation namespace.",
                StatusCodes.Status400BadRequest,
                soap);
        }

        try
        {
            XElement payload;
            switch (operation.Name.LocalName)
            {
            case "GetServiceDescriptions":
                payload = new XElement(
                    operationNamespace + "GetServiceDescriptionsResult",
                    await BuildSoapImageServerDescriptionsAsync(
                        context,
                        operationNamespace,
                        graphProvider,
                        rasterStore,
                        logger,
                        folderName: null).ConfigureAwait(false));
                break;
            case "GetServiceDescriptionsEx":
                var arguments = operation.Elements().ToArray();
                if (arguments.Length > 1 ||
                    arguments.Any(argument =>
                        argument.Name.Namespace != operationNamespace ||
                        !string.Equals(argument.Name.LocalName, "folderName", StringComparison.OrdinalIgnoreCase)))
                {
                    return CreateSoapFault(
                        "GetServiceDescriptionsEx accepts only one folderName argument.",
                        StatusCodes.Status400BadRequest,
                        soap);
                }

                var folderName = arguments.SingleOrDefault()?.Value.Trim();
                payload = new XElement(
                    operationNamespace + "GetServiceDescriptionsExResult",
                    await BuildSoapImageServerDescriptionsAsync(
                        context,
                        operationNamespace,
                        graphProvider,
                        rasterStore,
                        logger,
                        folderName).ConfigureAwait(false));
                break;
            case "GetFolders":
                payload = new XElement(operationNamespace + "GetFoldersResult");
                break;
            case "GetMessageVersion":
                payload = new XElement(operationNamespace + "GetMessageVersionResult", "esriArcGISVersion108");
                break;
            case "GetMessageFormats":
                payload = new XElement(operationNamespace + "GetMessageFormatsResult", "esriServiceCatalogMessageFormatSoap");
                break;
            case "GetTokenServiceURL":
                payload = new XElement(
                    operationNamespace + "GetTokenServiceURLResult",
                    tokenOptions.Value.Enabled
                        ? $"{BaseUrlResolver.GetBaseUrl(context).TrimEnd('/')}/sharing/rest/generateToken"
                        : string.Empty);
                break;
            case "RequiresTokens":
                payload = new XElement(
                    operationNamespace + "RequiresTokensResult",
                    tokenOptions.Value.Enabled);
                break;
                default:
                    return CreateSoapFault(
                        $"Unsupported catalog operation '{operation.Name.LocalName}'.",
                        StatusCodes.Status400BadRequest,
                        soap);
            }

            var response = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(
                    soap + "Envelope",
                    new XAttribute(XNamespace.Xmlns + "soap", soap.NamespaceName),
                    new XElement(
                        soap + "Body",
                        new XElement(
                            operationNamespace + $"{operation.Name.LocalName}Response",
                            payload))));

            return Results.Content(
                response.ToString(SaveOptions.DisableFormatting),
                contentType: SoapContentTypeFor(soap),
                contentEncoding: Encoding.UTF8);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            GeoservicesCatalogEndpointLogging.LogSoapCatalogOperationFailed(
                logger,
                operation.Name.LocalName,
                exception);
            return CreateSoapFault(
                "The SOAP services catalog is temporarily unavailable.",
                StatusCodes.Status503ServiceUnavailable,
                soap);
        }
    }

    private static async Task<IReadOnlyList<XElement>> BuildSoapImageServerDescriptionsAsync(
        HttpContext context,
        XNamespace operationNamespace,
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ILogger logger,
        string? folderName)
    {
        // Honua currently exposes a root-only catalog. IServiceCatalog2 defines
        // ServiceDescriptionsEx(folderName), so a named folder has no entries.
        if (!string.IsNullOrWhiteSpace(folderName))
        {
            return [];
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var requestedServiceName = context.Request.RouteValues["serviceName"] as string;
        var services = new List<MetadataV2Service>();
        var probes = new List<(int ServiceIndex, int LayerIndex)>();

        foreach (var service in snapshot.Graph.Services.OrderBy(static service => service.Metadata.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!service.IsRoutable() ||
                !service.Protocols.Contains(ImageServerProtocolName, StringComparer.Ordinal) ||
                (requestedServiceName is not null &&
                 !string.Equals(service.Metadata.Name, requestedServiceName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var layerIndexes = new List<int>();
            foreach (var publication in snapshot.PublicationsForService(service.Metadata.Id).Where(snapshot.IsRoutable))
            {
                var resource = snapshot.ResolveResource(publication) as MetadataV2Resource;
                var storageLayerId = snapshot.ResolveStorageLayerId(publication);
                if (resource is null || storageLayerId is not { } layerIndex)
                {
                    continue;
                }

                var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
                    context,
                    resource,
                    AuthorizationOperation.Metadata,
                    service,
                    cancellationToken).ConfigureAwait(false);
                if (accessError is not null)
                {
                    continue;
                }

                layerIndexes.Add(layerIndex);
            }

            if (layerIndexes.Count == 0)
            {
                continue;
            }

            var serviceIndex = services.Count;
            services.Add(service);
            probes.AddRange(layerIndexes.Select(layerIndex => (serviceIndex, layerIndex)));
        }

        var advertised = new bool[services.Count];
        await Parallel.ForEachAsync(
            probes,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (probe, ct) =>
            {
                if (advertised[probe.ServiceIndex])
                {
                    return;
                }

                try
                {
                    if ((await rasterStore.ListRastersAsync(probe.LayerIndex, ct).ConfigureAwait(false)).Length > 0)
                    {
                        advertised[probe.ServiceIndex] = true;
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                {
                    GeoservicesCatalogEndpointLogging.LogRasterProbeFailed(
                        logger,
                        services[probe.ServiceIndex].Metadata.Name,
                        exception);
                }
            }).ConfigureAwait(false);

        var descriptions = new List<XElement>();
        for (var index = 0; index < services.Count; index++)
        {
            if (!advertised[index])
            {
                continue;
            }

            var service = services[index];
            descriptions.Add(new XElement(
                operationNamespace + "ServiceDescription",
                new XElement(operationNamespace + "Name", service.Metadata.Name),
                new XElement(operationNamespace + "Type", ImageServerProtocolName),
                new XElement(
                    operationNamespace + "Url",
                    $"{baseUrl}/services/{Uri.EscapeDataString(service.Metadata.Name)}/{ImageServerProtocolName}"),
                new XElement(operationNamespace + "ParentType", string.Empty),
                new XElement(operationNamespace + "Capabilities", "Image,Metadata"),
                new XElement(operationNamespace + "Description", string.Empty)));
        }

        return descriptions;
    }

    private static IResult CreateSoapFault(string message, int statusCode, XNamespace soap)
    {
        var isServerFault = statusCode >= StatusCodes.Status500InternalServerError;
        var fault = soap == Soap12EnvelopeNamespace
            ? new XElement(
                soap + "Fault",
                new XElement(
                    soap + "Code",
                    new XElement(soap + "Value", isServerFault ? "soap:Receiver" : "soap:Sender")),
                new XElement(
                    soap + "Reason",
                    new XElement(
                        soap + "Text",
                        new XAttribute(XNamespace.Xml + "lang", "en"),
                        message)))
            : new XElement(
                soap + "Fault",
                new XElement("faultcode", isServerFault ? "soap:Server" : "soap:Client"),
                new XElement("faultstring", message));
        var response = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", soap.NamespaceName),
                new XElement(
                    soap + "Body",
                    fault)));

        return Results.Content(
            response.ToString(SaveOptions.DisableFormatting),
            contentType: SoapContentTypeFor(soap),
            contentEncoding: Encoding.UTF8,
            statusCode: statusCode);
    }

    private static XNamespace RequestedSoapNamespace(HttpRequest request)
        => request.ContentType?.StartsWith("application/soap+xml", StringComparison.OrdinalIgnoreCase) == true
            ? Soap12EnvelopeNamespace
            : Soap11EnvelopeNamespace;

    private static string SoapContentTypeFor(XNamespace soap)
        => soap == Soap12EnvelopeNamespace ? Soap12ContentType : Soap11ContentType;

    private static async Task<IResult> HandleGetServicesDirectory(
        HttpContext context,
        string? f,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] IRasterStore rasterStore,
        [FromServices] ILicenseStatusProvider licenseStatusProvider,
        [FromServices] ILogger<GeoservicesCatalogLog> logger)
    {
        if (!IsSupportedFormat(f))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson.");
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var entries = new List<ServiceDirectoryEntry>();
        var deniedPublications = new List<MetadataV2Resource>();
        // ImageServer entries require a raster-store probe to determine availability.
        // Collect them separately so all probes can run concurrently instead of serially.
        var imageServerServices = new List<MetadataV2Service>();

        foreach (var service in snapshot.Graph.Services.OrderBy(static s => s.Metadata.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!service.IsRoutable())
            {
                continue;
            }

            // Derive the catalog "type" from every Esri-family protocol the service
            // exposes, not just the primary one. A service reachable under multiple
            // Esri types (e.g. both FeatureServer and MapServer, or a vector layer that
            // is also rendered as a MapServer) is listed once per type, mirroring how a
            // real ArcGIS Services Directory enumerates it (#1853). Raster/coverage
            // services that expose ImageServer/MapServer therefore type as
            // ImageServer/MapServer instead of being flattened to FeatureServer.
            var directoryTypes = MapEsriDirectoryTypes(service);
            if (directoryTypes.Count == 0)
            {
                continue;
            }

            // Project publications -> resources, filtering by access.
            var visibleResources = new List<MetadataV2Resource>();
            foreach (var resource in snapshot.PublicationsForService(service.Metadata.Id)
                .Where(snapshot.IsRoutable)
                .Select(snapshot.ResolveResource)
                .OfType<MetadataV2Resource>())
            {
                if (AccessPolicyHelpers.IsResourceAccessible(context, resource, service))
                {
                    visibleResources.Add(resource);
                }
                else
                {
                    deniedPublications.Add(resource);
                }
            }

            // GPServer is service-scoped: its built-in process catalog is usable even when
            // a service has no layer publications (including the default layerless
            // `geoprocessing` service). Other directory types remain publication-backed.
            // Evaluate the service policy before exposing this layerless entry so catalog
            // discovery matches the GPServer endpoints' own service-level authorization.
            var advertiseLayerlessGp = false;
            if (visibleResources.Count == 0
                && directoryTypes.Contains(GPServerProtocolName, StringComparer.Ordinal))
            {
                advertiseLayerlessGp = await AccessPolicyHelpers.RequireServiceAccessAsync(
                    context,
                    service,
                    AuthorizationOperation.Query,
                    cancellationToken).ConfigureAwait(false) is null;
            }
            if (visibleResources.Count == 0 && !advertiseLayerlessGp)
            {
                continue;
            }

            if (visibleResources.Count == 0)
            {
                directoryTypes = [GPServerProtocolName];
            }

            var escapedName = Uri.EscapeDataString(service.Metadata.Name);
            foreach (var directoryType in directoryTypes)
            {
                // ImageServer availability additionally depends on a raster-store probe,
                // so those entries are collected and probed concurrently below rather
                // than emitted unconditionally here.
                if (string.Equals(directoryType, ImageServerProtocolName, StringComparison.Ordinal))
                {
                    imageServerServices.Add(service);
                    continue;
                }

                entries.Add(new ServiceDirectoryEntry
                {
                    Name = service.Metadata.Name,
                    Type = directoryType,
                    Url = $"{baseUrl}/rest/services/{escapedName}/{directoryType}"
                });
            }
        }

        // Probe all ImageServer services concurrently (bounded to 4 in-flight at once)
        // rather than serially, to avoid an N+1 raster-store round-trip per service.
        if (imageServerServices.Count > 0)
        {
            var imageServerEntries = new ServiceDirectoryEntry?[imageServerServices.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, imageServerServices.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (i, ct) =>
                {
                    var svc = imageServerServices[i];
                    try
                    {
                        var imageServerLayerId = await GetImageServerLayerIdAsync(
                            snapshot,
                            svc,
                            rasterStore,
                            ct).ConfigureAwait(false);
                        if (imageServerLayerId.HasValue)
                        {
                            // The URL uses the service NAME as the route segment (matching every
                            // other service type and the canonical ArcGIS addressing), not the
                            // numeric layer id. The probe above only decides whether to advertise.
                            var escapedImageServerName = Uri.EscapeDataString(svc.Metadata.Name);
                            imageServerEntries[i] = new ServiceDirectoryEntry
                            {
                                Name = svc.Metadata.Name,
                                Type = "ImageServer",
                                Url = $"{baseUrl}/rest/services/{escapedImageServerName}/ImageServer"
                            };
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                    {
                        // Intentional catch-all: one service's raster-store probe failing must not
                        // abort the whole concurrent Parallel.ForEachAsync batch or the directory
                        // response; the failure is logged and the entry is simply omitted.
                        GeoservicesCatalogEndpointLogging.LogRasterProbeFailed(logger, svc.Metadata.Name, ex);
                    }
                }).ConfigureAwait(false);

            // Merge ImageServer entries back; re-sort by name to restore alphabetical order.
            entries.AddRange(imageServerEntries.OfType<ServiceDirectoryEntry>());

            entries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        }

        // Esri I3S SceneServer entries (#1807). Hosted scenes live in the scene
        // registry, not the MetadataV2 graph, so they are appended here rather
        // than discovered in the service loop above. They are Enterprise-gated:
        // open-core (< Enterprise) omits them from the catalog entirely, matching
        // the SceneServer serving endpoints' 403 behaviour. No 402 is introduced.
        var sceneEntriesAdded = await AppendSceneServerEntriesAsync(
            context,
            entries,
            licenseStatusProvider,
            baseUrl,
            cancellationToken).ConfigureAwait(false);
        if (sceneEntriesAdded)
        {
            entries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        }

        // If nothing was emitted but there were publications the caller could not see,
        // surface the standard 401/403 access decision instead of an empty directory.
        if (entries.Count == 0 && deniedPublications.Count > 0)
        {
            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(context, deniedPublications);
            if (accessError != null)
            {
                return accessError;
            }
        }

        var response = new ServicesDirectoryResponse
        {
            Services = [.. entries]
        };

        GeoservicesCatalogEndpointLogging.LogServicesDirectoryReturned(logger, response.Services.Length);

        return Results.Json(response, GeoservicesCatalogJsonContext.Default.ServicesDirectoryResponse, contentType: JsonContentType);
    }

    private static IResult HandleGetRestInfo(HttpContext context, string? f)
    {
        if (!IsSupportedFormat(f))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson.");
        }

        var response = new RestInfoResponse();
        return Results.Json(response, GeoservicesCatalogJsonContext.Default.RestInfoResponse, contentType: JsonContentType);
    }

    /// <summary>
    /// Maps every Esri-family protocol a service exposes to the directory-entry "type"
    /// strings the GeoServices REST catalog advertises (FeatureServer, MapServer,
    /// ImageServer, GPServer, VectorTileServer, SceneServer), preserving the service's declared
    /// protocol order and de-duplicating. A service reachable under several Esri types
    /// is listed once per type, matching ArcGIS Services Directory semantics (#1853).
    /// Non-Esri protocols (OGC API Features, STAC, OData, etc.) are skipped because they
    /// are surfaced through their own catalogs.
    /// </summary>
    private static List<string> MapEsriDirectoryTypes(MetadataV2Service service)
    {
        return service.Protocols
            .Select(protocol => TryMapServiceType(protocol, out var directoryType) ? directoryType : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Maps an Esri-family protocol to the directory-entry "type" string the
    /// GeoServices REST catalog exposes (FeatureServer, MapServer, ImageServer,
    /// GPServer, VectorTileServer). Returns false for non-Esri protocols (OGC API Features, STAC, etc.)
    /// which are surfaced through other catalogs.
    /// </summary>
    private static bool TryMapServiceType(string? primaryProtocol, out string directoryType)
    {
        switch (primaryProtocol)
        {
            case FeatureServerProtocolName:
                directoryType = "FeatureServer";
                return true;
            case MapServerProtocolName:
                directoryType = "MapServer";
                return true;
            case ImageServerProtocolName:
                directoryType = "ImageServer";
                return true;
            case GPServerProtocolName:
                directoryType = "GPServer";
                return true;
            case VectorTileServerProtocolName:
                directoryType = "VectorTileServer";
                return true;
            case SceneServerProtocolName:
                // Esri I3S SceneServer (#1807). Hosted scenes are not part of the
                // MetadataV2 graph (they live in the scene registry), so this
                // case is only reachable if a future graph-backed scene producer
                // sets SceneServer as its primary protocol; the scene-registry
                // entries are appended separately in AppendSceneServerEntriesAsync.
                directoryType = "SceneServer";
                return true;
            default:
                directoryType = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// Appends an Esri I3S <c>SceneServer</c> directory entry for every active
    /// registered scene, addressed at the canonical
    /// <c>/rest/services/{id}/SceneServer</c> GeoServices path (#1807). Returns
    /// <see langword="true"/> when at least one entry was appended so the caller
    /// can re-sort the directory.
    /// </summary>
    /// <remarks>
    /// SceneServer is Enterprise-gated: for editions below
    /// <see cref="HonuaEdition.Enterprise"/> this is a no-op, so open-core
    /// catalogs omit SceneServer entirely (no 402, matching the serving
    /// endpoints' 403). The scene registry seam is optional — when no
    /// <see cref="ISceneRegistrationService"/> is registered (e.g. config-only
    /// hosting) no scene entries are emitted.
    /// </remarks>
    private static async Task<bool> AppendSceneServerEntriesAsync(
        HttpContext context,
        List<ServiceDirectoryEntry> entries,
        ILicenseStatusProvider licenseStatusProvider,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        if (licenseStatusProvider.GetCurrentStatus().Edition < HonuaEdition.Enterprise)
        {
            return false;
        }

        var registration = context.RequestServices.GetService<ISceneRegistrationService>();
        if (registration is null)
        {
            return false;
        }

        var scenes = await registration.ListAsync(includeInactive: false, cancellationToken).ConfigureAwait(false);
        if (scenes.Count == 0)
        {
            return false;
        }

        foreach (var scene in scenes)
        {
            var escapedSceneId = Uri.EscapeDataString(scene.Id);
            entries.Add(new ServiceDirectoryEntry
            {
                Name = scene.Name,
                Type = SceneServerProtocolName,
                Url = $"{baseUrl}/rest/services/{escapedSceneId}/SceneServer"
            });
        }

        return true;
    }

    /// <summary>
    /// Finds the first publication on the given image service whose layer index
    /// has at least one raster registered in the raster store. The catalog uses
    /// the layer index (not the service name) as the route segment for
    /// ImageServer entries.
    /// </summary>
    private static async Task<int?> GetImageServerLayerIdAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        IRasterStore rasterStore,
        CancellationToken cancellationToken)
    {
        foreach (var publication in snapshot.PublicationsForService(service.Metadata.Id))
        {
            if (!snapshot.IsRoutable(publication)
                || publication.LayerIndex is not { } layerIndex
                || snapshot.ResolveStorageLayerId(publication) is not { } storageLayerId)
            {
                continue;
            }

            var rasters = await rasterStore.ListRastersAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
            if (rasters.Length > 0)
            {
                return layerIndex;
            }
        }

        return null;
    }

    private static bool IsSupportedFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ||
           string.Equals(format, JsonFormat, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, PrettyJsonFormat, StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedSoapContentType(string? contentType)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "text/xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/soap+xml", StringComparison.OrdinalIgnoreCase);
    }
}

internal static partial class GeoservicesCatalogEndpointLogging
{
    [LoggerMessage(EventId = 9401, Level = LogLevel.Information,
        Message = "GeoServices services directory returned {ServiceCount} entries.")]
    public static partial void LogServicesDirectoryReturned(ILogger logger, int serviceCount);

    [LoggerMessage(EventId = 9402, Level = LogLevel.Warning,
        Message = "Failed to probe raster availability for service {ServiceName}.")]
    public static partial void LogRasterProbeFailed(ILogger logger, string serviceName, Exception exception);

    [LoggerMessage(EventId = 9403, Level = LogLevel.Error,
        Message = "ArcGIS SOAP services catalog operation {Operation} failed.")]
    public static partial void LogSoapCatalogOperationFailed(ILogger logger, string operation, Exception exception);
}

/// <summary>
/// Logger category for GeoServices catalog endpoints.
/// </summary>
internal sealed class GeoservicesCatalogLog
{
}
