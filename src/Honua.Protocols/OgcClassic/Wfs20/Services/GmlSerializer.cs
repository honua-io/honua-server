// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Xml.Linq;
using System.Xml;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal sealed class GmlSerializer
{
    private static readonly XNamespace GmlNamespace = "http://www.opengis.net/gml/3.2";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Retains the instance API used by dependency-injected consumers.")]
    public XElement? SerializeGeometry(string? geometryGml)
    {
        if (string.IsNullOrEmpty(geometryGml))
        {
            return null;
        }

        try
        {
            // PA-107: use explicit XmlReaderSettings with DtdProcessing.Prohibit to make the
            // DTD prohibition consistent with SecureXmlDocumentParser used elsewhere in the codebase,
            // and to document the security decision at the call site rather than relying on runtime defaults.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            XElement gmlElement;
            using (var reader = XmlReader.Create(new System.IO.StringReader(geometryGml), settings))
            {
                gmlElement = XElement.Load(reader);
            }

            var geometryProperty = new XElement(GmlNamespace + "geometryProperty");
            geometryProperty.Add(gmlElement);
            return geometryProperty;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Xml.XmlException)
        {
            // If GML parsing fails, return null (no geometry)
            return null;
        }
    }

    private static string FormatXmlDateTime(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return FormatXmlDateTimeOffset(new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero));
    }

    private static string FormatXmlDateTimeOffset(DateTimeOffset value)
    {
        var format = value.Millisecond == 0
            ? "yyyy-MM-dd'T'HH:mm:sszzz"
            : "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}
