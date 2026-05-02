// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Http;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Metadata.Services;

/// <summary>
/// Unified metadata provider that consolidates metadata gathering from all sources.
/// Provides cached, consistent metadata for protocol-specific formatters.
/// </summary>
internal sealed class UnifiedMetadataProvider : IMetadataProvider
{
    private static readonly ActivitySource ActivitySource = new("Honua.Core.Metadata");
    private readonly ILogger<UnifiedMetadataProvider> _logger;
    private readonly ILayerCatalog _layerCatalog;
    private readonly IFeatureReader _featureReader;
    private readonly ICrsRegistry _crsRegistry;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly IOptions<LimitsOptions> _limitsOptions;

    private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExpensiveCacheDuration = TimeSpan.FromHours(1);

    public UnifiedMetadataProvider(
        ILogger<UnifiedMetadataProvider> logger,
        ILayerCatalog layerCatalog,
        IFeatureReader featureReader,
        ICrsRegistry crsRegistry,
        IMemoryCache cache,
        IConfiguration configuration,
        IOptions<LimitsOptions> limitsOptions)
    {
        _logger = logger;
        _layerCatalog = layerCatalog;
        _featureReader = featureReader;
        _crsRegistry = crsRegistry;
        _cache = cache;
        _configuration = configuration;
        _limitsOptions = limitsOptions;
    }

    public async Task<ServiceMetadata> GetServiceMetadataAsync(
        IRequestContext context,
        ServiceDefinition service,
        MetadataProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"service_metadata:{service.Name}:{GetOptionsHash(options)}";

        if (_cache.TryGetValue(cacheKey, out ServiceMetadata? cached))
        {
            return cached!;
        }

        using var activity = ActivitySource.StartActivity("UnifiedMetadataProvider.GetServiceMetadata");
        activity?.SetTag("service.name", service.Name);

        UnifiedMetadataProviderLog.GeneratingServiceMetadata(_logger, service.Name);

        try
        {
            var metadata = await GenerateServiceMetadataAsync(context, service, options, cancellationToken);

            var cacheDuration = options.IncludeExpensiveMetadata ? ExpensiveCacheDuration : DefaultCacheDuration;
            _cache.Set(cacheKey, metadata, cacheDuration);

            UnifiedMetadataProviderLog.GeneratedServiceMetadata(_logger, service.Name, metadata.Layers.Length);

            return metadata;
        }
        catch (Exception ex)
        {
            UnifiedMetadataProviderLog.GenerateServiceMetadataFailed(_logger, service.Name, ex);
            throw;
        }
    }

    public async Task<LayerMetadata> GetLayerMetadataAsync(
        IRequestContext context,
        ServiceDefinition service,
        LayerDefinition layer,
        MetadataProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"layer_metadata:{service.Name}:{layer.Id}:{GetOptionsHash(options)}";

        if (_cache.TryGetValue(cacheKey, out LayerMetadata? cached))
        {
            return cached!;
        }

        using var activity = ActivitySource.StartActivity("UnifiedMetadataProvider.GetLayerMetadata");
        activity?.SetTag("service.name", service.Name);
        activity?.SetTag("layer.id", layer.Id);
        activity?.SetTag("layer.name", layer.Name);

        UnifiedMetadataProviderLog.GeneratingLayerMetadata(_logger, service.Name, layer.Name);

        try
        {
            var metadata = await GenerateLayerMetadataAsync(context, service, layer, options, cancellationToken);

            var cacheDuration = options.IncludeExpensiveMetadata ? ExpensiveCacheDuration : DefaultCacheDuration;
            _cache.Set(cacheKey, metadata, cacheDuration);

            UnifiedMetadataProviderLog.GeneratedLayerMetadata(_logger, service.Name, layer.Name);
            return metadata;
        }
        catch (Exception ex)
        {
            UnifiedMetadataProviderLog.GenerateLayerMetadataFailed(_logger, service.Name, layer.Name, ex);
            throw;
        }
    }

    public async Task<GlobalCapabilities> GetGlobalCapabilitiesAsync(
        IRequestContext context,
        MetadataProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"global_capabilities:{GetOptionsHash(options)}";

        if (_cache.TryGetValue(cacheKey, out GlobalCapabilities? cached))
        {
            return cached!;
        }

        using var activity = ActivitySource.StartActivity("UnifiedMetadataProvider.GetGlobalCapabilities");

        UnifiedMetadataProviderLog.GeneratingGlobalCapabilities(_logger);

        try
        {
            var capabilities = await GenerateGlobalCapabilitiesAsync(context, options, cancellationToken);

            _cache.Set(cacheKey, capabilities, ExpensiveCacheDuration);

            UnifiedMetadataProviderLog.GeneratedGlobalCapabilities(_logger);
            return capabilities;
        }
        catch (Exception ex)
        {
            UnifiedMetadataProviderLog.GenerateGlobalCapabilitiesFailed(_logger, ex);
            throw;
        }
    }

    private async Task<ServiceMetadata> GenerateServiceMetadataAsync(
        IRequestContext context,
        ServiceDefinition service,
        MetadataProviderOptions options,
        CancellationToken cancellationToken)
    {
        // Generate layer metadata for all layers
        var layerMetadataTasks = service.Layers.Select(layer =>
            GenerateLayerMetadataAsync(context, service, layer, options, cancellationToken));
        var layerMetadata = await Task.WhenAll(layerMetadataTasks);

        // Build service identity
        var identity = new ServiceIdentity
        {
            Name = service.Name,
            Title = service.Description ?? service.Name,
            Description = service.Description ?? $"Geospatial service: {service.Name}",
            Keywords = ExtractKeywordsFromService(service),
            License = ExtractLicenseFromService(service),
            Contact = ExtractContactFromConfiguration(),
            Provider = ExtractProviderFromConfiguration()
        };

        // Build service capabilities
        var capabilities = BuildServiceCapabilities(service, layerMetadata);

        // Build spatial information
        var spatialInfo = BuildServiceSpatialInfo(service, layerMetadata, options.IncludeExpensiveMetadata);

        // Build temporal information
        var temporalInfo = BuildServiceTemporalInfo(service, layerMetadata);

        // Build access control information
        var accessControl = BuildServiceAccessControl(context, service, layerMetadata);

        // Build service links
        var links = BuildServiceLinks(options.BaseUrl, service, capabilities);

        return new ServiceMetadata
        {
            Definition = service,
            Identity = identity,
            Layers = layerMetadata,
            Capabilities = capabilities,
            SpatialInfo = spatialInfo,
            TemporalInfo = temporalInfo,
            AccessControl = accessControl,
            Links = links,
            GeneratedAt = DateTimeOffset.UtcNow,
            Version = GenerateServiceVersion(service)
        };
    }

    private async Task<LayerMetadata> GenerateLayerMetadataAsync(
        IRequestContext context,
        ServiceDefinition service,
        LayerDefinition layer,
        MetadataProviderOptions options,
        CancellationToken cancellationToken)
    {
        // Build layer identity
        var identity = new LayerIdentity
        {
            Id = layer.Id,
            Name = layer.Name,
            Title = layer.Description ?? layer.Name,
            Description = layer.Description ?? $"Layer: {layer.Name}",
            Keywords = ExtractKeywordsFromLayer(layer),
            LayerType = DetermineLayerType(layer),
            DefaultVisibility = layer.DefaultVisibility,
            MinScale = layer.MinScale,
            MaxScale = layer.MaxScale
        };

        // Build field information
        var fields = await BuildLayerFieldInfoAsync(layer, options, cancellationToken);

        // Build statistics (if requested)
        LayerStatistics? statistics = null;
        if (options.IncludeExpensiveMetadata)
        {
            statistics = await ComputeLayerStatisticsAsync(layer, options.ExpensiveMetadataTimeout, cancellationToken);
        }

        // Build spatial information
        var spatialInfo = BuildLayerSpatialInfo(layer, statistics);

        // Build temporal information
        var temporalInfo = BuildLayerTemporalInfo(layer);

        // Build extrusion information for 3D-capable feature layers
        var extrusionInfo = BuildLayerExtrusionInfo(layer);

        // Build style information
        LayerStyleInfo? styleInfo = null;
        if (options.IncludeDrawingInfo)
        {
            styleInfo = await BuildLayerStyleInfoAsync(layer, cancellationToken);
        }

        // Build relationship information
        var relationships = BuildLayerRelationships(layer, service, options);

        // Build layer capabilities
        var capabilities = BuildLayerCapabilities(layer, service);

        // Build access control
        var accessControl = BuildLayerAccessControl(context, layer, service);

        return new LayerMetadata
        {
            Definition = layer,
            Identity = identity,
            Fields = fields,
            Statistics = statistics,
            SpatialInfo = spatialInfo,
            TemporalInfo = temporalInfo,
            ExtrusionInfo = extrusionInfo,
            StyleInfo = styleInfo,
            Relationships = relationships,
            Capabilities = capabilities,
            AccessControl = accessControl,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<GlobalCapabilities> GenerateGlobalCapabilitiesAsync(
        IRequestContext context,
        MetadataProviderOptions options,
        CancellationToken cancellationToken)
    {
        var serverIdentity = new ServerIdentity
        {
            Name = _configuration.GetValue("Server:Name", "Honua Server"),
            Title = _configuration.GetValue("Server:Title", "Honua Geospatial Server"),
            Description = _configuration.GetValue("Server:Description", "Open source geospatial data server"),
            Version = _configuration.GetValue("Server:Version", "1.0.0"),
            Contact = ExtractContactFromConfiguration(),
            Provider = ExtractProviderFromConfiguration(),
            ServerUrl = options.BaseUrl,
            License = _configuration.GetValue("Server:License", "Elastic License 2.0"),
            Keywords = _configuration.GetSection("Server:Keywords").Get<string[]>() ?? Array.Empty<string>()
        };

        var protocols = await BuildProtocolCapabilitiesAsync(cancellationToken);
        var spatial = await BuildGlobalSpatialCapabilitiesAsync(cancellationToken);
        var formats = BuildGlobalFormatCapabilities();
        var query = BuildGlobalQueryCapabilities();
        var limits = BuildGlobalLimits();
        var security = BuildSecurityCapabilities();
        var performance = BuildPerformanceCapabilities();
        var extensions = BuildExtensionCapabilities();

        return new GlobalCapabilities
        {
            Server = serverIdentity,
            Protocols = protocols,
            Spatial = spatial,
            Formats = formats,
            Query = query,
            Limits = limits,
            Security = security,
            Performance = performance,
            Extensions = extensions,
            GeneratedAt = DateTimeOffset.UtcNow,
            Version = serverIdentity.Version
        };
    }

    private async Task<LayerFieldInfo[]> BuildLayerFieldInfoAsync(
        LayerDefinition layer,
        MetadataProviderOptions options,
        CancellationToken cancellationToken)
    {
        var fieldInfos = new List<LayerFieldInfo>();

        foreach (var field in layer.Fields)
        {
            FieldStatistics? statistics = null;
            object[]? uniqueValues = null;

            if (options.IncludeExpensiveMetadata && field.Type != FieldType.Geometry)
            {
                // Compute field statistics for non-geometry fields
                try
                {
                    using var timeoutCts = new CancellationTokenSource(options.ExpensiveMetadataTimeout);
                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, timeoutCts.Token);

                    statistics = await ComputeFieldStatisticsAsync(layer, field, combinedCts.Token);

                    // For categorical fields with few unique values, get the unique values
                    if (statistics?.DistinctCount <= 100 && field.Type == FieldType.String)
                    {
                        uniqueValues = await GetUniqueFieldValuesAsync(layer, field, 100, combinedCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    UnifiedMetadataProviderLog.FieldStatisticsTimedOut(_logger, layer.Name, field.Name);
                }
                catch (Exception ex)
                {
                    UnifiedMetadataProviderLog.FieldStatisticsFailed(_logger, layer.Name, field.Name, ex);
                }
            }

            fieldInfos.Add(new LayerFieldInfo
            {
                Definition = field,
                Statistics = statistics,
                UniqueValues = uniqueValues,
                IsIndexed = DetermineIfFieldIsIndexed(field),
                IsSearchable = DetermineIfFieldIsSearchable(field),
                Domain = ExtractFieldDomain(field),
                Validation = ExtractFieldValidation(field)
            });
        }

        return fieldInfos.ToArray();
    }

    // Helper methods for building specific metadata components
    private ServiceCapabilities BuildServiceCapabilities(ServiceDefinition service, LayerMetadata[] layers)
    {
        var queryCapabilities = new QueryCapabilities
        {
            SupportsAdvancedQueries = service.SupportsAdvancedQueries,
            SupportsStatistics = true,
            SupportsPagination = true,
            SupportsSorting = true,
            SupportsDistinct = true,
            SupportedSpatialRelations = GetSupportedSpatialRelations(),
            Filter = new FilterCapabilities
            {
                SupportsWhere = true,
                SupportsGeometry = layers.Any(l => l.SpatialInfo.HasGeometry),
                SupportsTemporal = layers.Any(l => l.TemporalInfo != null),
                SupportedOperators = GetSupportedFilterOperators(),
                SupportedFunctions = GetSupportedFilterFunctions()
            }
        };

        EditCapabilities? editCapabilities = null;
        if (service.SupportsEditing)
        {
            editCapabilities = new EditCapabilities
            {
                SupportsCreate = service.Capabilities.Contains("Create"),
                SupportsUpdate = service.Capabilities.Contains("Update"),
                SupportsDelete = service.Capabilities.Contains("Delete"),
                SupportsBatchOperations = true,
                SupportsTransactions = true,
                SupportsAttachments = layers.Any(l => l.Definition.SupportsAttachments)
            };
        }

        return new ServiceCapabilities
        {
            Query = queryCapabilities,
            Edit = editCapabilities,
            Formats = new FormatCapabilities
            {
                QueryFormats = service.SupportedFormats,
                InputFormats = editCapabilities != null ? ["JSON", "GeoJSON"] : Array.Empty<string>(),
                ImageFormats = ["PNG", "JPEG", "SVG"],
                SupportsGeometryPrecision = true
            },
            Spatial = new SpatialCapabilities
            {
                DefaultSrs = service.SpatialReference,
                SupportedSrs = GetSupportedSpatialReferences(service, layers),
                SupportsTransformation = true,
                SupportedGeometryTypes = service.GeometryTypes,
                Supports3D = layers.Any(l => l.SpatialInfo.Complexity?.HasZ == true),
                SupportsM = layers.Any(l => l.SpatialInfo.Complexity?.HasM == true)
            },
            Limits = new ServiceLimits
            {
                MaxRecordCount = _limitsOptions.Value.Query.MaxRecordCount,
                DefaultRecordCount = _limitsOptions.Value.Query.DefaultRecordCount,
                MaxTimeout = TimeSpan.FromSeconds(_limitsOptions.Value.MaxQueryTimeoutSeconds),
                MaxUploadSize = 50 * 1024 * 1024, // 50MB
                MaxGeometryComplexity = 10_000
            }
        };
    }

    // Additional helper methods would be implemented here to complete the metadata generation
    private static string[] GetSupportedSpatialRelations() =>
        ["intersects", "contains", "within", "crosses", "overlaps", "touches", "disjoint"];

    private static string[] GetSupportedFilterOperators() =>
        ["=", "<>", "<", "<=", ">", ">=", "LIKE", "IN", "IS NULL", "IS NOT NULL"];

    private static string[] GetSupportedFilterFunctions() =>
        ["UPPER", "LOWER", "LENGTH", "SUBSTRING", "NOW"];

    private static SpatialReference[] GetSupportedSpatialReferences(ServiceDefinition service, LayerMetadata[] layers)
    {
        var srsSet = new HashSet<SpatialReference> { service.SpatialReference };
        foreach (var layer in layers)
        {
            srsSet.Add(layer.SpatialInfo.SpatialReference);
        }
        return srsSet.ToArray();
    }

    // Placeholder methods - these would contain actual implementation
    private static async Task<LayerStatistics?> ComputeLayerStatisticsAsync(LayerDefinition layer, TimeSpan timeout, CancellationToken cancellationToken) => null;
    private static async Task<FieldStatistics?> ComputeFieldStatisticsAsync(LayerDefinition layer, FieldDefinition field, CancellationToken cancellationToken) => null;
    private static async Task<object[]?> GetUniqueFieldValuesAsync(LayerDefinition layer, FieldDefinition field, int limit, CancellationToken cancellationToken) => null;
    private static async Task<LayerStyleInfo?> BuildLayerStyleInfoAsync(LayerDefinition layer, CancellationToken cancellationToken) => null;
    private static async Task<ProtocolCapabilities> BuildProtocolCapabilitiesAsync(CancellationToken cancellationToken) => new();
    private static async Task<GlobalSpatialCapabilities> BuildGlobalSpatialCapabilitiesAsync(CancellationToken cancellationToken) => new() { DefaultCrs = SpatialReference.WGS84 };

    // Helper methods for extracting information
    private static string[] ExtractKeywordsFromService(ServiceDefinition service) => Array.Empty<string>();
    private static string[] ExtractKeywordsFromLayer(LayerDefinition layer) => Array.Empty<string>();
    private static string? ExtractLicenseFromService(ServiceDefinition service) => service.Metadata?.Stac?.License;
    private static ContactInfo? ExtractContactFromConfiguration() => null;
    private static ProviderInfo? ExtractProviderFromConfiguration() => null;
    private static string DetermineLayerType(LayerDefinition layer) => layer.HasGeometry ? "Feature Layer" : "Table";
    private static bool DetermineIfFieldIsIndexed(FieldDefinition field) => field.Name.Contains("id", StringComparison.OrdinalIgnoreCase);
    private static bool DetermineIfFieldIsSearchable(FieldDefinition field) => field.Type == FieldType.String;
    private static FieldDomain? ExtractFieldDomain(FieldDefinition field) => null;
    private static FieldValidation? ExtractFieldValidation(FieldDefinition field) => null;

    // Additional helper methods would be implemented here...
    private static LayerSpatialInfo BuildLayerSpatialInfo(LayerDefinition layer, LayerStatistics? statistics) => new()
    {
        GeometryType = layer.GeometryType,
        HasGeometry = layer.HasGeometry,
        SpatialReference = layer.SpatialReference,
        Extent = layer.Extent,
        ExtentIsComputed = false
    };

    private static LayerTemporalInfo? BuildLayerTemporalInfo(LayerDefinition layer) =>
        layer.Metadata?.TimeInfo != null ? new LayerTemporalInfo
        {
            StartTimeField = layer.Metadata.TimeInfo.StartTimeField,
            EndTimeField = layer.Metadata.TimeInfo.EndTimeField,
            TrackIdField = layer.Metadata.TimeInfo.TrackIdField
        } : null;

    private static LayerExtrusionMetadata? BuildLayerExtrusionInfo(LayerDefinition layer)
    {
        if (layer.Metadata?.Extrusion is not { } extrusion)
        {
            return null;
        }

        VerticalUnits.TryNormalize(extrusion.Unit, out var unitWire);

        return new LayerExtrusionMetadata
        {
            Enabled = true,
            HeightField = extrusion.HeightField,
            BaseHeightField = extrusion.BaseHeightField,
            Unit = unitWire,
            DefaultHeight = extrusion.DefaultHeight,
            MaterialHint = extrusion.MaterialHint
        };
    }

    private static LayerRelationshipInfo[] BuildLayerRelationships(LayerDefinition layer, ServiceDefinition service, MetadataProviderOptions options) =>
        layer.Relationships?.Select(r => new LayerRelationshipInfo
        {
            Definition = r,
            RelatedResource = new RelatedResourceInfo
            {
                Id = r.RelatedLayerId,
                Name = service.GetLayer(r.RelatedLayerId)?.Name ?? "Unknown",
                Type = "Layer",
                IsAvailable = service.GetLayer(r.RelatedLayerId) != null
            }
        }).ToArray() ?? Array.Empty<LayerRelationshipInfo>();

    private static LayerCapabilities BuildLayerCapabilities(LayerDefinition layer, ServiceDefinition service) => new();
    private static LayerAccessInfo BuildLayerAccessControl(IRequestContext context, LayerDefinition layer, ServiceDefinition service) => new();
    private static ServiceSpatialInfo BuildServiceSpatialInfo(ServiceDefinition service, LayerMetadata[] layers, bool includeExpensive) => new()
    {
        Extent = service.EffectiveExtent,
        ExtentSrs = service.SpatialReference
    };

    private static ServiceTemporalInfo? BuildServiceTemporalInfo(ServiceDefinition service, LayerMetadata[] layers) => null;
    private static AccessControlInfo BuildServiceAccessControl(IRequestContext context, ServiceDefinition service, LayerMetadata[] layers) => new();
    private static ServiceLinks BuildServiceLinks(string baseUrl, ServiceDefinition service, ServiceCapabilities capabilities) => new() { BaseUrl = baseUrl };

    private static GlobalFormatCapabilities BuildGlobalFormatCapabilities() => new();
    private static GlobalQueryCapabilities BuildGlobalQueryCapabilities() => new();
    private static GlobalLimits BuildGlobalLimits() => new();
    private static SecurityCapabilities BuildSecurityCapabilities() => new();
    private static PerformanceCapabilities BuildPerformanceCapabilities() => new();
    private static ExtensionCapabilities BuildExtensionCapabilities() => new();

    private static string GenerateServiceVersion(ServiceDefinition service) =>
        $"{service.Name}:{service.Layers.Length}:{DateTimeOffset.UtcNow:yyyyMMdd}";

    private static string GetOptionsHash(MetadataProviderOptions options) =>
        $"{options.IncludeFields}:{options.IncludeExtents}:{options.IncludeExpensiveMetadata}:{options.IncludeDrawingInfo}";
}
