// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Wfs20.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Service responsible for WFS 2.0 capabilities operations.
/// Follows Single Responsibility Principle by handling only capabilities-related operations.
/// </summary>
internal sealed class Wfs20CapabilitiesService : IWfs20CapabilitiesService
{
    private readonly ILogger<Wfs20CapabilitiesService> _logger;
    private readonly ILayerCatalog _layerCatalog;
    private readonly ICrsRegistry _crsRegistry;

    public Wfs20CapabilitiesService(
        ILogger<Wfs20CapabilitiesService> logger,
        ILayerCatalog layerCatalog,
        ICrsRegistry crsRegistry)
    {
        _logger = logger;
        _layerCatalog = layerCatalog;
        _crsRegistry = crsRegistry;
    }

    public async Task<WfsCapabilities> GetCapabilitiesAsync(
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
                        FeatureTypes = await Task.WhenAll(
                            featureTypes.Select(featureType => BuildFeatureTypeAsync(featureType, cancellationToken)))
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

    private async Task<FeatureTypeDescriptor[]> GetPublishedFeatureTypesAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var authResult = await GetAuthorizedLayersAsync(context, cancellationToken);
        var authorizedLayers = authResult.AuthorizedLayers;

        return authorizedLayers
            .Where(layer => layer.GeometryType != GeometryType.None)
            .Select(layer => new FeatureTypeDescriptor(layer))
            .ToArray();
    }

    private async Task<LayerAuthorizationResult> GetAuthorizedLayersAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        // TODO: Implement proper layer authorization after refactoring is complete
        // For now, get all layers (temporary implementation for build compatibility)
        var layers = await _layerCatalog.ListLayersAsync(cancellationToken);
        return new LayerAuthorizationResult(layers);
    }

    private static bool ShouldIncludeCapabilitiesSection(
        IReadOnlySet<string>? requestedSections,
        string sectionName)
    {
        return requestedSections?.Contains(sectionName) != false;
    }

    private static OperationsMetadata BuildOperationsMetadata(string wfsUrl)
    {
        return new OperationsMetadata
        {
            Operations = new[]
            {
                BuildGetCapabilitiesOperation(wfsUrl),
                BuildDescribeFeatureTypeOperation(wfsUrl),
                BuildGetFeatureOperation(wfsUrl),
                BuildGetPropertyValueOperation(wfsUrl),
                BuildListStoredQueriesOperation(wfsUrl),
                BuildDescribeStoredQueriesOperation(wfsUrl),
                BuildTransactionOperation(wfsUrl)
            },
            Parameters = new[]
            {
                new Parameter
                {
                    Name = "version",
                    AllowedValues = new AllowedValues { Values = new[] { "2.0.0" } }
                },
                new Parameter
                {
                    Name = "AcceptVersions",
                    AllowedValues = new AllowedValues { Values = new[] { "2.0.0" } }
                }
            },
            Constraints = Array.Empty<Constraint>()
        };
    }

    private static Operation BuildGetCapabilitiesOperation(string wfsUrl)
    {
        return new Operation
        {
            Name = "GetCapabilities",
            DCP = new[]
            {
                new DCP
                {
                    Http = new Http
                    {
                        Get = new[] { new Models.HttpMethod { Href = $"{wfsUrl}?" } },
                        Post = new[] { new Models.HttpMethod { Href = wfsUrl } }
                    }
                }
            },
            Parameters = new[]
            {
                new Parameter
                {
                    Name = "AcceptVersions",
                    AllowedValues = new AllowedValues { Values = new[] { "2.0.0" } }
                },
                new Parameter
                {
                    Name = "Sections",
                    AllowedValues = new AllowedValues
                    {
                        Values = new[]
                        {
                            "ServiceIdentification", "ServiceProvider", "OperationsMetadata",
                            "FeatureTypeList", "Filter_Capabilities"
                        }
                    }
                }
            }
        };
    }

    // Additional helper methods would be implemented here following the same pattern
    // These are extracted from the original Wfs20Handler implementation

    private static Operation BuildDescribeFeatureTypeOperation(string wfsUrl) => throw new NotImplementedException();
    private static Operation BuildGetFeatureOperation(string wfsUrl) => throw new NotImplementedException();
    private static Operation BuildGetPropertyValueOperation(string wfsUrl) => throw new NotImplementedException();
    private static Operation BuildListStoredQueriesOperation(string wfsUrl) => throw new NotImplementedException();
    private static Operation BuildDescribeStoredQueriesOperation(string wfsUrl) => throw new NotImplementedException();
    private static Operation BuildTransactionOperation(string wfsUrl) => throw new NotImplementedException();

    private async Task<FeatureType> BuildFeatureTypeAsync(
        FeatureTypeDescriptor featureType,
        CancellationToken cancellationToken)
    {
        // Implementation would be extracted from original Wfs20Handler
        throw new NotImplementedException();
    }

    private static FilterCapabilities BuildFilterCapabilities()
    {
        // Implementation would be extracted from original Wfs20Handler
        throw new NotImplementedException();
    }
}

// Temporary type definitions for build compatibility
// TODO: Extract proper types from WFS20Handler refactoring
internal sealed record FeatureTypeDescriptor(LayerDefinition Layer)
{
    public string QualifiedName => Layer.Name;
    public string LocalName => Layer.Name;
}

internal sealed record LayerAuthorizationResult(LayerDefinition[] AuthorizedLayers);