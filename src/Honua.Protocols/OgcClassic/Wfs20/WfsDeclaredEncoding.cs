// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Xml;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Ogc.Classic.Wfs20;

internal readonly record struct WfsDeclaredBoundedBy(string SrsName, string Coordinates);

// What a deployer declares on a resource under extensions.wfs so a feature type can follow an
// application schema defined outside the server: its XML namespace, and how WFS 1.0 and 1.1
// encode its features. The JSON is read by hand because the server is published native AOT,
// and missing or malformed metadata reads as "nothing declared": resource metadata is
// deployer-authored and must never fail a capabilities or feature request.
internal sealed record WfsDeclaredEncoding
{
    internal static readonly WfsDeclaredEncoding None = new();

    private const string ExtensionKey = "wfs";

    // Prefixes the WFS documents bind themselves; a feature namespace may not rebind them.
    private static readonly string[] ReservedPrefixes =
        ["xml", "xmlns", "xsi", "xsd", "xs", "xlink", "wfs", "gml", "ogc", "ows", "fes"];

    public string? NamespacePrefix { get; private init; }
    public string? NamespaceUri { get; private init; }
    public bool OmitNullProperties { get; private init; }
    public bool GeometryAfterAttributes { get; private init; }
    public string[] HiddenFields { get; private init; } = [];
    public string[] GmlFields { get; private init; } = [];
    public string? Wfs10Schema { get; private init; }
    public WfsDeclaredBoundedBy? Wfs10BoundedBy { get; private init; }

    internal bool IsHidden(string fieldName) => Contains(HiddenFields, fieldName);

    internal bool IsGmlField(string fieldName) => Contains(GmlFields, fieldName);

    internal WfsDeclaredEncoding WithoutNamespace() => this with { NamespacePrefix = null, NamespaceUri = null };

    internal static WfsDeclaredEncoding Read(MetadataV2Object resource)
    {
        if (!resource.Extensions.TryGetValue(ExtensionKey, out var extension) ||
            extension.ValueKind != JsonValueKind.Object)
        {
            return None;
        }

        var (prefix, uri) = ReadNamespace(extension);
        var encoding = extension.TryGetProperty("legacyEncoding", out var legacy) && legacy.ValueKind == JsonValueKind.Object
            ? legacy
            : default;
        if (prefix is null && encoding.ValueKind != JsonValueKind.Object)
        {
            return None;
        }

        return new WfsDeclaredEncoding
        {
            NamespacePrefix = prefix,
            NamespaceUri = uri,
            OmitNullProperties = ReadFlag(encoding, "omitNullProperties"),
            GeometryAfterAttributes = ReadFlag(encoding, "geometryAfterAttributes"),
            HiddenFields = ReadNames(encoding, "hiddenFields"),
            GmlFields = ReadNames(encoding, "gmlFields"),
            Wfs10Schema = ReadText(encoding, "wfs10Schema"),
            Wfs10BoundedBy = ReadBoundedBy(encoding)
        };
    }

    private static (string? Prefix, string? Uri) ReadNamespace(JsonElement extension)
    {
        if (!extension.TryGetProperty("namespace", out var declared) ||
            ReadText(declared, "prefix") is not { } prefix ||
            ReadText(declared, "uri") is not { } uri ||
            !IsUsablePrefix(prefix) ||
            !IsUsableUri(uri))
        {
            return (null, null);
        }

        return (prefix, uri);
    }

    // A scheme is required: on Linux a rooted path such as "/x" also parses as an absolute URI.
    private static bool IsUsableUri(string uri)
        => System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
           !parsed.IsFile &&
           IsXmlText(uri);

    // Text the XML writer accepts; a control character would fail the response.
    private static bool IsXmlText(string text)
    {
        try
        {
            XmlConvert.VerifyXmlChars(text);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool IsUsablePrefix(string prefix)
    {
        if (Contains(ReservedPrefixes, prefix))
        {
            return false;
        }

        try
        {
            XmlConvert.VerifyNCName(prefix);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static WfsDeclaredBoundedBy? ReadBoundedBy(JsonElement encoding)
    {
        if (encoding.ValueKind != JsonValueKind.Object ||
            !encoding.TryGetProperty("wfs10BoundedBy", out var box) ||
            ReadText(box, "srsName") is not { } srsName ||
            ReadText(box, "coordinates") is not { } coordinates ||
            !IsXmlText(srsName) ||
            !IsXmlText(coordinates))
        {
            return null;
        }

        return new WfsDeclaredBoundedBy(srsName, coordinates);
    }

    private static string[] ReadNames(JsonElement parent, string property)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(property, out var names) &&
           names.ValueKind == JsonValueKind.Array
            ? [.. names.EnumerateArray()
                .Where(name => name.ValueKind == JsonValueKind.String)
                .Select(name => name.GetString())
                .Where(name => !string.IsNullOrWhiteSpace(name))!]
            : [];

    private static string? ReadText(JsonElement parent, string property)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(property, out var element) &&
           element.ValueKind == JsonValueKind.String &&
           element.GetString() is { } text &&
           !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static bool ReadFlag(JsonElement parent, string property)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(property, out var element) &&
           element.ValueKind == JsonValueKind.True;

    private static bool Contains(string[] names, string name)
        => Array.Exists(names, candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
}
