// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;

namespace Honua.Protocols.GeoServices.Soap;

/// <summary>
/// Reads one bounded ArcGIS SOAP 1.1 operation without accepting namespace-local-name
/// lookalikes. Protocol adapters remain responsible for validating operation-specific
/// children after this shared envelope validation succeeds.
/// </summary>
internal static class ArcGisSoap11RequestParser
{
    internal const string EnvelopeNamespaceName = "http://schemas.xmlsoap.org/soap/envelope/";
    internal const string CatalogNamespaceName = "http://www.esri.com/schemas/ArcGIS/10.8";
    internal const string ImageServerNamespaceName = "http://www.esri.com/schemas/ArcGIS/2.9.0";
    private const int MaxRequestCharacters = 1_048_576;

    internal static XNamespace EnvelopeNamespace => EnvelopeNamespaceName;

    internal static XNamespace CatalogNamespace => CatalogNamespaceName;

    internal static XNamespace ImageServerNamespace => ImageServerNamespaceName;

    internal static async Task<ArcGisSoap11OperationReadResult> ReadOperationAsync(
        Stream requestBody,
        string operationFamily,
        XNamespace operationNamespace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestBody);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationFamily);
        ArgumentNullException.ThrowIfNull(operationNamespace);

        XDocument request;
        try
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxRequestCharacters
            };
            using var reader = XmlReader.Create(requestBody, settings);
            request = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            return ArcGisSoap11OperationReadResult.Failure("Malformed SOAP request.");
        }

        var soap = EnvelopeNamespace;
        if (request.Root?.Name != soap + "Envelope")
        {
            return ArcGisSoap11OperationReadResult.Failure("A SOAP 1.1 Envelope is required.");
        }

        var bodies = request.Root.Elements(soap + "Body").Take(2).ToArray();
        if (bodies.Length != 1)
        {
            return ArcGisSoap11OperationReadResult.Failure(
                "The SOAP 1.1 Envelope must contain exactly one Body.");
        }

        var operations = bodies[0].Elements().Take(2).ToArray();
        if (operations.Length != 1)
        {
            return ArcGisSoap11OperationReadResult.Failure(
                $"SOAP body must contain exactly one {operationFamily} operation.");
        }

        if (operations[0].Name.Namespace != operationNamespace)
        {
            return ArcGisSoap11OperationReadResult.Failure(
                $"{operationFamily} SOAP operations must use the '{operationNamespace.NamespaceName}' target namespace.");
        }

        return ArcGisSoap11OperationReadResult.Success(operations[0]);
    }
}

internal readonly record struct ArcGisSoap11OperationReadResult(XElement? Operation, string? Error)
{
    internal static ArcGisSoap11OperationReadResult Success(XElement operation) => new(operation, null);

    internal static ArcGisSoap11OperationReadResult Failure(string error) => new(null, error);
}
