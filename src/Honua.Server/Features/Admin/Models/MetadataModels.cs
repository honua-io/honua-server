// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Admin.Models;

// ============================================================================
// Service DTOs
// ============================================================================

/// <summary>
/// Request to create a new service
/// </summary>
public sealed record CreateServiceRequest
{
    /// <summary>
    /// Service name (URL segment identifier)
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Human-readable description
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Spatial reference SRID (default: 4326 for WGS84)
    /// </summary>
    public int SpatialReferenceSrid { get; init; } = 4326;

    /// <summary>
    /// Maximum records returned per query (default: 1000)
    /// </summary>
    public int MaxRecordCount { get; init; } = 1000;
}

/// <summary>
/// Request to update a service
/// </summary>
public sealed record UpdateServiceRequest
{
    /// <summary>
    /// New description (optional)
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// New max record count (optional)
    /// </summary>
    public int? MaxRecordCount { get; init; }
}

/// <summary>
/// Response for service operations
/// </summary>
public sealed record ServiceResponse
{
    /// <summary>
    /// Service name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Service description
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Spatial reference SRID
    /// </summary>
    public required int SpatialReferenceSrid { get; init; }

    /// <summary>
    /// Maximum records per query
    /// </summary>
    public required int MaxRecordCount { get; init; }

    /// <summary>
    /// Number of layers in service
    /// </summary>
    public required int LayerCount { get; init; }

    /// <summary>
    /// Layer IDs bound to this service
    /// </summary>
    public required int[] LayerIds { get; init; }
}

/// <summary>
/// Response listing all services
/// </summary>
public sealed record ServiceListResponse
{
    /// <summary>
    /// List of services
    /// </summary>
    public required ServiceResponse[] Services { get; init; }
}

// ============================================================================
// Layer DTOs
// ============================================================================

/// <summary>
/// Request to create a new layer from a database table
/// </summary>
public sealed record CreateLayerRequest
{
    /// <summary>
    /// Database table name
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Database schema name (default: public)
    /// </summary>
    public string SchemaName { get; init; } = "public";

    /// <summary>
    /// Display name for the layer
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Layer description
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Request to update a layer
/// </summary>
public sealed record UpdateLayerRequest
{
    /// <summary>
    /// New display name
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// New description
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Minimum visibility scale
    /// </summary>
    public double? MinScale { get; init; }

    /// <summary>
    /// Maximum visibility scale
    /// </summary>
    public double? MaxScale { get; init; }

    /// <summary>
    /// Default visibility
    /// </summary>
    public bool? DefaultVisibility { get; init; }
}

/// <summary>
/// Response for layer operations
/// </summary>
public sealed record LayerResponse
{
    /// <summary>
    /// Layer ID
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// Layer name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Layer description
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Geometry type
    /// </summary>
    public required string GeometryType { get; init; }

    /// <summary>
    /// Spatial reference SRID
    /// </summary>
    public required int SpatialReferenceSrid { get; init; }

    /// <summary>
    /// Number of fields
    /// </summary>
    public required int FieldCount { get; init; }

    /// <summary>
    /// Field names
    /// </summary>
    public required string[] FieldNames { get; init; }

    /// <summary>
    /// Minimum visibility scale
    /// </summary>
    public double? MinScale { get; init; }

    /// <summary>
    /// Maximum visibility scale
    /// </summary>
    public double? MaxScale { get; init; }

    /// <summary>
    /// Default visibility
    /// </summary>
    public required bool DefaultVisibility { get; init; }

    /// <summary>
    /// Whether attachments are supported
    /// </summary>
    public required bool SupportsAttachments { get; init; }

    /// <summary>
    /// Number of relationships
    /// </summary>
    public required int RelationshipCount { get; init; }
}

/// <summary>
/// Response listing all layers
/// </summary>
public sealed record LayerListResponse
{
    /// <summary>
    /// List of layers
    /// </summary>
    public required LayerResponse[] Layers { get; init; }
}

// ============================================================================
// Service-Layer Binding DTOs
// ============================================================================

/// <summary>
/// Request to bind a layer to a service
/// </summary>
public sealed record BindLayerRequest
{
    /// <summary>
    /// Layer ID to bind
    /// </summary>
    public required int LayerId { get; init; }
}

/// <summary>
/// Response for binding operations
/// </summary>
public sealed record BindingResponse
{
    /// <summary>
    /// Whether the operation succeeded
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Message describing the result
    /// </summary>
    public required string Message { get; init; }
}

// ============================================================================
// Relationship DTOs
// ============================================================================

/// <summary>
/// Request to create a relationship
/// </summary>
public sealed record CreateRelationshipRequest
{
    /// <summary>
    /// Related layer ID
    /// </summary>
    public required int RelatedLayerId { get; init; }

    /// <summary>
    /// Relationship name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Relationship type (OneToMany, ManyToMany, etc.)
    /// </summary>
    public required string RelationshipType { get; init; }

    /// <summary>
    /// Foreign key field in origin layer
    /// </summary>
    public required string OriginForeignKeyField { get; init; }

    /// <summary>
    /// Foreign key field in destination layer
    /// </summary>
    public required string DestinationForeignKeyField { get; init; }

    /// <summary>
    /// Relationship description
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Response for relationship operations
/// </summary>
public sealed record RelationshipResponse
{
    /// <summary>
    /// Relationship ID
    /// </summary>
    public required int RelationshipId { get; init; }

    /// <summary>
    /// Relationship name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Related layer ID
    /// </summary>
    public required int RelatedLayerId { get; init; }

    /// <summary>
    /// Relationship type
    /// </summary>
    public required string RelationshipType { get; init; }

    /// <summary>
    /// Origin foreign key field
    /// </summary>
    public required string OriginForeignKeyField { get; init; }

    /// <summary>
    /// Destination foreign key field
    /// </summary>
    public required string DestinationForeignKeyField { get; init; }

    /// <summary>
    /// Relationship description
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Response listing relationships for a layer
/// </summary>
public sealed record RelationshipListResponse
{
    /// <summary>
    /// Layer ID
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// List of relationships
    /// </summary>
    public required RelationshipResponse[] Relationships { get; init; }
}

// ============================================================================
// Style DTOs
// ============================================================================

/// <summary>
/// Request to update layer style
/// </summary>
public sealed record UpdateStyleRequest
{
    /// <summary>
    /// Style in MapLibre GL JSON format (optional)
    /// </summary>
    public object? MapLibreStyle { get; init; }

    /// <summary>
    /// GeoServices drawingInfo format (optional)
    /// </summary>
    public object? DrawingInfo { get; init; }
}

/// <summary>
/// Response for layer style
/// </summary>
public sealed record StyleResponse
{
    /// <summary>
    /// Layer ID
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Style in MapLibre GL JSON format
    /// </summary>
    public object? MapLibreStyle { get; init; }

    /// <summary>
    /// GeoServices drawingInfo format
    /// </summary>
    public object? DrawingInfo { get; init; }
}

// ============================================================================
// Generic Response DTOs
// ============================================================================

/// <summary>
/// Generic success response
/// </summary>
public sealed record SuccessResponse
{
    /// <summary>
    /// Whether the operation succeeded
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Message describing the result
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// Response with validation errors
/// </summary>
public sealed record ValidationErrorResponse
{
    /// <summary>
    /// Error message
    /// </summary>
    public required string Error { get; init; }

    /// <summary>
    /// Validation error details
    /// </summary>
    public required string[] ValidationErrors { get; init; }
}
