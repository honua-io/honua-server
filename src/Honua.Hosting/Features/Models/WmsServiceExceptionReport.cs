// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security;
using System.Text;

namespace Honua.Infrastructure.Models;

/// <summary>
/// Version-aware WMS ServiceExceptionReport serialization shared by the WMS protocol
/// handler and the cross-protocol <see cref="StandardErrorResponseFormatter"/>, so an
/// error raised before the WMS handler runs (for example an invalid portal token
/// rejected by middleware) is byte-for-byte the same envelope the handler emits.
/// </summary>
internal static class WmsServiceExceptionReport
{
    internal const string Wms13Version = "1.3.0";
    internal const string Wms111Version = "1.1.1";
    internal const string XmlMimeType = "text/xml";
    internal const string SeXmlMimeType = "application/vnd.ogc.se_xml";
    internal const string Wms13SchemaLocation = "http://www.opengis.net/ogc http://schemas.opengis.net/wms/1.3.0/exceptions_1_3_0.xsd";
    internal const string Wms111Dtd = "http://schemas.opengis.net/wms/1.1.1/exception_1_1_1.dtd";

    /// <summary>
    /// Returns the requested WMS version (VERSION, falling back to the pre-1.0 WMTVER key).
    /// </summary>
    internal static string? GetRequestedVersion(IQueryCollection query)
    {
        var version = query.TryGetValue("VERSION", out var value) ? value.ToString() : null;
        return string.IsNullOrWhiteSpace(version)
            ? query.TryGetValue("WMTVER", out var legacy) ? legacy.ToString() : null
            : version;
    }

    internal static bool IsWms111(string? version)
        => string.Equals(version, Wms111Version, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Selects the exception media type: an explicit EXCEPTIONS=application/vnd.ogc.se_xml,
    /// or a WMS 1.1.1 request without EXCEPTIONS, uses the 1.1.1 se_xml type.
    /// </summary>
    internal static string GetMimeType(string? exceptionsValue, string? version)
        => string.Equals(exceptionsValue, SeXmlMimeType, StringComparison.OrdinalIgnoreCase) ||
           (string.IsNullOrWhiteSpace(exceptionsValue) && IsWms111(version))
            ? SeXmlMimeType
            : XmlMimeType;

    /// <summary>
    /// Builds the ServiceExceptionReport for the requested version: the WMS 1.1.1 DTD form
    /// (OGC 01-068r3 Annex A.4) or the WMS 1.3.0 namespaced form.
    /// </summary>
    internal static string Build(string code, string message, string? version)
    {
        if (IsWms111(version))
        {
            // WMS 1.1.1 (OGC 01-068r3 Annex A.4) names this exception code InvalidSRS;
            // InvalidCRS is the WMS 1.3.0 equivalent.
            if (string.Equals(code, "InvalidCRS", StringComparison.OrdinalIgnoreCase))
            {
                code = "InvalidSRS";
            }

            var legacy = new StringBuilder(512);
            legacy.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            legacy.Append("<!DOCTYPE ServiceExceptionReport SYSTEM \"")
                .Append(Wms111Dtd)
                .AppendLine("\">");
            legacy.AppendLine("<ServiceExceptionReport version=\"1.1.1\">");
            legacy.Append("  <ServiceException code=\"")
                .Append(Escape(code))
                .Append("\">")
                .Append(Escape(message))
                .AppendLine("</ServiceException>");
            legacy.AppendLine("</ServiceExceptionReport>");
            return legacy.ToString();
        }

        var sb = new StringBuilder(512);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<ServiceExceptionReport xmlns=\"http://www.opengis.net/ogc\" ")
            .Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ")
            .Append("version=\"1.3.0\" xsi:schemaLocation=\"")
            .Append(Wms13SchemaLocation)
            .AppendLine("\">");
        sb.Append("  <ServiceException code=\"")
            .Append(Escape(code))
            .Append("\">")
            .Append(Escape(message))
            .AppendLine("</ServiceException>");
        sb.AppendLine("</ServiceExceptionReport>");
        return sb.ToString();
    }

    /// <summary>
    /// Creates the XML ServiceExceptionReport result for the request's WMS version and
    /// EXCEPTIONS parameter, preserving <paramref name="statusCode"/>.
    /// </summary>
    internal static IResult CreateXmlResult(HttpContext context, string code, string message, int statusCode)
    {
        var query = context.Request.Query;
        var version = GetRequestedVersion(query);
        var exceptionsValue = query.TryGetValue("EXCEPTIONS", out var exceptions) ? exceptions.ToString() : null;
        return Results.Content(
            Build(code, message, version),
            GetMimeType(exceptionsValue, version),
            Encoding.UTF8,
            statusCode);
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
