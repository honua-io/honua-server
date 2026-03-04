// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Admin.Services;

/// <summary>
/// Mock implementation of layer catalog for form designer development.
/// In production, this would be replaced with the actual layer catalog service.
/// </summary>
internal sealed class MockLayerCatalog : ILayerCatalog
{
    public async Task<LayerDefinition?> GetLayerDefinitionAsync(string serviceId, int layerId, CancellationToken cancellationToken = default)
    {
        // Mock layer definition for demonstration
        if (serviceId == "sample_service" && layerId == 0)
        {
            return new LayerDefinition
            {
                Id = 0,
                Name = "Sample Layer",
                Description = "Example layer with common field types for OpenRosa form generation",
                GeometryType = GeometryType.Point,
                IsEditable = true,
                SpatialReference = new SpatialReference { Wkid = 4326 },
                AttributeFields = new[]
                {
                    new FieldDefinition
                    {
                        Name = "OBJECTID",
                        Type = FieldType.Integer,
                        Alias = "Object ID",
                        IsNullable = false
                    },
                    new FieldDefinition
                    {
                        Name = "NAME",
                        Type = FieldType.String,
                        Alias = "Feature Name",
                        IsNullable = false,
                        Length = 100
                    },
                    new FieldDefinition
                    {
                        Name = "DESCRIPTION",
                        Type = FieldType.String,
                        Alias = "Description",
                        IsNullable = true,
                        Length = 255
                    },
                    new FieldDefinition
                    {
                        Name = "STATUS",
                        Type = FieldType.String,
                        Alias = "Status",
                        IsNullable = true,
                        Length = 50
                    },
                    new FieldDefinition
                    {
                        Name = "PRIORITY",
                        Type = FieldType.Integer,
                        Alias = "Priority Level",
                        IsNullable = true
                    },
                    new FieldDefinition
                    {
                        Name = "CREATED_DATE",
                        Type = FieldType.Date,
                        Alias = "Created Date",
                        IsNullable = false
                    },
                    new FieldDefinition
                    {
                        Name = "INSPECTOR",
                        Type = FieldType.String,
                        Alias = "Inspector Name",
                        IsNullable = true,
                        Length = 100
                    },
                    new FieldDefinition
                    {
                        Name = "COST",
                        Type = FieldType.Double,
                        Alias = "Estimated Cost",
                        IsNullable = true
                    },
                    new FieldDefinition
                    {
                        Name = "PHOTO",
                        Type = FieldType.Blob,
                        Alias = "Photo",
                        IsNullable = true
                    },
                    new FieldDefinition
                    {
                        Name = "NOTES",
                        Type = FieldType.String,
                        Alias = "Additional Notes",
                        IsNullable = true,
                        Length = 1000
                    }
                }
            };
        }

        return null;
    }

    public async Task<IEnumerable<LayerDefinition>> GetLayersAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        if (serviceId == "sample_service")
        {
            var layer = await GetLayerDefinitionAsync(serviceId, 0, cancellationToken);
            return layer != null ? new[] { layer } : Array.Empty<LayerDefinition>();
        }

        return Array.Empty<LayerDefinition>();
    }

    // Other required interface methods (not used in form designer)
    public Task<IEnumerable<LayerDefinition>> GetLayersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Enumerable.Empty<LayerDefinition>());

    public Task InvalidateCacheAsync(string serviceId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task InvalidateCacheAsync(string serviceId, int layerId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Mock implementation of service catalog for form designer development.
/// In production, this would be replaced with the actual service catalog.
/// </summary>
internal sealed class MockServiceCatalog : IServiceCatalog
{
    public async Task<ServiceDefinition?> GetServiceDefinitionAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        if (serviceId == "sample_service")
        {
            return new ServiceDefinition
            {
                Id = "sample_service",
                Name = "Sample GIS Service",
                Description = "Example service for OpenRosa form generation",
                IsEditable = true,
                SpatialReference = new SpatialReference { Wkid = 4326 }
            };
        }

        return null;
    }

    public async Task<IEnumerable<ServiceDefinition>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        return new[]
        {
            new ServiceDefinition
            {
                Id = "sample_service",
                Name = "Sample GIS Service",
                Description = "Example service for OpenRosa form generation"
            },
            new ServiceDefinition
            {
                Id = "infrastructure_service",
                Name = "Infrastructure Inspections",
                Description = "Infrastructure asset inspection and maintenance tracking"
            },
            new ServiceDefinition
            {
                Id = "environmental_service",
                Name = "Environmental Monitoring",
                Description = "Environmental data collection and monitoring"
            }
        };
    }

    // Other required interface methods (not used in form designer)
    public Task InvalidateCacheAsync(string serviceId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}