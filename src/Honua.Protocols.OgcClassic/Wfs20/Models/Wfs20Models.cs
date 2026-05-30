// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Xml;
using System.Xml.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Models;

// These types are public by design because XmlSerializer requires public CLR contracts for
// the WFS 2.0 XML payloads emitted by the protocol endpoints.

/// <summary>
/// WFS 2.0 GetCapabilities response
/// </summary>
[XmlRoot("WFS_Capabilities", Namespace = Wfs20Utilities.WfsNamespace)]
public sealed class WfsCapabilities : IXmlNamespaceProvider
{
    [XmlNamespaceDeclarations]
    public XmlSerializerNamespaces Namespaces { get; } = new(
        new[]
        {
            new XmlQualifiedName(string.Empty, Wfs20Utilities.WfsNamespace),
            new XmlQualifiedName("ows", Wfs20Utilities.OwsNamespace),
            new XmlQualifiedName("fes", Wfs20Utilities.FesNamespace),
            new XmlQualifiedName("gml", Wfs20Utilities.GmlNamespace),
            new XmlQualifiedName("honua", "http://honua.io/wfs"),
            new XmlQualifiedName("xlink", Wfs20Utilities.XLinkNamespace),
            new XmlQualifiedName("xsd", Wfs20Utilities.XsdNamespace),
            new XmlQualifiedName("xsi", Wfs20Utilities.XsiNamespace),
        });

    [XmlAttribute("version")]
    public string Version { get; set; } = Wfs20Utilities.Version;

    [XmlAttribute("updateSequence")]
    public string? UpdateSequence { get; set; }

    [XmlAttribute("schemaLocation", Namespace = Wfs20Utilities.XsiNamespace)]
    public string SchemaLocation { get; set; } =
        $"{Wfs20Utilities.WfsNamespace} http://schemas.opengis.net/wfs/2.0/wfs.xsd";

    [XmlElement("ServiceIdentification", Namespace = Wfs20Utilities.OwsNamespace)]
    public ServiceIdentification? ServiceIdentification { get; set; }

    [XmlElement("ServiceProvider", Namespace = Wfs20Utilities.OwsNamespace)]
    public ServiceProvider? ServiceProvider { get; set; }

    [XmlElement("OperationsMetadata", Namespace = Wfs20Utilities.OwsNamespace)]
    public OperationsMetadata? OperationsMetadata { get; set; }

    [XmlElement("FeatureTypeList")]
    public FeatureTypeList? FeatureTypeList { get; set; }

    [XmlElement("Filter_Capabilities", Namespace = Wfs20Utilities.FesNamespace)]
    public FilterCapabilities? FilterCapabilities { get; set; }
}

/// <summary>
/// Service identification information
/// </summary>
public sealed class ServiceIdentification
{
    [XmlElement("Title", Namespace = Wfs20Utilities.OwsNamespace)]
    public string Title { get; set; } = "Honua WFS 2.0";

    [XmlElement("Abstract", Namespace = Wfs20Utilities.OwsNamespace)]
    public string Abstract { get; set; } = "Honua WFS 2.0 implementation providing standards-based access to geospatial features";

    [XmlArray("Keywords", Namespace = Wfs20Utilities.OwsNamespace)]
    [XmlArrayItem("Keyword", Namespace = Wfs20Utilities.OwsNamespace)]
    public string[] Keywords { get; set; } = { "WFS", "OGC", "features", "geospatial" };

    [XmlElement("ServiceType", Namespace = Wfs20Utilities.OwsNamespace)]
    public string ServiceType { get; set; } = Wfs20Utilities.ServiceType;

    [XmlElement("ServiceTypeVersion", Namespace = Wfs20Utilities.OwsNamespace)]
    public string[] ServiceTypeVersion { get; set; } = [Wfs20Utilities.Version];

    [XmlElement("Fees", Namespace = Wfs20Utilities.OwsNamespace)]
    public string Fees { get; set; } = "NONE";

    [XmlElement("AccessConstraints", Namespace = Wfs20Utilities.OwsNamespace)]
    public string AccessConstraints { get; set; } = "NONE";
}

/// <summary>
/// Service provider information
/// </summary>
public sealed class ServiceProvider
{
    [XmlElement("ProviderName", Namespace = Wfs20Utilities.OwsNamespace)]
    public string ProviderName { get; set; } = "Honua";

    [XmlElement("ProviderSite", Namespace = Wfs20Utilities.OwsNamespace)]
    public ProviderSite ProviderSite { get; set; } = new() { Href = "https://honua.io" };

    [XmlElement("ServiceContact", Namespace = Wfs20Utilities.OwsNamespace)]
    public ServiceContact ServiceContact { get; set; } = new();
}

/// <summary>
/// Provider site link
/// </summary>
public sealed class ProviderSite
{
    [XmlAttribute("href", Namespace = Wfs20Utilities.XLinkNamespace)]
    public required string Href { get; set; }
}

/// <summary>
/// Service contact information
/// </summary>
public sealed class ServiceContact
{
    [XmlElement("IndividualName", Namespace = Wfs20Utilities.OwsNamespace)]
    public string? IndividualName { get; set; }

    [XmlElement("PositionName", Namespace = Wfs20Utilities.OwsNamespace)]
    public string? PositionName { get; set; }

    [XmlElement("ContactInfo", Namespace = Wfs20Utilities.OwsNamespace)]
    public ContactInfo? ContactInfo { get; set; }

    [XmlElement("Role", Namespace = Wfs20Utilities.OwsNamespace)]
    public string Role { get; set; } = "pointOfContact";
}

/// <summary>
/// Contact information
/// </summary>
public sealed class ContactInfo
{
    [XmlElement("Address", Namespace = Wfs20Utilities.OwsNamespace)]
    public Address? Address { get; set; }

    [XmlElement("OnlineResource", Namespace = Wfs20Utilities.OwsNamespace)]
    public OnlineResource? OnlineResource { get; set; }
}

/// <summary>
/// Address information
/// </summary>
public sealed class Address
{
    [XmlElement("ElectronicMailAddress", Namespace = Wfs20Utilities.OwsNamespace)]
    public string? ElectronicMailAddress { get; set; }
}

/// <summary>
/// Online resource reference
/// </summary>
public sealed class OnlineResource
{
    [XmlAttribute("href", Namespace = Wfs20Utilities.XLinkNamespace)]
    public required string Href { get; set; }
}

/// <summary>
/// Operations metadata
/// </summary>
public sealed class OperationsMetadata
{
    [XmlElement("Operation", Namespace = Wfs20Utilities.OwsNamespace)]
    public required Operation[] Operations { get; set; }

    [XmlElement("Parameter", Namespace = Wfs20Utilities.OwsNamespace)]
    public Parameter[]? Parameters { get; set; }

    [XmlElement("Constraint", Namespace = Wfs20Utilities.OwsNamespace)]
    public Constraint[]? Constraints { get; set; }
}

/// <summary>
/// Operation description
/// </summary>
public sealed class Operation
{
    [XmlAttribute("name")]
    public required string Name { get; set; }

    [XmlElement("DCP", Namespace = Wfs20Utilities.OwsNamespace)]
    public required DCP[] DCP { get; set; }

    [XmlElement("Parameter", Namespace = Wfs20Utilities.OwsNamespace)]
    public Parameter[]? Parameters { get; set; }

    [XmlElement("Constraint", Namespace = Wfs20Utilities.OwsNamespace)]
    public Constraint[]? Constraints { get; set; }
}

/// <summary>
/// Distributed Computing Platform (DCP) - HTTP methods
/// </summary>
public sealed class DCP
{
    [XmlElement("HTTP", Namespace = Wfs20Utilities.OwsNamespace)]
    public required Http Http { get; set; }
}

/// <summary>
/// HTTP methods and URLs
/// </summary>
public sealed class Http
{
    [XmlElement("Get", Namespace = Wfs20Utilities.OwsNamespace)]
    public HttpMethod[]? Get { get; set; }

    [XmlElement("Post", Namespace = Wfs20Utilities.OwsNamespace)]
    public HttpMethod[]? Post { get; set; }
}

/// <summary>
/// HTTP method definition
/// </summary>
public sealed class HttpMethod
{
    [XmlAttribute("href", Namespace = Wfs20Utilities.XLinkNamespace)]
    public required string Href { get; set; }
}

/// <summary>
/// Parameter definition
/// </summary>
public sealed class Parameter
{
    [XmlAttribute("name")]
    public required string Name { get; set; }

    [XmlElement("AllowedValues", Namespace = Wfs20Utilities.OwsNamespace)]
    public AllowedValues? AllowedValues { get; set; }

    [XmlElement("AnyValue", Namespace = Wfs20Utilities.OwsNamespace)]
    public object? AnyValue { get; set; }

    [XmlElement("NoValues", Namespace = Wfs20Utilities.OwsNamespace)]
    public object? NoValues { get; set; }
}

/// <summary>
/// Allowed parameter values
/// </summary>
public sealed class AllowedValues
{
    [XmlElement("Value", Namespace = Wfs20Utilities.OwsNamespace)]
    public required string[] Values { get; set; }
}

/// <summary>
/// Constraint definition
/// </summary>
public sealed class Constraint
{
    [XmlAttribute("name")]
    public required string Name { get; set; }

    [XmlElement("AllowedValues", Namespace = Wfs20Utilities.OwsNamespace)]
    public AllowedValues? AllowedValues { get; set; }

    [XmlElement("AnyValue", Namespace = Wfs20Utilities.OwsNamespace)]
    public object? AnyValue { get; set; }

    [XmlElement("NoValues", Namespace = Wfs20Utilities.OwsNamespace)]
    public object? NoValues { get; set; }

    [XmlElement("DefaultValue", Namespace = Wfs20Utilities.OwsNamespace)]
    public string? DefaultValue { get; set; }
}

/// <summary>
/// Feature type list
/// </summary>
public sealed class FeatureTypeList
{
    [XmlElement("FeatureType")]
    public required FeatureType[] FeatureTypes { get; set; }
}

/// <summary>
/// Feature type description
/// </summary>
public sealed class FeatureType
{
    [XmlElement("Name")]
    public required string Name { get; set; }

    [XmlElement("Title")]
    public required string Title { get; set; }

    [XmlElement("Abstract")]
    public string? Abstract { get; set; }

    [XmlArray("Keywords", Namespace = Wfs20Utilities.OwsNamespace)]
    [XmlArrayItem("Keyword", Namespace = Wfs20Utilities.OwsNamespace)]
    public string[]? Keywords { get; set; }

    [XmlElement("DefaultCRS")]
    public string DefaultCRS { get; set; } = Wfs20Utilities.DefaultSrs;

    [XmlElement("OtherCRS")]
    public string[]? OtherCRS { get; set; }

    [XmlElement("OutputFormats")]
    public OutputFormats? OutputFormats { get; set; }

    [XmlElement("WGS84BoundingBox", Namespace = Wfs20Utilities.OwsNamespace)]
    public WGS84BoundingBox? WGS84BoundingBox { get; set; }

    [XmlElement("MetadataURL")]
    public MetadataURL[]? MetadataURLs { get; set; }
}

/// <summary>
/// Output formats for a feature type
/// </summary>
public sealed class OutputFormats
{
    [XmlElement("Format")]
    public required string[] Formats { get; set; }
}

/// <summary>
/// WGS84 bounding box
/// </summary>
public sealed class WGS84BoundingBox
{
    [XmlAttribute("crs")]
    public string Crs { get; set; } = "urn:ogc:def:crs:OGC:2:84";

    [XmlElement("LowerCorner", Namespace = Wfs20Utilities.OwsNamespace)]
    public required string LowerCorner { get; set; }

    [XmlElement("UpperCorner", Namespace = Wfs20Utilities.OwsNamespace)]
    public required string UpperCorner { get; set; }
}

/// <summary>
/// Metadata URL reference
/// </summary>
public sealed class MetadataURL
{
    [XmlAttribute("type")]
    public string? Type { get; set; }

    [XmlAttribute("format")]
    public string? Format { get; set; }

    [XmlText]
    public required string Href { get; set; }
}

/// <summary>
/// Filter capabilities
/// </summary>
[XmlRoot("Filter_Capabilities", Namespace = Wfs20Utilities.FesNamespace)]
public sealed class FilterCapabilities
{
    [XmlElement("Conformance", Namespace = Wfs20Utilities.FesNamespace)]
    public required FesConformance Conformance { get; set; }

    [XmlElement("Id_Capabilities", Namespace = Wfs20Utilities.FesNamespace)]
    public IdCapabilities? IdCapabilities { get; set; }

    [XmlElement("Scalar_Capabilities", Namespace = Wfs20Utilities.FesNamespace)]
    public ScalarCapabilities? ScalarCapabilities { get; set; }

    [XmlElement("Spatial_Capabilities", Namespace = Wfs20Utilities.FesNamespace)]
    public SpatialCapabilities? SpatialCapabilities { get; set; }

    [XmlElement("Temporal_Capabilities", Namespace = Wfs20Utilities.FesNamespace)]
    public TemporalCapabilities? TemporalCapabilities { get; set; }

    [XmlElement("Functions", Namespace = Wfs20Utilities.FesNamespace)]
    public FunctionList? Functions { get; set; }
}

/// <summary>
/// FES conformance declaration
/// </summary>
public sealed class FesConformance
{
    [XmlElement("Constraint", Namespace = Wfs20Utilities.FesNamespace)]
    public required FesConstraint[] Constraints { get; set; }
}

/// <summary>
/// FES constraint
/// </summary>
public sealed class FesConstraint
{
    [XmlAttribute("name")]
    public required string Name { get; set; }

    [XmlElement("AllowedValues", Namespace = Wfs20Utilities.OwsNamespace)]
    public AllowedValues? AllowedValues { get; set; }

    [XmlElement("AnyValue", Namespace = Wfs20Utilities.OwsNamespace)]
    public object? AnyValue { get; set; }

    [XmlElement("NoValues", Namespace = Wfs20Utilities.OwsNamespace)]
    public object? NoValues { get; set; }

    [XmlElement("DefaultValue", Namespace = Wfs20Utilities.OwsNamespace)]
    public string? DefaultValue { get; set; }
}

/// <summary>
/// ID-based filter capabilities
/// </summary>
public sealed class IdCapabilities
{
    [XmlElement("ResourceIdentifier", Namespace = Wfs20Utilities.FesNamespace)]
    public ResourceIdentifier[]? ResourceIdentifiers { get; set; }
}

/// <summary>
/// Resource identifier
/// </summary>
public sealed class ResourceIdentifier
{
    [XmlAttribute("name")]
    public required string Name { get; set; }
}

/// <summary>
/// Scalar filter capabilities
/// </summary>
public sealed class ScalarCapabilities
{
    [XmlElement("LogicalOperators", Namespace = Wfs20Utilities.FesNamespace)]
    public object? LogicalOperators { get; set; }

    [XmlElement("ComparisonOperators", Namespace = Wfs20Utilities.FesNamespace)]
    public ComparisonOperators? ComparisonOperators { get; set; }
}

/// <summary>
/// Comparison operators
/// </summary>
public sealed class ComparisonOperators
{
    [XmlElement("ComparisonOperator", Namespace = Wfs20Utilities.FesNamespace)]
    public required ComparisonOperator[] Operators { get; set; }
}

/// <summary>
/// Comparison operator
/// </summary>
public sealed class ComparisonOperator
{
    [XmlAttribute("name")]
    public required string Name { get; set; }
}

/// <summary>
/// Spatial filter capabilities
/// </summary>
public sealed class SpatialCapabilities
{
    [XmlElement("GeometryOperands", Namespace = Wfs20Utilities.FesNamespace)]
    public GeometryOperands? GeometryOperands { get; set; }

    [XmlElement("SpatialOperators", Namespace = Wfs20Utilities.FesNamespace)]
    public SpatialOperators? SpatialOperators { get; set; }
}

/// <summary>
/// Geometry operands
/// </summary>
public sealed class GeometryOperands
{
    [XmlElement("GeometryOperand", Namespace = Wfs20Utilities.FesNamespace)]
    public required GeometryOperand[] Operands { get; set; }
}

/// <summary>
/// Geometry operand
/// </summary>
public sealed class GeometryOperand
{
    [XmlAttribute("name")]
    public required XmlQualifiedName Name { get; set; }
}

/// <summary>
/// Spatial operators
/// </summary>
public sealed class SpatialOperators
{
    [XmlElement("SpatialOperator", Namespace = Wfs20Utilities.FesNamespace)]
    public required SpatialOperator[] Operators { get; set; }
}

/// <summary>
/// Spatial operator
/// </summary>
public sealed class SpatialOperator
{
    [XmlAttribute("name")]
    public required string Name { get; set; }
}

/// <summary>
/// Temporal filter capabilities
/// </summary>
public sealed class TemporalCapabilities
{
    [XmlElement("TemporalOperands", Namespace = Wfs20Utilities.FesNamespace)]
    public TemporalOperands? TemporalOperands { get; set; }

    [XmlElement("TemporalOperators", Namespace = Wfs20Utilities.FesNamespace)]
    public TemporalOperators? TemporalOperators { get; set; }
}

/// <summary>
/// Temporal operands
/// </summary>
public sealed class TemporalOperands
{
    [XmlElement("TemporalOperand", Namespace = Wfs20Utilities.FesNamespace)]
    public required TemporalOperand[] Operands { get; set; }
}

/// <summary>
/// Temporal operand
/// </summary>
public sealed class TemporalOperand
{
    [XmlAttribute("name")]
    public required XmlQualifiedName Name { get; set; }
}

/// <summary>
/// Temporal operators
/// </summary>
public sealed class TemporalOperators
{
    [XmlElement("TemporalOperator", Namespace = Wfs20Utilities.FesNamespace)]
    public required TemporalOperator[] Operators { get; set; }
}

/// <summary>
/// Temporal operator
/// </summary>
public sealed class TemporalOperator
{
    [XmlAttribute("name")]
    public required string Name { get; set; }
}

/// <summary>
/// Function list for filter capabilities
/// </summary>
public sealed class FunctionList
{
    [XmlElement("Function", Namespace = Wfs20Utilities.FesNamespace)]
    public required FunctionDefinition[] Functions { get; set; }
}

/// <summary>
/// Function definition
/// </summary>
public sealed class FunctionDefinition
{
    [XmlAttribute("name")]
    public required string Name { get; set; }

    [XmlElement("Returns", Namespace = Wfs20Utilities.FesNamespace)]
    public FunctionReturn? Returns { get; set; }

    [XmlElement("Arguments", Namespace = Wfs20Utilities.FesNamespace)]
    public FunctionArguments? Arguments { get; set; }
}

/// <summary>
/// Function return type
/// </summary>
public sealed class FunctionReturn
{
    [XmlText]
    public required string Type { get; set; }
}

/// <summary>
/// Function arguments
/// </summary>
public sealed class FunctionArguments
{
    [XmlElement("Argument", Namespace = Wfs20Utilities.FesNamespace)]
    public FunctionArgument[]? Arguments { get; set; }
}

/// <summary>
/// Function argument definition
/// </summary>
public sealed class FunctionArgument
{
    [XmlAttribute("name")]
    public required string Name { get; set; }

    [XmlAttribute("type")]
    public required string Type { get; set; }
}

/// <summary>
/// WFS 2.0 exception report
/// </summary>
[XmlRoot("ExceptionReport", Namespace = Wfs20Utilities.OwsNamespace)]
public sealed class ExceptionReport
{
    [XmlAttribute("version")]
    public string Version { get; set; } = Wfs20Utilities.Version;

    [XmlElement("Exception", Namespace = Wfs20Utilities.OwsNamespace)]
    public required ExceptionType[] Exceptions { get; set; }
}

/// <summary>
/// Exception information
/// </summary>
public sealed class ExceptionType
{
    [XmlAttribute("exceptionCode")]
    public required string ExceptionCode { get; set; }

    [XmlAttribute("locator")]
    public string? Locator { get; set; }

    [XmlText]
    public string? ExceptionText { get; set; }
}

/// <summary>
/// WFS 2.0 feature collection response
/// </summary>
[XmlRoot("FeatureCollection", Namespace = Wfs20Utilities.WfsNamespace)]
public sealed class WfsFeatures
{
    [XmlAttribute("numberMatched")]
    public string? NumberMatched { get; set; }

    [XmlAttribute("numberReturned")]
    public int NumberReturned { get; set; }

    [XmlAttribute("timeStamp")]
    public DateTime TimeStamp { get; set; } = DateTime.UtcNow;

    [XmlAttribute("schemaLocation", Namespace = Wfs20Utilities.XsiNamespace)]
    public string? SchemaLocation { get; set; }

    [XmlElement("boundedBy", Namespace = Wfs20Utilities.GmlNamespace)]
    public BoundedBy? BoundedBy { get; set; }

    [XmlElement("member")]
    public FeatureMember[]? Members { get; set; }
}

/// <summary>
/// Bounded by envelope
/// </summary>
public sealed class BoundedBy
{
    [XmlElement("Envelope", Namespace = Wfs20Utilities.GmlNamespace)]
    public Envelope? Envelope { get; set; }

    [XmlElement("Null", Namespace = Wfs20Utilities.GmlNamespace)]
    public string? Null { get; set; }
}

/// <summary>
/// GML envelope
/// </summary>
public sealed class Envelope
{
    [XmlAttribute("srsName")]
    public string? SrsName { get; set; }

    [XmlElement("lowerCorner", Namespace = Wfs20Utilities.GmlNamespace)]
    public required string LowerCorner { get; set; }

    [XmlElement("upperCorner", Namespace = Wfs20Utilities.GmlNamespace)]
    public required string UpperCorner { get; set; }
}

/// <summary>
/// Feature member wrapper
/// </summary>
public sealed class FeatureMember
{
    [XmlAnyElement]
    public required System.Xml.XmlElement Feature { get; set; }
}
