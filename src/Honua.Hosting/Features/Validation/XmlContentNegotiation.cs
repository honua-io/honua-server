// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Validation;

internal static class XmlContentNegotiation
{
    private static readonly string[] DefaultXmlMediaTypes =
    [
        "application/xml",
        "text/xml",
        "application/gml+xml"
    ];

    public static bool IsXmlAccepted(string? acceptHeader)
        => IsXmlAccepted(acceptHeader, DefaultXmlMediaTypes);

    public static bool IsXmlAccepted(string? acceptHeader, IReadOnlyList<string> supportedXmlMediaTypes)
    {
        ArgumentNullException.ThrowIfNull(supportedXmlMediaTypes);

        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            return true;
        }

        var acceptValues = new StringValues(acceptHeader);
        return ContentNegotiationHelpers.TrySelectBestMediaType(
            supportedXmlMediaTypes,
            acceptValues,
            out _);
    }
}
