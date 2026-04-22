// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Interface for serializing features and collections to GML format for WFS 2.0 responses.
/// </summary>
internal interface IGmlSerializer
{
    /// <summary>
    /// Serializes a single feature to GML 3.2 format.
    /// </summary>
    /// <param name="feature">Feature to serialize</param>
    /// <param name="featureTypeName">Name of the feature type</param>
    /// <param name="namespaceUri">Namespace URI for the feature type</param>
    /// <returns>GML representation of the feature</returns>
    XElement SerializeFeature(GmlFeature feature, string featureTypeName, string namespaceUri);

    /// <summary>
    /// Serializes a collection of features to a GML FeatureCollection.
    /// </summary>
    /// <param name="features">Features to serialize</param>
    /// <param name="featureTypeName">Name of the feature type</param>
    /// <param name="namespaceUri">Namespace URI for the feature type</param>
    /// <param name="numberMatched">Total number of features matched (for pagination)</param>
    /// <param name="numberReturned">Number of features returned in this response</param>
    /// <returns>GML FeatureCollection element</returns>
    XElement SerializeFeatureCollection(
        IEnumerable<GmlFeature> features,
        string featureTypeName,
        string namespaceUri,
        int? numberMatched = null,
        int? numberReturned = null);

    /// <summary>
    /// Serializes geometry to GML format.
    /// </summary>
    /// <param name="geometryGml">Pre-encoded GML geometry fragment</param>
    /// <returns>GML geometry element</returns>
    XElement? SerializeGeometry(string? geometryGml);
}
