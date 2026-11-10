// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;

namespace Honua.Protocols.GeoServices.Soap;

internal static class GeoServicesSoapNamespaces
{
    internal const string Legacy = "http://www.esri.com/schemas/ArcGIS/9.0";
    internal const string Current = "http://www.esri.com/schemas/ArcGIS/10.8";

    // ArcGIS Pro 3.7 negotiates the services catalog with the 9.0 schema even when it consumes a current REST service.
    internal static bool IsSupported(XNamespace value)
        => value == Legacy || value == Current;

    // ArcGIS Server emits an XML declaration, and XDocument.ToString drops it, so the declaration is prefixed here.
    internal static string SerializeResponse(XDocument document)
        => string.Concat(
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?>",
            document.ToString(SaveOptions.DisableFormatting));
}
