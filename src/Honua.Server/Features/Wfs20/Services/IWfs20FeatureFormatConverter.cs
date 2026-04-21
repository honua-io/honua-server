// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Interface for converting between different feature formats in WFS 2.0 operations.
/// </summary>
internal interface IWfs20FeatureFormatConverter
{
    /// <summary>
    /// Converts a GML feature element to a domain feature object.
    /// </summary>
    /// <param name="gmlElement">GML feature element from XML request</param>
    /// <returns>Domain feature object</returns>
    Feature ConvertGmlToFeature(XElement gmlElement);

    /// <summary>
    /// Converts a domain feature to a GML feature for serialization.
    /// </summary>
    /// <param name="feature">Domain feature object</param>
    /// <returns>GML feature representation</returns>
    GmlFeature ConvertFeatureToGml(Feature feature);

    /// <summary>
    /// Extracts geometry from a GML element.
    /// </summary>
    /// <param name="gmlElement">GML element containing geometry</param>
    /// <returns>Geometry in WKT or WKB format, or null if no geometry</returns>
    string? ExtractGeometry(XElement gmlElement);

    /// <summary>
    /// Extracts attribute values from a GML feature element.
    /// </summary>
    /// <param name="gmlElement">GML feature element</param>
    /// <returns>Dictionary of attribute names to values</returns>
    Dictionary<string, object?> ExtractAttributes(XElement gmlElement);

    /// <summary>
    /// Converts a feature attribute value to the appropriate .NET type.
    /// </summary>
    /// <param name="value">Raw attribute value from XML</param>
    /// <param name="expectedType">Expected .NET type</param>
    /// <returns>Converted value</returns>
    object? ConvertAttributeValue(string value, Type expectedType);
}