// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Resolves WFS property references against the reversible XML local-name spelling emitted
/// by DescribeFeatureType and GML feature responses.
/// </summary>
internal static class WfsPropertyNameResolver
{
    /// <summary>
    /// Resolves canonical field names, ordinary QName-prefixed names, and the encoded NCName
    /// declared by WFS XSD (for example <c>eo_x003A_cloud_cover</c>).
    /// </summary>
    internal static string? Resolve(
        MetadataV2Resource resource,
        string requestedName,
        bool allowGeometryAlias)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(requestedName);

        var requested = requestedName.Trim();
        if (requested.Length == 0)
        {
            return null;
        }

        var localName = requested;
        var lastSlash = localName.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < localName.Length - 1)
        {
            localName = localName[(lastSlash + 1)..];
        }

        // A ValueReference can qualify the schema-declared local name with the feature
        // namespace (honua:eo_x003A_cloud_cover). Strip that QName prefix before decoding.
        var colon = localName.LastIndexOf(':');
        if (colon >= 0 && colon < localName.Length - 1)
        {
            localName = localName[(colon + 1)..];
        }

        // Prefer the schema-advertised spelling. A canonical field can itself look like an
        // XML escape, but EncodeLocalName escapes its underscore and therefore advertises a
        // different name from a colon-bearing field.
        var encodedField = resource.SchemaFields.FirstOrDefault(field =>
            XmlConvert.EncodeLocalName(field.Name).Equals(localName, StringComparison.OrdinalIgnoreCase));
        if (encodedField is not null)
        {
            return encodedField.Name;
        }

        // Try the canonical spelling before treating ':' as an XML namespace separator, so
        // a STAC-style field such as eo:cloud_cover remains addressable directly too.
        var exactField = FindExactField(resource, requested);
        if (exactField is not null)
        {
            return exactField;
        }

        var resolved = FilterExpressionHelpers.ResolveFieldName(resource, requested, allowGeometryAlias);
        if (resolved is not null)
        {
            return resolved;
        }

        var decodedName = XmlConvert.DecodeName(localName);
        return FindExactField(resource, decodedName);
    }

    private static string? FindExactField(MetadataV2Resource resource, string requestedName)
        => resource.SchemaFields.FirstOrDefault(field =>
            field.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase))?.Name;
}
