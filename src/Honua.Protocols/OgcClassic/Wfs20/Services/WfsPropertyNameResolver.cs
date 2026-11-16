// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal static class WfsPropertyNameResolver
{
    internal static string? Resolve(
        MetadataV2Object resource,
        string requestedName,
        bool allowGeometryAlias,
        bool includeHiddenFields = false)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(requestedName);

        if (!includeHiddenFields)
        {
            resource = resource with
            {
                SchemaFields = resource.SchemaFields.Where(field => !field.Hidden).ToArray()
            };
        }
        var requested = requestedName.Trim();
        if (requested.Length == 0)
        {
            return null;
        }

        var localName = ExtractLocalName(requested);

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

    internal static bool MatchesGeometryProperty(string requestedName, string geometryPropertyName)
    {
        ArgumentNullException.ThrowIfNull(requestedName);
        ArgumentNullException.ThrowIfNull(geometryPropertyName);

        var localName = ExtractLocalName(requestedName.Trim());
        if (localName.Length == 0)
        {
            return false;
        }

        // 'geometry' and 'shape' are the aliases FilterExpressionHelpers accepts for an
        // explicit geometry field, so the fallback property stays addressable the same way.
        return localName.Equals(geometryPropertyName, StringComparison.OrdinalIgnoreCase) ||
               XmlConvert.DecodeName(localName).Equals(geometryPropertyName, StringComparison.OrdinalIgnoreCase) ||
               localName.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
               localName.Equals("shape", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractLocalName(string requested)
    {
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

        return localName;
    }

    private static string? FindExactField(MetadataV2Object resource, string requestedName)
        => resource.SchemaFields.FirstOrDefault(field =>
            field.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase))?.Name;
}
