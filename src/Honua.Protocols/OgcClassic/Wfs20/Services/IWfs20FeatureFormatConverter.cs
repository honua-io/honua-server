// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal interface IWfs20FeatureFormatConverter
{
    Feature ConvertGmlToFeature(XElement gmlElement);

    GmlFeature ConvertFeatureToGml(Feature feature);

    string? ExtractGeometry(XElement gmlElement);

    Dictionary<string, object?> ExtractAttributes(XElement gmlElement);

    object? ConvertAttributeValue(string value, Type expectedType);
}
