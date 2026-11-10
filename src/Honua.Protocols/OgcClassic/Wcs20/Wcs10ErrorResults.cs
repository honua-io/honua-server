// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;

namespace Honua.Protocols.Ogc.Classic.Wcs20;

// WCS 1.0.0 returns a ServiceExceptionReport, because QGIS parses that element and an OWS ExceptionReport leaves the error unexplained.
internal static class Wcs10ErrorResults
{
    private static readonly XNamespace Ogc = Wcs20Utilities.OgcExceptionNamespace;

    internal static IResult CreateBadRequest(string exceptionCode, string detail, string? locator = null)
        => Create(StatusCodes.Status400BadRequest, exceptionCode, detail, locator);

    internal static IResult CreateNotFound(string exceptionCode, string detail, string? locator = null)
        => Create(StatusCodes.Status404NotFound, exceptionCode, detail, locator);

    internal static IResult CreateForbidden(string detail)
        => Create(StatusCodes.Status403Forbidden, Wcs20Utilities.ExceptionCodes10.InvalidParameterValue, detail, null);

    internal static IResult CreateUnauthorized(string detail)
        => Create(StatusCodes.Status401Unauthorized, Wcs20Utilities.ExceptionCodes10.InvalidParameterValue, detail, null);

    internal static IResult CreateNotImplemented(string exceptionCode, string detail, string? locator = null)
        => Create(StatusCodes.Status501NotImplemented, exceptionCode, detail, locator);

    internal static IResult CreateInternalServerError(string detail)
        => Create(StatusCodes.Status500InternalServerError, Wcs20Utilities.ExceptionCodes10.InvalidParameterValue, detail, null);

    internal static IResult Create(int statusCode, string exceptionCode, string detail, string? locator)
    {
        // The 1.0.0 ServiceException carries the offending parameter in `locator`, and
        // the message as element text. Unlike OWS there is no separate ExceptionText
        // child, so the detail and the locator hint are combined into the text when a
        // locator is present.
        var message = string.IsNullOrWhiteSpace(locator)
            ? detail
            : $"{detail} (parameter: {locator})";

        var exception = new XElement(Ogc + "ServiceException",
            new XAttribute("code", exceptionCode),
            message);

        if (!string.IsNullOrWhiteSpace(locator))
        {
            exception.Add(new XAttribute("locator", locator));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ogc + "ServiceExceptionReport",
                new XAttribute(XNamespace.Xmlns + "ogc", Wcs20Utilities.OgcExceptionNamespace),
                new XAttribute("version", "1.2.0"),
                exception));

        return Results.Content(
            document.ToString(SaveOptions.DisableFormatting),
            Wcs20Utilities.OgcServiceExceptionContentType,
            System.Text.Encoding.UTF8,
            statusCode);
    }
}
