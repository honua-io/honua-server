// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wcs20;

internal static class Wcs20ErrorResults
{
    private static readonly XNamespace Ows = Wcs20Utilities.OwsNamespace;
    private static readonly XNamespace Xsi = Wcs20Utilities.XsiNamespace;

    internal static IResult CreateBadRequest(string exceptionCode, string detail, string? locator = null)
        => Create(StatusCodes.Status400BadRequest, exceptionCode, detail, locator);

    internal static IResult CreateNotFound(string exceptionCode, string detail, string? locator = null)
        => Create(StatusCodes.Status404NotFound, exceptionCode, detail, locator);

    internal static IResult CreateForbidden(string detail)
        => Create(StatusCodes.Status403Forbidden, Wcs20Utilities.ExceptionCodes.NoApplicableCode, detail, null);

    internal static IResult CreateUnauthorized(string detail)
        => Create(StatusCodes.Status401Unauthorized, Wcs20Utilities.ExceptionCodes.NoApplicableCode, detail, null);

    internal static IResult CreateNotImplemented(string exceptionCode, string detail, string? locator = null)
        => Create(StatusCodes.Status501NotImplemented, exceptionCode, detail, locator);

    internal static IResult CreateInternalServerError(string detail)
        => Create(StatusCodes.Status500InternalServerError, Wcs20Utilities.ExceptionCodes.NoApplicableCode, detail, null);

    private static IResult Create(int statusCode, string exceptionCode, string detail, string? locator)
    {
        var exceptionAttributes = new List<object>
        {
            new XAttribute("exceptionCode", exceptionCode)
        };

        if (!string.IsNullOrWhiteSpace(locator))
        {
            exceptionAttributes.Add(new XAttribute("locator", locator));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ows + "ExceptionReport",
                new XAttribute(XNamespace.Xmlns + "ows", Wcs20Utilities.OwsNamespace),
                new XAttribute(XNamespace.Xmlns + "xsi", Wcs20Utilities.XsiNamespace),
                new XAttribute("version", "2.0.0"),
                new XAttribute(Xsi + "schemaLocation",
                    $"{Wcs20Utilities.OwsNamespace} http://schemas.opengis.net/ows/2.0/owsExceptionReport.xsd"),
                new XElement(Ows + "Exception",
                    exceptionAttributes.ToArray(),
                    new XElement(Ows + "ExceptionText", detail))));

        return Results.Content(
            document.ToString(SaveOptions.DisableFormatting),
            Wcs20Utilities.XmlContentType,
            System.Text.Encoding.UTF8,
            statusCode);
    }
}
