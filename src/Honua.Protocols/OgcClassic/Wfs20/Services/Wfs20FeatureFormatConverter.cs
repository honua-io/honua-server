// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal sealed class Wfs20FeatureFormatConverter
{
    private static readonly XNamespace GmlNamespace = "http://www.opengis.net/gml/3.2";
    private readonly ILogger<Wfs20FeatureFormatConverter> _logger;

    public Wfs20FeatureFormatConverter(ILogger<Wfs20FeatureFormatConverter> logger)
    {
        _logger = logger;
    }

    public string? ExtractGeometry(XElement gmlElement)
    {
        ArgumentNullException.ThrowIfNull(gmlElement);

        try
        {
            // Look for geometry property elements
            var geometryProperty = gmlElement.Elements()
                .FirstOrDefault(e => e.Name.LocalName.EndsWith("geometry", StringComparison.OrdinalIgnoreCase) ||
                                    e.Name.Namespace == GmlNamespace);

            if (geometryProperty == null)
                return null;

            // Find the actual geometry element (child of the property)
            var geometryElement = geometryProperty.Elements()
                .FirstOrDefault(e => e.Name.Namespace == GmlNamespace);

            if (geometryElement == null)
                return null;

            // For a full implementation, this would parse the GML geometry to WKT
            // For now, return the GML as a string (this would need a proper GML parser)
            return geometryElement.ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally generic: this is a best-effort GML geometry extraction step;
            // any malformed/unexpected XML shape should log and yield null rather than fail.
            Wfs20Log.FeatureSerializationFailed(_logger, "gml-geometry-extract", ex.Message);
            return null;
        }
    }
}
