// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Honua.Protocols.Ogc.Api.Records;

internal enum OgcRecordOutputFormat
{
    GeoJson,
    Iso19139,
    Iso19115Part3,
    DcatAp,
}

internal sealed record OgcRecordFormatNegotiation(OgcRecordOutputFormat Format, int? ErrorStatus = null, string? Error = null);

// A record item is also served in the standard metadata encodings of its resolved descriptive
// metadata, chosen by f= or, without f, by the Accept header.
internal static class OgcRecordMetadataFormats
{
    public const string Iso19139MediaType = "application/vnd.iso.19139+xml";
    public const string Iso19115Part3MediaType = "application/vnd.iso.19115-3+xml";
    public const string DcatMediaType = "application/ld+json";

    private static readonly (string F, string MediaType, OgcRecordOutputFormat Format, string Title)[] Encodings =
    [
        ("iso19139", Iso19139MediaType, OgcRecordOutputFormat.Iso19139, "ISO 19139 metadata"),
        ("iso19115-3", Iso19115Part3MediaType, OgcRecordOutputFormat.Iso19115Part3, "ISO 19115-3 metadata"),
        ("dcat", DcatMediaType, OgcRecordOutputFormat.DcatAp, "DCAT-AP metadata"),
    ];

    private static readonly string[] GeoJsonMediaTypes = [MediaTypes.GeoJson, MediaTypes.Json, "*/*", "application/*"];

    // f wins; otherwise the highest-quality Accept range this record can be served as.
    public static OgcRecordFormatNegotiation Negotiate(HttpRequest request, string? f, DescribedItemKind kind)
    {
        if (!string.IsNullOrWhiteSpace(f))
        {
            var requested = FromFormatParameter(f);
            return requested.ErrorStatus is null && !Supports(requested.Format, kind)
                ? new(requested.Format, StatusCodes.Status406NotAcceptable,
                    $"This record describes a {kind.ToString().ToLowerInvariant()}; the ISO encodings describe datasets. Available: json, geojson, dcat.")
                : requested;
        }

        var accept = request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(accept) || !MediaTypeHeaderValue.TryParseList([accept], out var ranges))
        {
            return new(OgcRecordOutputFormat.GeoJson);
        }

        foreach (var range in ranges.Where(range => range.Quality is not 0).OrderByDescending(range => range.Quality ?? 1))
        {
            if (FormatFor(range.MediaType.Value ?? string.Empty, kind) is { } format)
            {
                return new(format);
            }
        }

        return new(OgcRecordOutputFormat.GeoJson, StatusCodes.Status406NotAcceptable,
            $"None of the accepted media types is available. Available: {MediaTypes.GeoJson}, {string.Join(", ", Encodings.Where(encoding => Supports(encoding.Format, kind)).Select(encoding => encoding.MediaType))}.");
    }

    // An unknown f is refused before the record is looked up.
    public static string? FormatParameterError(string? f)
        => string.IsNullOrWhiteSpace(f) ? null : FromFormatParameter(f).Error;

    // The ISO encodings describe datasets only; a service record is offered as DCAT-AP.
    public static bool Supports(OgcRecordOutputFormat format, DescribedItemKind kind)
        => kind == DescribedItemKind.Dataset || format is OgcRecordOutputFormat.GeoJson or OgcRecordOutputFormat.DcatAp;

    public static IEnumerable<Link> AlternateLinks(string selfHref, DescribedItemKind kind)
        => Encodings.Where(encoding => Supports(encoding.Format, kind)).Select(encoding =>
            Link.Create($"{selfHref}?f={encoding.F}", RelationTypes.Alternate, encoding.MediaType, encoding.Title));

    public static IResult Write(OgcRecordOutputFormat format, DescriptiveMetadata metadata, string itemUrl) => format switch
    {
        OgcRecordOutputFormat.Iso19139 => Xml(Iso19139Writer.Write(metadata), Iso19139MediaType),
        OgcRecordOutputFormat.Iso19115Part3 => Xml(Iso19115Part3Writer.Write(metadata), Iso19115Part3MediaType),
        OgcRecordOutputFormat.DcatAp => Results.Content(DcatApWriter.Write(metadata, itemUrl).ToJsonString(), DcatMediaType),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "GeoJSON records are written by the endpoint."),
    };

    private static OgcRecordFormatNegotiation FromFormatParameter(string f)
    {
        if (f.Equals("json", StringComparison.OrdinalIgnoreCase) || f.Equals("geojson", StringComparison.OrdinalIgnoreCase))
        {
            return new(OgcRecordOutputFormat.GeoJson);
        }

        return Encodings.FirstOrDefault(encoding => encoding.F.Equals(f, StringComparison.OrdinalIgnoreCase)) is { F: not null } match
            ? new(match.Format)
            : new(OgcRecordOutputFormat.GeoJson, StatusCodes.Status400BadRequest,
                $"Unsupported format '{f}'. Supported formats: json, geojson, {string.Join(", ", Encodings.Select(encoding => encoding.F))}.");
    }

    private static OgcRecordOutputFormat? FormatFor(string mediaType, DescribedItemKind kind)
    {
        if (Encodings.FirstOrDefault(encoding => encoding.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase)) is { F: not null } match)
        {
            return Supports(match.Format, kind) ? match.Format : null;
        }

        return GeoJsonMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase) ? OgcRecordOutputFormat.GeoJson : null;
    }

    // Stored text may hold characters XML cannot carry (control characters); they are replaced so the
    // record is still served instead of failing.
    private static IResult Xml(XDocument document, string mediaType)
    {
        foreach (var text in document.DescendantNodes().OfType<XText>())
        {
            text.Value = XmlSafe(text.Value);
        }

        foreach (var attribute in document.Descendants().Attributes())
        {
            attribute.Value = XmlSafe(attribute.Value);
        }

        return Results.Content(document.ToString(), $"{mediaType}; charset=utf-8");
    }

    private static string XmlSafe(string value)
    {
        if (value.All(XmlConvert.IsXmlChar))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (index + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[index + 1], value[index]))
            {
                builder.Append(value, index++, 2);
                continue;
            }

            builder.Append(XmlConvert.IsXmlChar(value[index]) ? value[index] : '\uFFFD');
        }

        return builder.ToString();
    }
}
