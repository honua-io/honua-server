// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal interface IGmlSerializer
{
    XElement SerializeFeature(GmlFeature feature, string featureTypeName, string namespaceUri);

    XElement SerializeFeatureCollection(
        IEnumerable<GmlFeature> features,
        string featureTypeName,
        string namespaceUri,
        int? numberMatched = null,
        int numberReturned = 0);

    XElement? SerializeGeometry(string? geometryGml);
}
