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
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.ServiceDefaults;
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
            .WithSummary("Discover supported ArcGIS services through SOAP")
            .WithDescription("Implements the protocol-wide ArcGIS Server SOAP catalog using the same principal-filtered projection as the REST services directory.")
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
            .Produces(StatusCodes.Status401Unauthorized, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status403Forbidden, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status415UnsupportedMediaType, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status503ServiceUnavailable, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .AllowAnonymous();

        endpoints.MapGet("/services", HandleGetSoapCatalogWsdl)
            .WithDisplayName("ArcGIS SOAP Services Catalog WSDL")
            .WithName("ArcGisSoapServicesCatalogWsdl")
            .WithSummary("Get the ArcGIS SOAP services catalog WSDL")
            .WithDescription("Returns the SOAP 1.1 and SOAP 1.2 service-catalog contract when the wsdl query flag is present.")
            .WithTags("GeoServices Catalog")
            .Produces(StatusCodes.Status200OK, contentType: "text/xml")
            .Produces(StatusCodes.Status404NotFound, contentType: "text/xml")
            .AllowAnonymous();

        return endpoints;
    }

    private static IResult HandleGetSoapCatalogWsdl(HttpContext context)
    {
        if (!context.Request.Query.ContainsKey("wsdl"))
        {
            return Results.NotFound();
        }

        XNamespace wsdl = "http://schemas.xmlsoap.org/wsdl/";
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        XNamespace soap11 = "http://schemas.xmlsoap.org/wsdl/soap/";
        XNamespace soap12 = "http://schemas.xmlsoap.org/wsdl/soap12/";
        XNamespace esri = ArcGisSoapNamespaces.Current;
        var operations = new[]
        {
            "GetServiceDescriptions",
            "GetServiceDescriptionsEx",
            "GetFolders",
            "GetMessageVersion",
            "GetMessageFormats",
            "GetTokenServiceURL",
            "RequiresTokens"
        };

        var schema = new XElement(
            xs + "schema",
            new XAttribute("targetNamespace", esri.NamespaceName),
            new XAttribute("elementFormDefault", "qualified"),
            new XElement(
                xs + "complexType",
                new XAttribute("name", "ServiceDescription"),
                new XElement(
                    xs + "sequence",
                    SoapCatalogSchemaElement(xs, "Name", "xs:string"),
                    SoapCatalogSchemaElement(xs, "Type", "xs:string"),
                    SoapCatalogSchemaElement(xs, "Url", "xs:anyURI"),
                    new XElement(
                        xs + "element",
                        new XAttribute("name", "RestUrl"),
                        new XAttribute("type", "xs:anyURI"),
                        new XAttribute("minOccurs", "0")),
                    SoapCatalogSchemaElement(xs, "ParentType", "xs:string"),
                    SoapCatalogSchemaElement(xs, "Capabilities", "xs:string"),
                    SoapCatalogSchemaElement(xs, "Description", "xs:string"))),
            new XElement(
                xs + "complexType",
                new XAttribute("name", "ArrayOfServiceDescription"),
                new XElement(
                    xs + "sequence",
                    new XElement(
                        xs + "element",
                        new XAttribute("name", "ServiceDescription"),
                        new XAttribute("type", "e:ServiceDescription"),
                        new XAttribute("minOccurs", "0"),
                        new XAttribute("maxOccurs", "unbounded")))));

        foreach (var operation in operations)
        {
            var requestSequence = operation == "GetServiceDescriptionsEx"
                ? new XElement(
                    xs + "sequence",
                    new XElement(
                        xs + "element",
                        new XAttribute("name", "folderName"),
                        new XAttribute("type", "xs:string"),
                        new XAttribute("minOccurs", "0")))
                : new XElement(xs + "sequence");
            var responseType = operation is "GetServiceDescriptions" or "GetServiceDescriptionsEx"
                ? "e:ArrayOfServiceDescription"
                : operation == "RequiresTokens" ? "xs:boolean" : "xs:string";
            schema.Add(
                new XElement(
                    xs + "element",
                    new XAttribute("name", operation),
                    new XElement(xs + "complexType", requestSequence)),
                new XElement(
                    xs + "element",
                    new XAttribute("name", operation + "Response"),
                    new XElement(
                        xs + "complexType",
                        new XElement(
                            xs + "sequence",
                            new XElement(
                                xs + "element",
                                new XAttribute("name", operation + "Result"),
                                new XAttribute("type", responseType))))));
        }

        var definitions = new XElement(
            wsdl + "definitions",
            new XAttribute(XNamespace.Xmlns + "wsdl", wsdl.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xs", xs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "soap", soap11.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "soap12", soap12.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "e", esri.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "tns", esri.NamespaceName),
            new XAttribute("targetNamespace", esri.NamespaceName),
            new XElement(wsdl + "types", schema));

        foreach (var operation in operations)
        {
            definitions.Add(
                SoapCatalogWsdlMessage(wsdl, operation, operation),
                SoapCatalogWsdlMessage(wsdl, operation + "Response", operation + "Response"));
        }

        definitions.Add(SoapCatalogPortType(wsdl, operations));
        definitions.Add(SoapCatalogBinding(wsdl, soap11, operations, "ServiceCatalogSoap", "binding"));
        definitions.Add(SoapCatalogBinding(wsdl, soap12, operations, "ServiceCatalogSoap12", "binding"));

        var address = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/') + "/services";
        definitions.Add(
            new XElement(
                wsdl + "service",
                new XAttribute("name", "ServiceCatalog"),
                SoapCatalogPort(wsdl, soap11, "ServiceCatalogSoap", "tns:ServiceCatalogSoap", address),
                SoapCatalogPort(wsdl, soap12, "ServiceCatalogSoap12", "tns:ServiceCatalogSoap12", address)));

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), definitions);
        return Results.Content(
            document.ToString(SaveOptions.DisableFormatting),
            contentType: Soap11ContentType,
            contentEncoding: Encoding.UTF8);
    }

    private static XElement SoapCatalogSchemaElement(XNamespace xs, string name, string type)
        => new(xs + "element", new XAttribute("name", name), new XAttribute("type", type));

    private static XElement SoapCatalogWsdlMessage(XNamespace wsdl, string messageName, string elementName)
        => new(
            wsdl + "message",
            new XAttribute("name", messageName),
            new XElement(
                wsdl + "part",
                new XAttribute("name", "parameters"),
                new XAttribute("element", "e:" + elementName)));

    private static XElement SoapCatalogPortType(XNamespace wsdl, IEnumerable<string> operations)
        => new(
            wsdl + "portType",
            new XAttribute("name", "ServiceCatalogPortType"),
            operations.Select(operation => new XElement(
                wsdl + "operation",
                new XAttribute("name", operation),
                new XElement(wsdl + "input", new XAttribute("message", "tns:" + operation)),
                new XElement(wsdl + "output", new XAttribute("message", "tns:" + operation + "Response")))));

    private static XElement SoapCatalogBinding(
        XNamespace wsdl,
        XNamespace soap,
        IEnumerable<string> operations,
        string name,
        string bindingElementName)
        => new(
            wsdl + "binding",
            new XAttribute("name", name),
            new XAttribute("type", "tns:ServiceCatalogPortType"),
            new XElement(soap + bindingElementName, new XAttribute("style", "document"), new XAttribute("transport", "http://schemas.xmlsoap.org/soap/http")),
            operations.Select(operation => new XElement(
                wsdl + "operation",
                new XAttribute("name", operation),
                new XElement(soap + "operation", new XAttribute("soapAction", $"{ArcGisSoapNamespaces.Current}/{operation}")),
                new XElement(wsdl + "input", new XElement(soap + "body", new XAttribute("use", "literal"))),
                new XElement(wsdl + "output", new XElement(soap + "body", new XAttribute("use", "literal"))))));

    private static XElement SoapCatalogPort(
        XNamespace wsdl,
        XNamespace soap,
        string name,
        string binding,
        string address)
        => new(
            wsdl + "port",
            new XAttribute("name", name),
            new XAttribute("binding", binding),
            new XElement(soap + "address", new XAttribute("location", address)));

    private static async Task<IResult> HandlePostSoapCatalog(
        HttpContext context,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] IRasterStore rasterStore,
        [FromServices] ILicenseStatusProvider licenseStatusProvider,
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

        var contentTypeSoap = RequestedSoapNamespace(context.Request);
        if (envelopeNamespace != contentTypeSoap)
        {
            return CreateSoapFault(
                "Content-Type does not match the SOAP envelope version.",
                StatusCodes.Status415UnsupportedMediaType,
                contentTypeSoap);
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
        if (!ArcGisSoapNamespaces.IsSupported(operationNamespace))
        {
            return CreateSoapFault(
                "Unsupported ArcGIS SOAP operation namespace.",
                StatusCodes.Status400BadRequest,
                soap);
        }

        var operationName = operation.Name.LocalName;
        if (!IsSupportedSoapCatalogOperation(operationName))
        {
            return CreateSoapFault(
                $"Unsupported catalog operation '{operationName}'.",
                StatusCodes.Status400BadRequest,
                soap);
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            $"soap-catalog-{operationName}",
            HonuaTelemetry.Protocols.ImageServer,
            "*",
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.Operation, operationName);

        try
        {
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            XElement payload;
            switch (operation.Name.LocalName)
            {
                case "GetServiceDescriptions":
                    payload = new XElement(
                        "ServiceDescriptions",
                        new XAttribute(xsi + "type", "tns:ArrayOfServiceDescription"),
                        await BuildSoapServiceDescriptionsAsync(
                            context,
                            graphProvider,
                            rasterStore,
                            licenseStatusProvider,
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
                        return CompleteSoapCatalogOperation(scope, CreateSoapFault(
                            "GetServiceDescriptionsEx accepts only one folderName argument.",
                            StatusCodes.Status400BadRequest,
                            soap));
                    }

                    var folderName = arguments.SingleOrDefault()?.Value.Trim();
                    payload = new XElement(
                        "ServiceDescriptions",
                        new XAttribute(xsi + "type", "tns:ArrayOfServiceDescription"),
                        await BuildSoapServiceDescriptionsAsync(
                            context,
                            graphProvider,
                            rasterStore,
                            licenseStatusProvider,
                            logger,
                            folderName).ConfigureAwait(false));
                    break;
                case "GetFolders":
                    payload = new XElement(
                        "FolderNames",
                        new XAttribute(xsi + "type", "tns:ArrayOfString"));
                    break;
                case "GetMessageVersion":
                    payload = new XElement("MessageVersion", "esriArcGISVersion108");
                    break;
                case "GetMessageFormats":
                    payload = new XElement("MessageFormats", "esriServiceCatalogMessageFormatSoap");
                    break;
                case "GetTokenServiceURL":
                    payload = new XElement(
                        "TokenServiceURL",
                        tokenOptions.Value.Enabled
                            ? $"{BaseUrlResolver.GetBaseUrl(context).TrimEnd('/')}/sharing/rest/generateToken"
                            : string.Empty);
                    break;
                case "RequiresTokens":
                    payload = new XElement(
                        "Result",
                        tokenOptions.Value.Enabled ? "1" : "0");
                    break;
                default:
                    return CompleteSoapCatalogOperation(scope, CreateSoapFault(
                        $"Unsupported catalog operation '{operation.Name.LocalName}'.",
                        StatusCodes.Status400BadRequest,
                        soap));
            }

            var response = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(
                    soap + "Envelope",
                    new XAttribute(XNamespace.Xmlns + "soap", soap.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
                    new XAttribute(XNamespace.Xmlns + "tns", operationNamespace.NamespaceName),
                    new XElement(
                        soap + "Body",
                        new XElement(
                            operationNamespace + $"{operation.Name.LocalName}Response",
                            payload))));

            return CompleteSoapCatalogOperation(scope, Results.Content(
                ArcGisSoapNamespaces.SerializeResponse(response),
                contentType: SoapContentTypeFor(soap),
                contentEncoding: Encoding.UTF8));
        }
        catch (SoapCatalogAccessException exception)
        {
            return CompleteSoapCatalogOperation(scope, CreateSoapFault(
                exception.StatusCode == StatusCodes.Status401Unauthorized
                    ? "Authentication is required to discover these services."
                    : "The calling principal is not authorized to discover these services.",
                exception.StatusCode,
                soap));
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
            var result = CompleteSoapCatalogOperation(scope, CreateSoapFault(
                "The SOAP services catalog is temporarily unavailable.",
                StatusCodes.Status503ServiceUnavailable,
                soap));
            scope.RecordException(exception);
            return result;
        }
    }

    private static bool IsSupportedSoapCatalogOperation(string operationName)
        => operationName is "GetServiceDescriptions"
            or "GetServiceDescriptionsEx"
            or "GetFolders"
            or "GetMessageVersion"
            or "GetMessageFormats"
            or "GetTokenServiceURL"
            or "RequiresTokens";

    private static IResult CompleteSoapCatalogOperation(HonuaTelemetryScope scope, IResult result)
    {
        var statusCode = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        var succeeded = statusCode < StatusCodes.Status400BadRequest;
        scope.WithTag("http.response.status_code", statusCode)
            .WithTag("honua.result", succeeded ? "success" : "error");
        if (succeeded)
        {
            scope.SetSuccess(1);
        }
        else
        {
            scope.SetError();
        }

        return result;
    }

    private static async Task<IReadOnlyList<XElement>> BuildSoapServiceDescriptionsAsync(
        HttpContext context,
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ILicenseStatusProvider licenseStatusProvider,
        ILogger logger,
        string? folderName)
    {
        var projection = await BuildServiceDirectoryProjectionAsync(
            context,
            graphProvider,
            rasterStore,
            licenseStatusProvider,
            logger).ConfigureAwait(false);
        if (projection.AllImageServerProbesFailed)
        {
            throw new InvalidOperationException("All eligible ImageServer raster catalog probes failed.");
        }
        if (projection.AccessError is not null)
        {
            throw new SoapCatalogAccessException(projection.AccessStatusCode!.Value);
        }

        // Honua currently exposes a root-only catalog. IServiceCatalog2 defines
        // ServiceDescriptionsEx(folderName), so a named folder has no entries.
        // Apply this only after authorization so a folder argument cannot bypass
        // the principal-filtered discovery decision.
        if (!string.IsNullOrWhiteSpace(folderName))
        {
            return [];
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        // Preserve the established ImageServer-first record when one service name
        // publishes several protocol types; protocol-wide discovery adds siblings
        // without changing the existing SOAP catalog's primary description.
        return projection.Entries
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => string.Equals(entry.Type, ImageServerProtocolName, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(static entry => entry.Type, StringComparer.Ordinal)
            .Select(entry =>
        {
            var escapedName = Uri.EscapeDataString(entry.Name);
            var soapUrl = string.Equals(entry.Type, ImageServerProtocolName, StringComparison.Ordinal)
                ? $"{baseUrl}/services/{escapedName}/{ImageServerProtocolName}"
                : entry.Url;
            return new XElement(
                "ServiceDescription",
                new XAttribute(xsi + "type", "tns:ServiceDescription"),
                new XElement("Name", entry.Name),
                new XElement("Type", entry.Type),
                new XElement("Url", soapUrl),
                new XElement("RestUrl", entry.Url),
                new XElement("ParentType", string.Empty),
                new XElement("Capabilities", entry.SoapCapabilities ?? CapabilitiesFor(entry.Type)),
                new XElement("Description", string.Empty));
        }).ToArray();
    }

    private static string CapabilitiesFor(string serviceType)
        => serviceType switch
        {
            FeatureServerProtocolName => "Query,Create,Update,Delete,Uploads,Editing",
            MapServerProtocolName => "Map,Query,Data",
            ImageServerProtocolName => "Image,Metadata",
            GPServerProtocolName => "SubmitJob,Execute",
            VectorTileServerProtocolName => "Tiles",
            SceneServerProtocolName => "Scene,Query",
            _ => string.Empty
        };

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
            ArcGisSoapNamespaces.SerializeResponse(response),
            contentType: SoapContentTypeFor(soap),
            contentEncoding: Encoding.UTF8,
            statusCode: statusCode);
    }

    private static XNamespace RequestedSoapNamespace(HttpRequest request)
        => string.Equals(
            request.ContentType?.Split(';', 2)[0].Trim(),
            "application/soap+xml",
            StringComparison.OrdinalIgnoreCase)
            ? Soap12EnvelopeNamespace
            : Soap11EnvelopeNamespace;

    private static string SoapContentTypeFor(XNamespace soap)
        => soap == Soap12EnvelopeNamespace ? Soap12ContentType : Soap11ContentType;

    private static async Task<ServiceDirectoryProjection> BuildServiceDirectoryProjectionAsync(
        HttpContext context,
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ILicenseStatusProvider licenseStatusProvider,
        ILogger logger)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<ServiceDirectoryEntry>();
        var deniedDecisions = new List<AccessDecision>();
        var imageServerServices = new List<ImageServerProbeCandidate>();
        var successfulImageServerProbes = 0;
        var failedImageServerProbes = 0;

        foreach (var service in snapshot.Graph.Services.OrderBy(static s => s.Metadata.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!service.IsRoutable())
            {
                continue;
            }

            var directoryTypes = MapEsriDirectoryTypes(service);
            if (directoryTypes.Count == 0)
            {
                continue;
            }

            var visibleResources = new List<MetadataV2Resource>();
            foreach (var resource in snapshot.PublicationsForService(service.Metadata.Id)
                .Where(snapshot.IsRoutable)
                .Select(snapshot.ResolveResource)
                .OfType<MetadataV2Resource>())
            {
                var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
                    context, resource, service, AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false);
                if (decision.IsAllowed)
                {
                    visibleResources.Add(resource);
                }
                else
                {
                    deniedDecisions.Add(decision);
                }
            }

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
                if (string.Equals(directoryType, ImageServerProtocolName, StringComparison.Ordinal))
                {
                    imageServerServices.Add(new ImageServerProbeCandidate(service, visibleResources));
                    continue;
                }

                entries.Add(new ServiceDirectoryEntry
                {
                    Name = service.Metadata.Name,
                    Type = directoryType,
                    Url = $"{baseUrl}/rest/services/{escapedName}/{directoryType}",
                    SoapCapabilities = string.Equals(directoryType, FeatureServerProtocolName, StringComparison.Ordinal)
                        ? FeatureServer.FeatureServerEndpoints.BuildServiceCapabilitiesV2(service)
                        : null
                });
            }
        }

        if (imageServerServices.Count > 0)
        {
            var imageServerEntries = new ServiceDirectoryEntry?[imageServerServices.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, imageServerServices.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (index, ct) =>
                {
                    var candidate = imageServerServices[index];
                    var service = candidate.Service;
                    try
                    {
                        var probe = await GetImageServerLayerIdAsync(
                            snapshot, service, candidate.VisibleResources, rasterStore, logger, ct).ConfigureAwait(false);
                        if (probe.AllLookupsFailed)
                        {
                            Interlocked.Increment(ref failedImageServerProbes);
                        }
                        else
                        {
                            Interlocked.Increment(ref successfulImageServerProbes);
                        }
                        if (probe.LayerId is not null)
                        {
                            var escapedName = Uri.EscapeDataString(service.Metadata.Name);
                            imageServerEntries[index] = new ServiceDirectoryEntry
                            {
                                Name = service.Metadata.Name,
                                Type = ImageServerProtocolName,
                                Url = $"{baseUrl}/rest/services/{escapedName}/{ImageServerProtocolName}"
                            };
                        }
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                    {
                        Interlocked.Increment(ref failedImageServerProbes);
                        GeoservicesCatalogEndpointLogging.LogRasterProbeFailed(logger, service.Metadata.Name, exception);
                    }
                }).ConfigureAwait(false);

            entries.AddRange(imageServerEntries.OfType<ServiceDirectoryEntry>());
            entries.Sort(ServiceDirectoryEntryComparer);
        }

        if (await AppendSceneServerEntriesAsync(
                context,
                entries,
                licenseStatusProvider,
                baseUrl,
                cancellationToken).ConfigureAwait(false))
        {
            entries.Sort(ServiceDirectoryEntryComparer);
        }

        IResult? accessError = null;
        int? accessStatusCode = null;
        if (entries.Count == 0 && deniedDecisions.Count > 0)
        {
            var requiresAuthentication = deniedDecisions.Any(static decision => decision.RequiresAuthentication);
            accessStatusCode = requiresAuthentication
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden;
            accessError = requiresAuthentication
                ? StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage)
                : StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage);
        }

        return new ServiceDirectoryProjection(
            entries,
            accessError,
            accessStatusCode,
            imageServerServices.Count > 0 && successfulImageServerProbes == 0 && failedImageServerProbes > 0);
    }

    private static int ServiceDirectoryEntryComparer(ServiceDirectoryEntry left, ServiceDirectoryEntry right)
    {
        var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        return nameComparison != 0
            ? nameComparison
            : StringComparer.Ordinal.Compare(left.Type, right.Type);
    }

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

        var projection = await BuildServiceDirectoryProjectionAsync(
            context,
            graphProvider,
            rasterStore,
            licenseStatusProvider,
            logger).ConfigureAwait(false);
        if (projection.AccessError is not null)
        {
            return projection.AccessError;
        }

        var response = new ServicesDirectoryResponse
        {
            Services = [.. projection.Entries]
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

        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        var soapUrl = $"{baseUrl}/services";

        // secureSoapUrl must follow the scheme of the SAME resolved public URL that produced
        // soapUrl, not the internal transport. Behind a TLS-terminating proxy -- the ordinary
        // deployment -- `Public:BaseUrl` is https while `Request.IsHttps` is false, which
        // published the contradictory pair `soapUrl: "https://..."` with `secureSoapUrl: null`
        // and left a client that selects the secure field unable to find the SOAP endpoint.
        var response = new RestInfoResponse
        {
            SoapUrl = soapUrl,
            SecureSoapUrl = IsHttpsBaseUrl(baseUrl, context) ? soapUrl : null
        };
        return Results.Json(response, GeoservicesCatalogJsonContext.Default.RestInfoResponse, contentType: JsonContentType);
    }

    /// <summary>
    /// Whether the resolved public base URL is served over TLS. A configured absolute base URL
    /// carries its own scheme and is authoritative; a relative or empty base means no public URL
    /// was configured, so the request transport is the only signal available.
    /// </summary>
    private static bool IsHttpsBaseUrl(string baseUrl, HttpContext context)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var absolute)
            ? string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            : context.Request.IsHttps;
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
    private static async Task<ImageServerProbeResult> GetImageServerLayerIdAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        IReadOnlyCollection<MetadataV2Resource> visibleResources,
        IRasterStore rasterStore,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var visibleResourceIds = visibleResources
            .Select(static resource => resource.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        var successfulLookups = 0;
        var failedLookups = 0;
        foreach (var publication in snapshot.PublicationsForService(service.Metadata.Id))
        {
            if (!snapshot.IsRoutable(publication)
                || !visibleResourceIds.Contains(publication.ResourceId)
                || publication.LayerIndex is not { } layerIndex
                || snapshot.ResolveStorageLayerId(publication) is not { } storageLayerId)
            {
                continue;
            }

            try
            {
                var raster = await rasterStore.GetPrimaryRasterInfoAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
                successfulLookups++;
                if (raster is not null)
                {
                    return new ImageServerProbeResult(layerIndex, AllLookupsFailed: false);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
            {
                failedLookups++;
                GeoservicesCatalogEndpointLogging.LogRasterProbeFailed(logger, service.Metadata.Name, exception);
            }
        }

        return new ImageServerProbeResult(
            LayerId: null,
            AllLookupsFailed: failedLookups > 0 && successfulLookups == 0);
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

    private sealed record ServiceDirectoryProjection(
        IReadOnlyList<ServiceDirectoryEntry> Entries,
        IResult? AccessError,
        int? AccessStatusCode,
        bool AllImageServerProbesFailed);

    private sealed record ImageServerProbeCandidate(
        MetadataV2Service Service,
        IReadOnlyCollection<MetadataV2Resource> VisibleResources);

    private sealed record ImageServerProbeResult(int? LayerId, bool AllLookupsFailed);

    private sealed class SoapCatalogAccessException(int statusCode) : Exception
    {
        public int StatusCode { get; } = statusCode;
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
