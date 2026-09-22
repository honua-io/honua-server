// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;

namespace Honua.Protocols.Ogc.Classic.Wcs20;

/// <summary>
/// WCS 1.0.0 error results. 1.0.0 predates OWS, so a failure is an OGC
/// <c>ServiceExceptionReport</c> rather than the <c>ows:ExceptionReport</c> that
/// <see cref="Wcs20ErrorResults"/> emits for 2.0.1. Clients key their error reporting on
/// this shape - the QGIS provider parses a <c>ServiceException</c> element and reads its
/// <c>code</c> attribute - so returning an OWS report to a 1.0.0 client would leave the
/// failure unexplained in the UI even though the status code was correct
/// (honua-server#5020).
/// </summary>
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

    private static IResult Create(int statusCode, string exceptionCode, string detail, string? locator)
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
