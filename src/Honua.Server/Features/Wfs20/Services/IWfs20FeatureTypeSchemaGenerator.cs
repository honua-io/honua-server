// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Interface for generating XML Schema (XSD) definitions for feature types in WFS 2.0 DescribeFeatureType responses.
/// </summary>
internal interface IWfs20FeatureTypeSchemaGenerator
{
    /// <summary>
    /// Generates an XML Schema document for a feature type.
    /// </summary>
    /// <param name="layer">Layer metadata</param>
    /// <param name="targetNamespace">Target namespace URI for the feature type</param>
    /// <param name="featureTypeName">Name of the feature type</param>
    /// <returns>XML Schema document describing the feature type</returns>
    XDocument GenerateFeatureTypeSchema(LayerDefinition layer, string targetNamespace, string featureTypeName);

    /// <summary>
    /// Generates XML Schema elements for layer fields.
    /// </summary>
    /// <param name="layer">Layer metadata</param>
    /// <param name="targetNamespace">Target namespace URI</param>
    /// <returns>Collection of XSD elements for layer fields</returns>
    IEnumerable<XElement> GenerateFieldElements(LayerDefinition layer, string targetNamespace);

    /// <summary>
    /// Maps a layer field type to the corresponding XSD type.
    /// </summary>
    /// <param name="fieldType">Layer field type</param>
    /// <returns>XSD type name</returns>
    string MapFieldTypeToXsdType(FieldType fieldType);
}
