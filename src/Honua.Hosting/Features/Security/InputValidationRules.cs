// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Honua.Infrastructure.Security;

internal readonly record struct ParameterInspectionContext(
    HttpRequest Request,
    string ParamType,
    string Name);

internal static partial class InputValidationRules
{
    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex QuotedStringLiteralPattern();

    // A WKT POINT/MULTIPOINT built only from numbers and WKT punctuation. No LDAP filter operator
    // (| & ! * =) can appear in a value this matches.
    [GeneratedRegex(@"^\s*(?:MULTI)?POINT\s*(?:Z\s*)?\(\s*[-+0-9.e\s,()]+\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EdrWktCoordsPattern();

    [GeneratedRegex(@"\$(select|filter|orderby|top|skip|count|expand|levels|search|compute|apply|deltatoken|format)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ODataSystemOptionPattern();

    private static readonly SearchValues<string> _ldapSuspiciousTokens = SearchValues.Create(
        [
            "(|",
            "(&",
            "(!",
            "|(",
            "&(",
            "!(",
            "*(",
            "*)",
            "*=",
            ")=",
            "(=",
            "))",
            "(("
        ],
        StringComparison.Ordinal);

    private static readonly SearchValues<char> _disallowedControlChars = CreateDisallowedControlChars();

    private static readonly FrozenSet<string> _featureEditPayloadFields = new[]
    {
        "adds",
        "updates",
        "deletes",
        "features",
        "edits"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> _oauth2CredentialFields = new[]
    {
        "token",
        "token_type_hint",
        "refresh_token",
        "code",
        "client_secret",
        "client_assertion",
        "assertion",
        "state",
        "code_challenge",
        "code_verifier",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> _geoServicesQueryPayloadFields = new[]
    {
        "objectIds",
        "outFields",
        "where",
        "orderByFields",
        "definitionExpression",
        "quantizationParameters",
        "layerDefs",
        "datumTransformation",
        "outStatistics",
        "groupByFieldsForStatistics",
        "having"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> _geometryServiceGeometryFields = new[]
    {
        "geometries",
        "geometries1",
        "geometries2",
        "geometry",
        "geometry1",
        "geometry2",
        "target",
        "cutter",
        "polylines",
        "polygons",
        "reshaper",
        "trimExtendTo",
        "envelope",
        "coordinates",
        "strings"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static bool ShouldSkipSqlInspection(ParameterInspectionContext parameter)
        => IsFeatureEditPayloadField(parameter)
           || IsOpaqueTokenCredentialParameter(parameter)
           || IsOAuth2CredentialParameter(parameter)
           || IsGenerateTokenPasswordParameter(parameter);

    internal static bool IsGeometryServiceGeometryField(ParameterInspectionContext parameter)
        => parameter.ParamType is "form" or "query"
           && _geometryServiceGeometryFields.Contains(parameter.Name)
           && parameter.Request.Path.Value?.Contains("/Utilities/Geometry/GeometryServer/", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool IsGeoServicesProtocolPayloadField(ParameterInspectionContext parameter)
    {
        if (IsFeatureEditPayloadField(parameter))
        {
            return true;
        }

        if (parameter.ParamType is not ("form" or "query") ||
            !_geoServicesQueryPayloadFields.Contains(parameter.Name))
        {
            return false;
        }

        var path = parameter.Request.Path.Value;
        return path?.Contains("/FeatureServer/", StringComparison.OrdinalIgnoreCase) == true ||
               path?.Contains("/MapServer/", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static string NormalizeForSqlInspection(ParameterInspectionContext parameter, string value)
    {
        if (IsODataSystemQueryOption(parameter))
        {
            return ODataSystemOptionPattern().Replace(value, string.Empty);
        }

        if (Cql2JsonLiteralMask.AppliesTo(parameter.Request, parameter.ParamType, parameter.Name))
        {
            return Cql2JsonLiteralMask.Mask(value);
        }

        if (IsSqlFilterExpressionParameter(parameter))
        {
            return QuotedStringLiteralPattern().Replace(value, "''");
        }

        return value;
    }

    internal static bool ShouldSkipLdapInspection(ParameterInspectionContext parameter, string value)
        => IsODataSystemQueryOption(parameter) ||
           IsSensorThingsSystemQueryOption(parameter) ||
           IsProtocolFilterQueryOption(parameter) ||
           IsPortalSearchQuery(parameter) ||
           IsEdrWktCoordsQueryOption(parameter, value);

    internal static bool ShouldSkipPathTraversalInspection(ParameterInspectionContext parameter, string value)
        => IsProtocolDatetimeQueryOption(parameter) &&
           IsOpenStartDatetimeInterval(value);

    internal static bool ContainsControlCharacters(string value)
        => value.AsSpan().ContainsAny(_disallowedControlChars);

    internal static bool ContainsPotentialLdapInjection(string value)
        => value.AsSpan().ContainsAny(_ldapSuspiciousTokens);

    internal static bool IsValidIpAddressList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan();
        var hasAny = false;
        foreach (var range in span.Split(','))
        {
            var segment = span[range];
            if (segment.IsEmpty)
            {
                continue;
            }

            hasAny = true;
            if (!IPAddress.TryParse(segment.Trim(), out _))
            {
                return false;
            }
        }

        return hasAny;
    }

    internal static bool IsMultipartFormData(string? contentType)
        => !string.IsNullOrWhiteSpace(contentType)
           && contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);

    internal static bool IsStreamingMultipartImportPath(string? path)
        => !string.IsNullOrEmpty(path)
           && (path.Contains("/admin/import/upload", StringComparison.OrdinalIgnoreCase)
               || (path.Contains("/admin/import/raster", StringComparison.OrdinalIgnoreCase)
                   && !path.EndsWith("/formats", StringComparison.OrdinalIgnoreCase)));

    private static bool IsOpaqueTokenCredentialParameter(ParameterInspectionContext parameter)
        => parameter.ParamType is "form" or "query"
           && parameter.Name.Equals("token", StringComparison.OrdinalIgnoreCase);

    private static bool IsGenerateTokenPasswordParameter(ParameterInspectionContext parameter)
        => parameter.ParamType is "form" or "query"
           && parameter.Name.Equals("password", StringComparison.OrdinalIgnoreCase)
           && (string.Equals(parameter.Request.Path.Value, "/sharing/rest/generateToken", StringComparison.OrdinalIgnoreCase)
               || string.Equals(parameter.Request.Path.Value, "/sharing/rest/generateToken/", StringComparison.OrdinalIgnoreCase));

    private static bool IsOAuth2CredentialParameter(ParameterInspectionContext parameter)
        => parameter.ParamType is "form" or "query"
           && _oauth2CredentialFields.Contains(parameter.Name)
           && parameter.Request.Path.Value?.Contains("/sharing/rest/oauth2/", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsFeatureEditPayloadField(ParameterInspectionContext parameter)
        => parameter.ParamType is "form" or "query"
           && _featureEditPayloadFields.Contains(parameter.Name)
           && parameter.Request.Path.Value?.Contains("/FeatureServer", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSqlFilterExpressionParameter(ParameterInspectionContext parameter)
        => parameter.ParamType is "form" or "query"
           && (parameter.Name.Equals("where", StringComparison.OrdinalIgnoreCase)
               || parameter.Name.Equals("having", StringComparison.OrdinalIgnoreCase)
               || parameter.Name.Equals("$filter", StringComparison.OrdinalIgnoreCase)
               || parameter.Name.Equals("filter", StringComparison.OrdinalIgnoreCase));

    private static bool IsPortalSearchQuery(ParameterInspectionContext parameter)
        => parameter.ParamType is "query" or "form"
           && parameter.Name.Equals("q", StringComparison.OrdinalIgnoreCase)
           && (string.Equals(parameter.Request.Path.Value, "/sharing/rest/search", StringComparison.OrdinalIgnoreCase)
               || string.Equals(parameter.Request.Path.Value, "/sharing/rest/search/", StringComparison.OrdinalIgnoreCase));

    private static bool IsEdrWktCoordsQueryOption(ParameterInspectionContext parameter, string value)
        => parameter.ParamType.Equals("query", StringComparison.Ordinal)
           && parameter.Name.Equals("coords", StringComparison.OrdinalIgnoreCase)
           && parameter.Request.Path.Value?.StartsWith("/edr/", StringComparison.OrdinalIgnoreCase) == true
           && EdrWktCoordsPattern().IsMatch(value);

    private static bool IsOpenStartDatetimeInterval(string value)
    {
        var span = value.AsSpan();
        Span<Range> parts = stackalloc Range[3];
        var count = span.Split(parts, '/', StringSplitOptions.TrimEntries);
        return count == 2 &&
               span[parts[0]].SequenceEqual("..") &&
               DateTimeOffset.TryParse(
                   span[parts[1]],
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out _);
    }

    private static bool IsProtocolDatetimeQueryOption(ParameterInspectionContext parameter)
    {
        if (!parameter.ParamType.Equals("query", StringComparison.Ordinal) ||
            !parameter.Name.Equals("datetime", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = parameter.Request.Path.Value;
        return path?.StartsWith("/ogc/", StringComparison.OrdinalIgnoreCase) == true ||
               path?.StartsWith("/stac", StringComparison.OrdinalIgnoreCase) == true ||
               path?.StartsWith("/edr/", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsODataSystemQueryOption(ParameterInspectionContext parameter)
        => parameter.ParamType.Equals("query", StringComparison.Ordinal)
           && parameter.Name.StartsWith('$')
           && parameter.Request.Path.Value?.StartsWith("/odata", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSensorThingsSystemQueryOption(ParameterInspectionContext parameter)
        => parameter.ParamType.Equals("query", StringComparison.Ordinal)
           && parameter.Name.StartsWith('$')
           && parameter.Request.Path.Value?.StartsWith("/sta/", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsProtocolFilterQueryOption(ParameterInspectionContext parameter)
    {
        if (parameter.ParamType is not ("query" or "form"))
        {
            return false;
        }

        var path = parameter.Request.Path.Value;
        if (parameter.Name.Equals("filter", StringComparison.OrdinalIgnoreCase))
        {
            return path?.StartsWith("/stac", StringComparison.OrdinalIgnoreCase) == true ||
                   path?.StartsWith("/ogc/features", StringComparison.OrdinalIgnoreCase) == true ||
                   path?.Contains("/wfs", StringComparison.OrdinalIgnoreCase) == true ||
                   path?.Contains("/wms", StringComparison.OrdinalIgnoreCase) == true;
        }

        return parameter.Name.Equals("where", StringComparison.OrdinalIgnoreCase) &&
               path?.StartsWith("/rest/services", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static SearchValues<char> CreateDisallowedControlChars()
    {
        Span<char> chars = stackalloc char[0x20 + 0x21];
        var count = 0;
        for (var codePoint = 0; codePoint <= 0x1F; codePoint++)
        {
            var ch = (char)codePoint;
            if (ch is not ('\t' or '\r' or '\n'))
            {
                chars[count++] = ch;
            }
        }

        for (var codePoint = 0x7F; codePoint <= 0x9F; codePoint++)
        {
            chars[count++] = (char)codePoint;
        }

        return SearchValues.Create(chars[..count]);
    }
}
