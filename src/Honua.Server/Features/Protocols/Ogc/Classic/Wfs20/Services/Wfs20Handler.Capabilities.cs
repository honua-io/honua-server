// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.ServiceDefaults;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Core handler for WFS 2.0 operations backed by the shared catalog and feature stores.
/// </summary>
internal sealed partial class Wfs20Handler
{

    public async Task<WfsCapabilities> HandleGetCapabilitiesAsync(
        HttpContext context,
        string? acceptVersions,
        IReadOnlySet<string>? requestedSections,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.get_capabilities", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.GetCapabilities);

        Wfs20Log.GetCapabilitiesRequested(_logger);

        try
        {
            var featureTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var wfsUrl = $"{baseUrl}/wfs";

            var capabilities = new WfsCapabilities
            {
                UpdateSequence = Wfs20Utilities.CurrentUpdateSequence,
                ServiceIdentification = ShouldIncludeCapabilitiesSection(requestedSections, "ServiceIdentification")
                    ? new ServiceIdentification()
                    : null,
                ServiceProvider = ShouldIncludeCapabilitiesSection(requestedSections, "ServiceProvider")
                    ? new Models.ServiceProvider()
                    : null,
                OperationsMetadata = ShouldIncludeCapabilitiesSection(requestedSections, "OperationsMetadata")
                    ? BuildOperationsMetadata(wfsUrl)
                    : null,
                FeatureTypeList = ShouldIncludeCapabilitiesSection(requestedSections, "FeatureTypeList")
                    ? new FeatureTypeList
                    {
                        FeatureTypes = await BuildFeatureTypesAsync(featureTypes, cancellationToken).ConfigureAwait(false)
                    }
                    : null,
                FilterCapabilities = ShouldIncludeCapabilitiesSection(requestedSections, "Filter_Capabilities")
                    ? BuildFilterCapabilities()
                    : null
            };

            Wfs20Log.GetCapabilitiesReturned(_logger);
            return capabilities;
        }
        catch (Exception ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, "true");
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            throw;
        }
    }


    public async Task<string> HandleDescribeFeatureTypeAsync(
        HttpContext context,
        string? typeNames,
        string? outputFormat,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.describe_feature_type", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.DescribeFeatureType);
        activity?.SetTag("wfs.type_names", typeNames ?? "ALL");

        Wfs20Log.DescribeFeatureTypeRequested(_logger, typeNames ?? "ALL");

        try
        {
            var requestedTypes = Wfs20Utilities.ParseTypeNames(typeNames);
            var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var selectedTypes = ResolveRequestedFeatureTypes(publishedTypes, requestedTypes);
            if (selectedTypes.Length == 0 && requestedTypes.Length > 0)
            {
                throw new ArgumentException($"Requested feature type(s) not found: {string.Join(", ", requestedTypes)}.");
            }

            var schema = GenerateSchemaForTypes(selectedTypes);

            Wfs20Log.DescribeFeatureTypeReturned(_logger, selectedTypes.Length == 0 ? requestedTypes.Length : selectedTypes.Length);
            return schema;
        }
        catch (Exception ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, "true");
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            throw;
        }
    }


    private async Task<ImmutableArray<WfsFeatureTypeDescriptor>> GetPublishedFeatureTypesAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var layers = await _layerCatalog.ListLayersAsync(cancellationToken);
        var services = await _layerCatalog.ListServicesAsync(cancellationToken);
        var visibleLayers = layers
            .Where(layer => IsPublishedForWfs(context, layer, services))
            .OrderBy(layer => layer.Id)
            .ToArray();

        var descriptors = ImmutableArray.CreateBuilder<WfsFeatureTypeDescriptor>(visibleLayers.Length);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in visibleLayers)
        {
            var baseName = IsWfs10CiteLayerName(layer.Name) ? layer.Name : BuildTypeLocalName(layer);
            var localName = baseName;
            if (!usedNames.Add(localName))
            {
                localName = $"{baseName}_{layer.Id}";
                usedNames.Add(localName);
            }

            descriptors.Add(new WfsFeatureTypeDescriptor(
                layer,
                $"{GetFeatureNamespacePrefix(layer)}:{localName}",
                localName,
                GetFeatureNamespacePrefix(layer),
                GetFeatureNamespaceUri(layer)));
        }

        return descriptors.ToImmutable();
    }


    private static string GetFeatureNamespacePrefix(LayerDefinition layer)
        => layer.Name switch
        {
            "Other" or "Fifteen" or "Seven" or "Nulls" or "Locks" => "cdf",
            "Points" or "Lines" or "Polygons" or "MPoints" or "MLines" or "MPolygons" => "cgf",
            _ => FeatureNamespacePrefix
        };


    private static string GetFeatureNamespaceUri(LayerDefinition layer)
        => layer.Name switch
        {
            "Other" or "Fifteen" or "Seven" or "Nulls" or "Locks" => "http://www.opengis.net/cite/data",
            "Points" or "Lines" or "Polygons" or "MPoints" or "MLines" or "MPolygons" => "http://www.opengis.net/cite/geometry",
            _ => FeatureNamespaceUri
        };


    private static bool IsWfs10CiteLayerName(string layerName)
        => layerName is "Other" or "Fifteen" or "Seven" or "Nulls" or "Locks" or
            "Points" or "Lines" or "Polygons" or "MPoints" or "MLines" or "MPolygons";


    private static ImmutableArray<WfsFeatureTypeDescriptor> ResolveRequestedFeatureTypes(
        ImmutableArray<WfsFeatureTypeDescriptor> publishedTypes,
        string[] requestedTypes)
    {
        if (requestedTypes.Length == 0)
        {
            return publishedTypes;
        }

        var matches = ImmutableArray.CreateBuilder<WfsFeatureTypeDescriptor>();
        var seenLayerIds = new HashSet<int>();

        foreach (var requestedType in requestedTypes)
        {
            foreach (var featureType in publishedTypes)
            {
                if (!MatchesRequestedType(featureType, requestedType) ||
                    !seenLayerIds.Add(featureType.Layer.Id))
                {
                    continue;
                }

                matches.Add(featureType);
            }
        }

        return matches.ToImmutable();
    }


    private static string[] GetUnknownRequestedFeatureTypes(
        ImmutableArray<WfsFeatureTypeDescriptor> publishedTypes,
        string[] requestedTypes)
    {
        if (requestedTypes.Length == 0)
        {
            return [];
        }

        return requestedTypes
            .Where(requestedType => !publishedTypes.Any(featureType => MatchesRequestedType(featureType, requestedType)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }


    private static bool MatchesRequestedType(WfsFeatureTypeDescriptor featureType, string requestedType)
    {
        var normalizedRequested = FilterExpressionHelpers.NormalizeIdentifier(requestedType);
        return string.Equals(requestedType, featureType.QualifiedName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedRequested, featureType.LocalName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedRequested, featureType.Layer.Name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedRequested, featureType.Layer.Id.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }


    private static bool IsPublishedForWfs(HttpContext context, LayerDefinition layer, ServiceDefinition[] services)
    {
        var relatedServices = services
            .Where(service => service.Layers.Any(candidate => candidate.Id == layer.Id))
            .ToArray();

        if (relatedServices.Length == 0)
        {
            return ServiceProtocols.IsProtocolEnabled(layer.Metadata, ServiceProtocols.Wfs20) &&
                AccessPolicyHelpers.IsLayerAccessible(context, layer);
        }

        return relatedServices
            .Where(service => ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.Wfs20))
            .Any(service => AccessPolicyHelpers.IsLayerAccessible(context, layer, service));
    }


    private async Task<FeatureType> BuildFeatureTypeAsync(
        WfsFeatureTypeDescriptor featureType,
        CancellationToken cancellationToken)
    {
        var layer = featureType.Layer;
        string[]? otherCrs = layer.SpatialReference.ToSrid() == 3857
            ? null
            : [FormatCrs(3857)];

        return new FeatureType
        {
            Name = featureType.QualifiedName,
            Title = layer.Name,
            Abstract = layer.Description,
            Keywords = BuildKeywords(layer),
            DefaultCRS = FormatCrs(layer.SpatialReference.ToSrid()),
            OtherCRS = otherCrs,
            OutputFormats = new OutputFormats
            {
                Formats = Wfs20Utilities.OutputFormats.All.ToArray()
            },
            WGS84BoundingBox = await BuildWgs84BoundingBoxAsync(layer, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<FeatureType[]> BuildFeatureTypesAsync(
        IReadOnlyList<WfsFeatureTypeDescriptor> featureTypes,
        CancellationToken cancellationToken)
    {
        const int MaxConcurrentExtentTransforms = 8;

        using var throttler = new SemaphoreSlim(MaxConcurrentExtentTransforms);
        var tasks = featureTypes.Select(async featureType =>
        {
            await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await BuildFeatureTypeAsync(featureType, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                throttler.Release();
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }


    private static string[] BuildKeywords(LayerDefinition layer)
    {
        var keywords = new List<string>
        {
            "wfs",
            "feature"
        };

        if (!string.IsNullOrWhiteSpace(layer.Name))
        {
            keywords.AddRange(layer.Name
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => word.ToLowerInvariant()));
        }

        if (layer.HasGeometry)
        {
            keywords.Add(layer.GeometryType.ToString().ToLowerInvariant());
        }

        return keywords
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }


    private async Task<WGS84BoundingBox?> BuildWgs84BoundingBoxAsync(
        LayerDefinition layer,
        CancellationToken cancellationToken)
    {
        if (layer.Extent == null)
        {
            return null;
        }

        var extent = layer.Extent.Value;
        var transformedExtent = await OgcExtentTransformer
            .TryTransformExtentToCrs84Async(
                extent.MinX,
                extent.MinY,
                extent.MaxX,
                extent.MaxY,
                extent.SpatialReference,
                _coordinateTransformService,
                cancellationToken)
            .ConfigureAwait(false);
        if (transformedExtent is null)
        {
            return null;
        }

        return new WGS84BoundingBox
        {
            LowerCorner = $"{FormatCoordinate(transformedExtent.Value.MinLon)} {FormatCoordinate(transformedExtent.Value.MinLat)}",
            UpperCorner = $"{FormatCoordinate(transformedExtent.Value.MaxLon)} {FormatCoordinate(transformedExtent.Value.MaxLat)}"
        };
    }


    private static string FormatCoordinate(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);

    private static string FormatCrs(int srid) => $"urn:ogc:def:crs:EPSG::{srid}";


    private static OperationsMetadata BuildOperationsMetadata(string wfsUrl)
    {
        return new OperationsMetadata
        {
            Operations =
            [
                CreateOperation(Wfs20Utilities.Operations.GetCapabilities, wfsUrl,
                [
                    CreateParameter("AcceptVersions", Wfs20Utilities.Version),
                    CreateParameter("Sections", "ServiceIdentification", "ServiceProvider", "OperationsMetadata", "FeatureTypeList", "Filter_Capabilities"),
                    CreateParameter("AcceptFormats", "application/xml")
                ]),
                CreateOperation(Wfs20Utilities.Operations.DescribeFeatureType, wfsUrl,
                [
                    CreateParameter("outputFormat", "application/xml"),
                    CreateParameter("typeNames", allowAnyValue: true)
                ]),
                CreateOperation(Wfs20Utilities.Operations.GetFeature, wfsUrl,
                [
                    CreateParameter("outputFormat", Wfs20Utilities.OutputFormats.All.ToArray()),
                    CreateParameter("typeNames", allowAnyValue: true),
                    CreateParameter("count", allowAnyValue: true),
                    CreateParameter("startIndex", allowAnyValue: true),
                    CreateParameter("sortBy", allowAnyValue: true),
                    CreateParameter("filter", allowAnyValue: true),
                    CreateParameter("bbox", allowAnyValue: true),
                    CreateParameter("resourceId", allowAnyValue: true),
                    CreateParameter("propertyName", allowAnyValue: true),
                    CreateParameter("srsName", allowAnyValue: true),
                    CreateParameter("resolve", "none", "local")
                ]),
                CreateOperation(Wfs20Utilities.Operations.GetPropertyValue, wfsUrl,
                [
                    CreateParameter(
                        "outputFormat",
                        Wfs20Utilities.OutputFormats.Gml32,
                        Wfs20Utilities.OutputFormats.GeoJson,
                        Wfs20Utilities.OutputFormats.Json),
                    CreateParameter("typeNames", allowAnyValue: true),
                    CreateParameter("valueReference", allowAnyValue: true),
                    CreateParameter("count", allowAnyValue: true),
                    CreateParameter("startIndex", allowAnyValue: true),
                    CreateParameter("filter", allowAnyValue: true),
                    CreateParameter("bbox", allowAnyValue: true),
                    CreateParameter("resourceId", allowAnyValue: true),
                    CreateParameter("srsName", allowAnyValue: true),
                    CreateParameter("resolve", "none", "local")
                ]),
                CreateOperation(Wfs20Utilities.Operations.Transaction, wfsUrl,
                [
                    CreateParameter("typeName", allowAnyValue: true),
                    CreateParameter("typeNames", allowAnyValue: true)
                ]),
                CreateOperation(Wfs20Utilities.Operations.ListStoredQueries, wfsUrl),
                CreateOperation(Wfs20Utilities.Operations.DescribeStoredQueries, wfsUrl,
                [
                    CreateParameter("storedquery_id", allowAnyValue: true)
                ])
            ],
            Parameters =
            [
                CreateParameter("version", Wfs20Utilities.Version),
                CreateParameter("service", Wfs20Utilities.ServiceType)
            ],
            Constraints =
            [
                CreateBooleanConstraint("ImplementsBasicWFS", true),
                CreateBooleanConstraint("ImplementsTransactionalWFS", true),
                CreateBooleanConstraint("ImplementsLockingWFS", false),
                CreateBooleanConstraint("ImplementsInheritance", false),
                CreateBooleanConstraint("ImplementsRemoteResolve", false),
                CreateBooleanConstraint("ImplementsResultPaging", true),
                CreateBooleanConstraint("ImplementsStandardJoins", false),
                CreateBooleanConstraint("ImplementsSpatialJoins", false),
                CreateBooleanConstraint("ImplementsTemporalJoins", false),
                CreateBooleanConstraint("ImplementsFeatureVersioning", false),
                CreateBooleanConstraint("ManageStoredQueries", false),
                CreateBooleanConstraint("KVPEncoding", true),
                CreateBooleanConstraint("XMLEncoding", true),
                CreateBooleanConstraint("SOAPEncoding", false),
                CreateOpenConstraint("DefaultMaxFeatures", Wfs20Utilities.DefaultMaxFeatures.ToString(CultureInfo.InvariantCulture)),
                CreateOpenConstraint("CountDefault", Wfs20Utilities.DefaultMaxFeatures.ToString(CultureInfo.InvariantCulture))
            ]
        };
    }


    private static Operation CreateOperation(string name, string url, Parameter[]? parameters = null)
    {
        return new Operation
        {
            Name = name,
            DCP =
            [
                new DCP
                {
                    Http = new Http
                    {
                        Get = [new Models.HttpMethod { Href = url }],
                        Post = [new Models.HttpMethod { Href = url }]
                    }
                }
            ],
            Parameters = parameters
        };
    }


    private static Parameter CreateParameter(string name, params string[] allowedValues)
    {
        return new Parameter
        {
            Name = name,
            AllowedValues = allowedValues.Length > 0 ? new AllowedValues { Values = allowedValues } : null,
            AnyValue = allowedValues.Length == 0 ? new object() : null
        };
    }


    private static Constraint CreateBooleanConstraint(string name, bool defaultValue)
    {
        return new Constraint
        {
            Name = name,
            AllowedValues = new AllowedValues
            {
                Values = defaultValue ? ["TRUE", "FALSE"] : ["FALSE"]
            },
            DefaultValue = defaultValue ? "TRUE" : "FALSE"
        };
    }


    private static Constraint CreateOpenConstraint(string name, string defaultValue)
    {
        return new Constraint
        {
            Name = name,
            AnyValue = new object(),
            DefaultValue = defaultValue
        };
    }


    private static FilterCapabilities BuildFilterCapabilities()
    {
        return new FilterCapabilities
        {
            Conformance = new FesConformance
            {
                Constraints =
                [
                    // Core query capabilities
                    CreateBooleanFesConstraint("ImplementsQuery", true),
                    CreateBooleanFesConstraint("ImplementsAdHocQuery", true),
                    CreateBooleanFesConstraint("ImplementsResourceId", true),

                    // Filter encoding capabilities per OGC Filter Encoding 2.0
                    CreateBooleanFesConstraint("ImplementsMinStandardFilter", true),
                    CreateBooleanFesConstraint("ImplementsStandardFilter", true),
                    CreateBooleanFesConstraint("ImplementsMinimumXPath", true),
                    CreateBooleanFesConstraint("ImplementsSchemaElementFunc", false),

                    // Spatial filter capabilities
                    CreateBooleanFesConstraint("ImplementsMinSpatialFilter", true),
                    CreateBooleanFesConstraint("ImplementsSpatialFilter", true),
                    CreateBooleanFesConstraint("ImplementsBBOX", true),
                    CreateBooleanFesConstraint("ImplementsDistanceBuffer", true),

                    // Runtime temporal predicates are still available, but the WFS 2.0
                    // capabilities document must not advertise FES temporal conformance
                    // until the instant/period matrix is CITE-stable.
                    CreateBooleanFesConstraint("ImplementsMinTemporalFilter", false),
                    CreateBooleanFesConstraint("ImplementsTemporalFilter", false),
                    CreateBooleanFesConstraint("ImplementsTemporalInstant", false),
                    CreateBooleanFesConstraint("ImplementsTemporalPeriod", false),

                    // Additional capabilities
                    CreateBooleanFesConstraint("ImplementsVersionNav", false),
                    CreateBooleanFesConstraint("ImplementsSorting", true),
                    CreateBooleanFesConstraint("ImplementsExtendedOperators", true),
                    CreateBooleanFesConstraint("ImplementsFunctions", false),
                    CreateBooleanFesConstraint("ImplementsArithmeticOperators", false),
                    CreateBooleanFesConstraint("ImplementsLogicalOperators", true),
                    CreateBooleanFesConstraint("ImplementsComparisonOperators", true),

                    // CQL2 support
                    CreateBooleanFesConstraint("ImplementsCQL2Text", false),
                    CreateBooleanFesConstraint("ImplementsCQL2JSON", false),
                    CreateBooleanFesConstraint("ImplementsCQL2BasicCQL", false),
                    CreateBooleanFesConstraint("ImplementsCQL2AdvancedComparison", false),
                    CreateBooleanFesConstraint("ImplementsCQL2BasicSpatial", false),
                    CreateBooleanFesConstraint("ImplementsCQL2SpatialOperators", false),
                    CreateBooleanFesConstraint("ImplementsCQL2TemporalOperators", false),
                    CreateBooleanFesConstraint("ImplementsCQL2ArrayOperators", false),
                    CreateBooleanFesConstraint("ImplementsCQL2Functions", false)
                ]
            },
            IdCapabilities = new IdCapabilities
            {
                ResourceIdentifiers =
                [
                    new ResourceIdentifier { Name = "id" },
                    new ResourceIdentifier { Name = "objectid" },
                    new ResourceIdentifier { Name = "fid" },
                    new ResourceIdentifier { Name = "gml:id" }
                ]
            },
            ScalarCapabilities = new ScalarCapabilities
            {
                LogicalOperators = new object(),
                ComparisonOperators = new ComparisonOperators
                {
                    Operators =
                    [
                        // Basic comparison operators
                        new ComparisonOperator { Name = "PropertyIsEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsNotEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsLessThan" },
                        new ComparisonOperator { Name = "PropertyIsGreaterThan" },
                        new ComparisonOperator { Name = "PropertyIsLessThanOrEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsGreaterThanOrEqualTo" },

                        // Pattern matching operators
                        new ComparisonOperator { Name = "PropertyIsLike" },

                        // Null checks
                        new ComparisonOperator { Name = "PropertyIsNil" },
                        new ComparisonOperator { Name = "PropertyIsNull" },

                        // Range operators
                        new ComparisonOperator { Name = "PropertyIsBetween" },

                        // Runtime supports the FES XML operators listed above.
                    ]
                }
            },
            SpatialCapabilities = new SpatialCapabilities
            {
                GeometryOperands = new GeometryOperands
                {
                    Operands =
                    [
                        // Basic geometry types
                        new GeometryOperand { Name = new XmlQualifiedName("Envelope", Wfs20Utilities.GmlNamespace) },
                        new GeometryOperand { Name = new XmlQualifiedName("Point", Wfs20Utilities.GmlNamespace) },
                        new GeometryOperand { Name = new XmlQualifiedName("LineString", Wfs20Utilities.GmlNamespace) },
                        new GeometryOperand { Name = new XmlQualifiedName("Curve", Wfs20Utilities.GmlNamespace) },
                        new GeometryOperand { Name = new XmlQualifiedName("Polygon", Wfs20Utilities.GmlNamespace) },
                        new GeometryOperand { Name = new XmlQualifiedName("Surface", Wfs20Utilities.GmlNamespace) }
                    ]
                },
                SpatialOperators = new SpatialOperators
                {
                    Operators =
                    [
                        // Topological operators (DE-9IM based)
                        new Models.SpatialOperator { Name = "BBOX" },
                        new Models.SpatialOperator { Name = "Intersects" },
                        new Models.SpatialOperator { Name = "Contains" },
                        new Models.SpatialOperator { Name = "Within" },
                        new Models.SpatialOperator { Name = "Crosses" },
                        new Models.SpatialOperator { Name = "Touches" },
                        new Models.SpatialOperator { Name = "Overlaps" },
                        new Models.SpatialOperator { Name = "Disjoint" },
                        new Models.SpatialOperator { Name = "Equals" },

                        // Distance operators
                        new Models.SpatialOperator { Name = "DWithin" },
                        new Models.SpatialOperator { Name = "Beyond" }
                    ]
                }
            }
        };
    }

    private static FesConstraint CreateBooleanFesConstraint(string name, bool defaultValue)
    {
        return new FesConstraint
        {
            Name = name,
            AllowedValues = new AllowedValues
            {
                Values = defaultValue ? ["TRUE", "FALSE"] : ["FALSE"]
            },
            DefaultValue = defaultValue ? "TRUE" : "FALSE"
        };
    }


    private static bool ShouldIncludeCapabilitiesSection(
        IReadOnlySet<string>? requestedSections,
        string sectionName)
    {
        return requestedSections is null ||
               requestedSections.Count == 0 ||
               requestedSections.Contains(sectionName);
    }


    private static string GenerateSchemaForTypes(IReadOnlyList<WfsFeatureTypeDescriptor> featureTypes)
    {
        if (featureTypes.Count == 0)
        {
            return GenerateEmptySchema();
        }

        var schema = new StringBuilder();
        schema.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        schema.AppendLine($"""<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:gml="{Wfs20Utilities.GmlNamespace}" xmlns:{FeatureNamespacePrefix}="{FeatureNamespaceUri}" targetNamespace="{FeatureNamespaceUri}" elementFormDefault="qualified" version="1.0.0">""");
        schema.AppendLine($"""  <xsd:import namespace="{Wfs20Utilities.GmlNamespace}" schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd"/>""");
        schema.AppendLine();

        foreach (var featureType in featureTypes)
        {
            var typeName = XmlConvert.EncodeLocalName(featureType.LocalName);
            schema.AppendLine($"""  <xsd:element name="{typeName}" type="{FeatureNamespacePrefix}:{typeName}Type" substitutionGroup="gml:AbstractFeature"/>""");
            schema.AppendLine($"""  <xsd:complexType name="{typeName}Type">""");
            schema.AppendLine("""    <xsd:complexContent>""");
            schema.AppendLine("""      <xsd:extension base="gml:AbstractFeatureType">""");
            schema.AppendLine("""        <xsd:sequence>""");

            if (featureType.Layer.GeometryField is not null)
            {
                var geometryFieldName = XmlConvert.EncodeLocalName(featureType.Layer.GeometryField.Name);
                schema.AppendLine($"""          <xsd:element name="{geometryFieldName}" type="{MapGeometryPropertyType(featureType.Layer.GeometryType)}" minOccurs="0" nillable="true"/>""");
            }

            foreach (var field in featureType.Layer.AttributeFields)
            {
                var fieldName = XmlConvert.EncodeLocalName(field.Name);
                var minOccurs = field.Nullable ? " minOccurs=\"0\"" : string.Empty;
                var nillable = field.Nullable ? " nillable=\"true\"" : string.Empty;
                schema.AppendLine($"""          <xsd:element name="{fieldName}" type="{MapXsdType(field.Type)}"{minOccurs}{nillable}/>""");
            }

            schema.AppendLine("""        </xsd:sequence>""");
            schema.AppendLine("""      </xsd:extension>""");
            schema.AppendLine("""    </xsd:complexContent>""");
            schema.AppendLine("""  </xsd:complexType>""");
            schema.AppendLine();
        }

        schema.AppendLine("""</xsd:schema>""");
        return schema.ToString();
    }


    private static string GenerateEmptySchema()
    {
        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xsd:schema
                xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                xmlns:gml="{{Wfs20Utilities.GmlNamespace}}"
                xmlns:{{FeatureNamespacePrefix}}="{{FeatureNamespaceUri}}"
                targetNamespace="{{FeatureNamespaceUri}}"
                elementFormDefault="qualified"
                version="1.0.0">
              <xsd:import namespace="{{Wfs20Utilities.GmlNamespace}}" schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd"/>
            </xsd:schema>
            """;
    }


    private static string GenerateEmptySchemaForTypes(IReadOnlyList<string> requestedTypes)
    {
        var requested = string.Join(", ", requestedTypes);
        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xsd:schema
                xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                xmlns:gml="{{Wfs20Utilities.GmlNamespace}}"
                xmlns:{{FeatureNamespacePrefix}}="{{FeatureNamespaceUri}}"
                targetNamespace="{{FeatureNamespaceUri}}"
                elementFormDefault="qualified"
                version="1.0.0">
              <xsd:import namespace="{{Wfs20Utilities.GmlNamespace}}" schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd"/>
              <xsd:annotation>
                <xsd:documentation>Requested feature types were not found: {{XmlEscape(requested)}}</xsd:documentation>
              </xsd:annotation>
            </xsd:schema>
            """;
    }
}
