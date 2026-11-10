// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Linq;

namespace Honua.Protocols.GeoServices.Soap;

internal static class GeoServicesSoapProtocol
{
    private const string Soap11ContentType = "text/xml; charset=utf-8";
    private const string Soap12ContentType = "application/soap+xml; charset=utf-8";
    private const string Soap11EnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Soap12EnvelopeNamespace = "http://www.w3.org/2003/05/soap-envelope";
    private const string XmlSchemaNamespace = "http://www.w3.org/2001/XMLSchema";
    private const string XmlSchemaInstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private const int MaxRequestCharacters = 1_048_576;

    internal static async Task<(XElement? Operation, XNamespace? SoapNamespace, IResult? ErrorResult)> TryReadSoapRequestAsync(
        HttpContext context)
    {
        if (!IsSupportedSoapContentType(context.Request.ContentType))
        {
            var requestedSoap = RequestedSoapNamespace(context.Request);
            return (null, requestedSoap, CreateSoapFault(
                "Content-Type must be text/xml or application/soap+xml.",
                StatusCodes.Status415UnsupportedMediaType,
                requestedSoap));
        }

        try
        {
            using var reader = SoapRequestXml.CreateReader(context.Request.Body, MaxRequestCharacters);
            var request = await XDocument.LoadAsync(reader, LoadOptions.None, context.RequestAborted).ConfigureAwait(false);
            var envelopeNamespace = request.Root?.Name.Namespace;
            if (request.Root?.Name.LocalName != "Envelope" ||
                (envelopeNamespace != Soap11EnvelopeNamespace && envelopeNamespace != Soap12EnvelopeNamespace))
            {
                var requestedSoap = RequestedSoapNamespace(context.Request);
                return (null, requestedSoap, CreateSoapFault(
                    "Unsupported SOAP envelope namespace.",
                    StatusCodes.Status400BadRequest,
                    requestedSoap));
            }

            var requestedSoapNamespace = RequestedSoapNamespace(context.Request);
            if (envelopeNamespace != requestedSoapNamespace)
            {
                return (null, requestedSoapNamespace, CreateSoapFault(
                    "Content-Type does not match the SOAP envelope version.",
                    StatusCodes.Status415UnsupportedMediaType,
                    requestedSoapNamespace));
            }

            XNamespace soap = envelopeNamespace;
            var bodies = request.Root.Elements(soap + "Body").Take(2).ToArray();
            if (bodies.Length != 1)
            {
                return (null, soap, CreateSoapFault(
                    "SOAP envelope must contain exactly one Body element.",
                    StatusCodes.Status400BadRequest,
                    soap));
            }

            var operations = bodies[0].Elements().Take(2).ToArray();
            if (operations is not { Length: 1 })
            {
                return (null, soap, CreateSoapFault(
                    "SOAP body must contain exactly one operation.",
                    StatusCodes.Status400BadRequest,
                    soap));
            }

            if (!GeoServicesSoapNamespaces.IsSupported(operations[0].Name.Namespace))
            {
                return (null, soap, CreateSoapFault(
                    "Unsupported ArcGIS SOAP operation namespace.",
                    StatusCodes.Status400BadRequest,
                    soap));
            }

            return (BindArgumentsByLocalName(operations[0]), soap, null);
        }
        catch (Exception exception) when (exception is XmlException or XmlSchemaValidationException or InvalidOperationException)
        {
            var requestedSoap = RequestedSoapNamespace(context.Request);
            return (null, requestedSoap, CreateSoapFault(
                SoapRequestXml.GetSafeErrorMessage(exception),
                StatusCodes.Status400BadRequest,
                requestedSoap));
        }
    }

    // Esri WSDLs set elementFormDefault to unqualified, so qualified, unqualified, and default-namespace arguments are the same request and bind by local name while the operation keeps its negotiated namespace.
    internal static XElement BindArgumentsByLocalName(XElement operation)
    {
        // codeql[cs/linq/missed-where]: renames namespaced arguments in place on the same element tree
        foreach (var argument in operation.Descendants())
        {
            if (argument.Name.Namespace != XNamespace.None)
            {
                argument.Name = XNamespace.None + argument.Name.LocalName;
            }
        }

        return operation;
    }

    internal static IResult CreateSoapResponse(
        XNamespace soap,
        XNamespace operationNamespace,
        string responseName,
        XElement? result)
    {
        XNamespace xsi = XmlSchemaInstanceNamespace;
        XNamespace xsd = XmlSchemaNamespace;
        var response = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", soap.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsd", xsd.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "tns", operationNamespace.NamespaceName),
                new XElement(
                    soap + "Body",
                    new XElement(operationNamespace + responseName, result))));

        return Results.Content(
            GeoServicesSoapNamespaces.SerializeResponse(response),
            contentType: SoapContentTypeFor(soap),
            contentEncoding: Encoding.UTF8);
    }

    internal static IResult CreateSoapFaultFromResult(IResult result, string message, XNamespace soap)
    {
        var statusCode = (result as IStatusCodeHttpResult)?.StatusCode
            ?? StatusCodes.Status500InternalServerError;
        return CreateSoapFault(message, statusCode, soap);
    }

    internal static IResult CreateSoapFault(string message, int statusCode, XNamespace soap)
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
            GeoServicesSoapNamespaces.SerializeResponse(response),
            contentType: SoapContentTypeFor(soap),
            contentEncoding: Encoding.UTF8,
            statusCode: statusCode);
    }

    internal static XNamespace RequestedSoapNamespace(HttpRequest request)
        => string.Equals(
            request.ContentType?.Split(';', 2)[0].Trim(),
            "application/soap+xml",
            StringComparison.OrdinalIgnoreCase)
            ? Soap12EnvelopeNamespace
            : Soap11EnvelopeNamespace;

    internal static bool IsSupportedSoapContentType(string? contentType)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "text/xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/soap+xml", StringComparison.OrdinalIgnoreCase);
    }

    internal static string SoapContentTypeFor(XNamespace soap)
        => soap == Soap12EnvelopeNamespace ? Soap12ContentType : Soap11ContentType;

}
