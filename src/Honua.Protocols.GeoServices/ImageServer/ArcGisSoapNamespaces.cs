// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;

namespace Honua.Protocols.GeoServices.ImageServer;

/// <summary>
/// ArcGIS SOAP schema namespaces accepted by the bounded compatibility surface.
/// </summary>
internal static class ArcGisSoapNamespaces
{
    internal const string Legacy = "http://www.esri.com/schemas/ArcGIS/9.0";
    internal const string Current = "http://www.esri.com/schemas/ArcGIS/10.8";

    /// <summary>
    /// Determines whether a namespace is one of the explicitly supported ArcGIS schemas.
    /// ArcGIS Pro 3.7 still negotiates the services catalog with the 9.0 schema even
    /// when it consumes a current REST service.
    /// </summary>
    internal static bool IsSupported(XNamespace value)
        => value == Legacy || value == Current;

    /// <summary>
    /// Serializes an ArcGIS SOAP document with the XML declaration emitted by
    /// ArcGIS Server. The default string representation drops the declaration.
    /// </summary>
    internal static string SerializeResponse(XDocument document)
        => string.Concat(
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?>",
            document.ToString(SaveOptions.DisableFormatting));
}
