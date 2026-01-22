// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Data.Common;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Styling;
using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin metadata endpoints for managing services, layers, relationships, and styles
/// </summary>
internal static partial class MetadataEndpoints
{
    internal sealed class MetadataEndpointsLog { }

    private const string LayerCacheKeyPrefix = "layer:";
    private const string LayerListCacheKey = "layers:all";
    private const string ServiceCacheKeyPrefix = "service:";
    private const string ServiceListCacheKey = "services:all";
    private const string RelationshipCacheKeyPrefix = "relationship:";
    private static readonly FrozenSet<string> _relationshipTypes = new[]
        {
            "esriRelRoleOrigin",
            "esriRelRoleDestination",
            "esriRelRoleAny"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Map admin metadata endpoints to the web application with formal API versioning
    /// </summary>
    public static void MapMetadataEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/metadata")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin Metadata")
            .RequireAdminAuthorization();

        // Service endpoints
        _ = group.Map("/services", HandleListServices)
            .WithName("ListServices")
            .WithSummary("List all services")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/services/{name}", HandleGetService)
            .WithName("GetService")
            .WithSummary("Get service by name")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/services", HandleCreateService)
            .WithName("CreateService")
            .WithSummary("Create a new service")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        _ = group.Map("/services/{name}", HandleUpdateService)
            .WithName("UpdateService")
            .WithSummary("Update a service")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        _ = group.Map("/services/{name}", HandleDeleteService)
            .WithName("DeleteService")
            .WithSummary("Delete a service")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        // Service-layer binding endpoints
        _ = group.Map("/services/{name}/layers", HandleBindLayer)
            .WithName("BindLayerToService")
            .WithSummary("Bind a layer to a service")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        _ = group.Map("/services/{name}/layers/{layerId:int}", HandleUnbindLayer)
            .WithName("UnbindLayerFromService")
            .WithSummary("Unbind a layer from a service")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        // Layer endpoints
        _ = group.Map("/layers", HandleListLayers)
            .WithName("ListLayers")
            .WithSummary("List all layers")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/layers/{layerId:int}", HandleGetLayer)
            .WithName("GetLayer")
            .WithSummary("Get layer by ID")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/layers", HandleCreateLayer)
            .WithName("CreateLayer")
            .WithSummary("Create a new layer from database table")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        _ = group.Map("/layers/{layerId:int}", HandleUpdateLayer)
            .WithName("UpdateLayer")
            .WithSummary("Update a layer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        _ = group.Map("/layers/{layerId:int}", HandleDeleteLayer)
            .WithName("DeleteLayer")
            .WithSummary("Delete a layer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        _ = group.Map("/layers/{layerId:int}/refresh", HandleRefreshLayer)
            .WithName("RefreshLayer")
            .WithSummary("Refresh layer metadata from database")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        // Relationship endpoints
        _ = group.Map("/layers/{layerId:int}/relationships", HandleListRelationships)
            .WithName("ListRelationships")
            .WithSummary("List relationships for a layer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/layers/{layerId:int}/relationships", HandleCreateRelationship)
            .WithName("CreateRelationship")
            .WithSummary("Create a relationship")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        _ = group.Map("/layers/{layerId:int}/relationships/{relationshipId:int}", HandleDeleteRelationship)
            .WithName("DeleteRelationship")
            .WithSummary("Delete a relationship")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        // Style endpoints
        _ = group.Map("/layers/{layerId:int}/style", HandleGetStyle)
            .WithName("GetLayerStyle")
            .WithSummary("Get layer style")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/layers/{layerId:int}/style", HandleUpdateStyle)
            .WithName("UpdateLayerStyle")
            .WithSummary("Update layer style")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));
    }

    // ========================================================================
    // Service handlers
    // ========================================================================

    private static async Task HandleListServices(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var services = await catalog.ListServicesAsync(context.RequestAborted);

        var response = new ServiceListResponse
        {
            Services = services.Select(MapToServiceResponse).ToArray()
        };

        await WriteJsonAsync(context, response, MetadataJsonContext.Default.ServiceListResponse);
    }

    private static async Task HandleGetService(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var name = context.GetRouteValue("name")?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Service name is required");
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var service = await catalog.GetServiceAsync(name, context.RequestAborted);

        if (service == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Service '{name}' not found");
            return;
        }

        var response = MapToServiceResponse(service);
        await WriteJsonAsync(context, response, MetadataJsonContext.Default.ServiceResponse);
    }

    private static async Task HandleCreateService(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        CreateServiceRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                MetadataJsonContext.Default.CreateServiceRequest,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Invalid JSON request body");
            return;
        }

        if (request == null)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Request body is required");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Service name is required");
            return;
        }

        if (request.ConnectionId == Guid.Empty)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Connection ID is required");
            return;
        }

        var adminCatalog = context.RequestServices.GetService<IAdminCatalog>();
        if (adminCatalog == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Admin catalog not available");
            return;
        }

        var registry = context.RequestServices.GetService<ISecureConnectionRegistry>();
        if (registry == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Secure connection registry not available");
            return;
        }

        var connection = await registry.GetConnectionAsync(request.ConnectionId, context.RequestAborted);
        if (connection == null)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Connection not found");
            return;
        }

        try
        {
            var spatialReference = SpatialReference.Create(request.SpatialReferenceSrid);
            var metadata = request.AccessPolicy == null
                ? null
                : new CatalogMetadata { AccessPolicy = request.AccessPolicy };
            var service = await adminCatalog.CreateServiceAsync(
                request.Name,
                request.Description,
                spatialReference,
                metadata,
                request.ConnectionId,
                context.RequestAborted);

            await InvalidateServiceCache(context, request.Name);

            var response = MapToServiceResponse(service);
            context.Response.StatusCode = StatusCodes.Status201Created;
            await WriteJsonAsync(context, response, MetadataJsonContext.Default.ServiceResponse);
        }
        catch (InvalidOperationException)
        {
            // Use safe error message to avoid leaking internal details
            await WriteError(context, StatusCodes.Status409Conflict, "A service with this name already exists.");
        }
    }

    private static async Task HandleUpdateService(HttpContext context)
    {
        if (!HttpMethods.IsPut(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var name = context.GetRouteValue("name")?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Service name is required");
            return;
        }

        UpdateServiceRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                MetadataJsonContext.Default.UpdateServiceRequest,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Invalid JSON request body");
            return;
        }

        if (request == null)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Request body is required");
            return;
        }

        var adminCatalog = context.RequestServices.GetService<IAdminCatalog>();
        if (adminCatalog == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Admin catalog not available");
            return;
        }

        if (request.ConnectionId.HasValue && request.ConnectionId.Value == Guid.Empty)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Connection ID is required");
            return;
        }

        if (request.ConnectionId.HasValue)
        {
            var registry = context.RequestServices.GetService<ISecureConnectionRegistry>();
            if (registry == null)
            {
                await WriteError(context, StatusCodes.Status501NotImplemented, "Secure connection registry not available");
                return;
            }

            var connection = await registry.GetConnectionAsync(request.ConnectionId.Value, context.RequestAborted);
            if (connection == null)
            {
                await WriteError(context, StatusCodes.Status400BadRequest, "Connection not found");
                return;
            }
        }

        var metadata = request.AccessPolicy == null
            ? null
            : new CatalogMetadata { AccessPolicy = request.AccessPolicy };
        var service = await adminCatalog.UpdateServiceAsync(
            name,
            request.Description,
            metadata,
            request.ConnectionId,
            context.RequestAborted);

        if (service == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Service '{name}' not found");
            return;
        }

        await InvalidateServiceCache(context, name);

        var response = MapToServiceResponse(service);
        await WriteJsonAsync(context, response, MetadataJsonContext.Default.ServiceResponse);
    }

    private static async Task HandleDeleteService(HttpContext context)
    {
        if (!HttpMethods.IsDelete(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var name = context.GetRouteValue("name")?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Service name is required");
            return;
        }

        var adminCatalog = context.RequestServices.GetService<IAdminCatalog>();
        if (adminCatalog == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Admin catalog not available");
            return;
        }

        var deleted = await adminCatalog.DeleteServiceAsync(name, context.RequestAborted);

        if (!deleted)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Service '{name}' not found");
            return;
        }

        await InvalidateServiceCache(context, name);

        var response = new SuccessResponse { Success = true, Message = $"Service '{name}' deleted" };
        await WriteJsonAsync(context, response, MetadataJsonContext.Default.SuccessResponse);
    }

    // ========================================================================
    // Service-layer binding handlers
    // ========================================================================

    private static async Task HandleBindLayer(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var name = context.GetRouteValue("name")?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Service name is required");
            return;
        }

        BindLayerRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                MetadataJsonContext.Default.BindLayerRequest,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Invalid JSON request body");
            return;
        }

        if (request == null)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Request body is required");
            return;
        }

        var adminCatalog = context.RequestServices.GetService<IAdminCatalog>();
        if (adminCatalog == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Admin catalog not available");
            return;
        }

        bool success;
        try
        {
            success = await adminCatalog.BindLayerToServiceAsync(name, request.LayerId, context.RequestAborted);
        }
        catch (InvalidOperationException ex)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, ex.Message);
            return;
        }

        if (!success)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, $"Failed to bind layer {request.LayerId} to service '{name}'");
            return;
        }

        await InvalidateServiceAndLayerCache(context, name, request.LayerId);

        var response = new BindingResponse { Success = true, Message = $"Layer {request.LayerId} bound to service '{name}'" };
        await WriteJsonAsync(context, response, MetadataJsonContext.Default.BindingResponse);
    }

    private static async Task HandleUnbindLayer(HttpContext context)
    {
        if (!HttpMethods.IsDelete(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var name = context.GetRouteValue("name")?.ToString();
        var layerIdStr = context.GetRouteValue("layerId")?.ToString();

        if (string.IsNullOrEmpty(name))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Service name is required");
            return;
        }

        if (!int.TryParse(layerIdStr, out var layerId))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Valid layer ID is required");
            return;
        }

        var adminCatalog = context.RequestServices.GetService<IAdminCatalog>();
        if (adminCatalog == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Admin catalog not available");
            return;
        }

        var success = await adminCatalog.UnbindLayerFromServiceAsync(name, layerId, context.RequestAborted);

        if (!success)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Layer {layerId} not bound to service '{name}'");
            return;
        }

        await InvalidateServiceAndLayerCache(context, name, layerId);

        var response = new BindingResponse { Success = true, Message = $"Layer {layerId} unbound from service '{name}'" };
        await WriteJsonAsync(context, response, MetadataJsonContext.Default.BindingResponse);
    }

    // ========================================================================
    // Layer handlers
    // ========================================================================

    private static async Task HandleListLayers(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var layers = await catalog.ListLayersAsync(context.RequestAborted);

        var response = new LayerListResponse
        {
            Layers = layers.Select(MapToLayerResponse).ToArray()
        };

        await WriteJsonAsync(context, response, MetadataJsonContext.Default.LayerListResponse);
    }

    private static async Task HandleGetLayer(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var layerIdStr = context.GetRouteValue("layerId")?.ToString();
        if (!int.TryParse(layerIdStr, out var layerId))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Valid layer ID is required");
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var layer = await catalog.GetLayerAsync(layerId, context.RequestAborted);

        if (layer == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Layer {layerId} not found");
            return;
        }

        var response = MapToLayerResponse(layer);
        await WriteJsonAsync(context, response, MetadataJsonContext.Default.LayerResponse);
    }

    private static async Task HandleCreateLayer(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        CreateLayerRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                MetadataJsonContext.Default.CreateLayerRequest,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Invalid JSON request body");
            return;
        }

        if (request == null)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Request body is required");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.TableName))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Table name is required");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Display name is required");
            return;
        }

        var adminCatalog = context.RequestServices.GetService<IAdminCatalog>();
        if (adminCatalog == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Admin catalog not available");
            return;
        }

        try
        {
            var metadata = request.AccessPolicy == null
                ? null
                : new CatalogMetadata { AccessPolicy = request.AccessPolicy };
            var layer = await adminCatalog.CreateLayerAsync(
                request.TableName,
                request.SchemaName,
                request.DisplayName,
                request.Description,
                metadata,
                context.RequestAborted);

            await InvalidateLayerCache(context, layer.Id);

            var response = MapToLayerResponse(layer);
            context.Response.StatusCode = StatusCodes.Status201Created;
            await WriteJsonAsync(context, response, MetadataJsonContext.Default.LayerResponse);
        }
        catch (InvalidOperationException)
        {
            // Use safe error message to avoid leaking internal details
            await WriteError(context, StatusCodes.Status400BadRequest, "Failed to create layer. The layer may already exist or the request is invalid.");
        }
    }

    private static async Task HandleUpdateLayer(HttpContext context)
    {
        if (!HttpMethods.IsPut(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var layerIdStr = context.GetRouteValue("layerId")?.ToString();
        if (!int.TryParse(layerIdStr, out var layerId))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Valid layer ID is required");
            return;
        }

        UpdateLayerRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                MetadataJsonContext.Default.UpdateLayerRequest,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Invalid JSON request body");
            return;
        }

        if (request == null)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Request body is required");
            return;
        }

        var adminCatalog = context.RequestServices.GetService<IAdminCatalog>();
        if (adminCatalog == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Admin catalog not available");
            return;
        }

        var metadata = request.AccessPolicy == null
            ? null
            : new CatalogMetadata { AccessPolicy = request.AccessPolicy };
        var layer = await adminCatalog.UpdateLayerAsync(
            layerId,
            request.DisplayName,
            request.Description,
            request.MinScale,
            request.MaxScale,
            request.DefaultVisibility,
            metadata,
            context.RequestAborted);

        if (layer == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Layer {layerId} not found");
            return;
        }

        await InvalidateLayerCache(context, layerId);

        var response = MapToLayerResponse(layer);
        await WriteJsonAsync(context, response, MetadataJsonContext.Default.LayerResponse);
    }

    private static async Task HandleDeleteLayer(HttpContext context)
    {
        if (!HttpMethods.IsDelete(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var layerIdStr = context.GetRouteValue("layerId")?.ToString();
        if (!int.TryParse(layerIdStr, out var layerId))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Valid layer ID is required");
            return;
        }

        var adminCatalog = context.RequestServices.GetService<IAdminCatalog>();
        if (adminCatalog == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Admin catalog not available");
            return;
        }

        var deleted = await adminCatalog.DeleteLayerAsync(layerId, context.RequestAborted);

        if (!deleted)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Layer {layerId} not found");
            return;
        }

        await InvalidateLayerCache(context, layerId);

        var response = new SuccessResponse { Success = true, Message = $"Layer {layerId} deleted" };
        await WriteJsonAsync(context, response, MetadataJsonContext.Default.SuccessResponse);
    }

    private static async Task HandleRefreshLayer(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var layerIdStr = context.GetRouteValue("layerId")?.ToString();
        if (!int.TryParse(layerIdStr, out var layerId))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Valid layer ID is required");
            return;
        }

        var adminCatalog = context.RequestServices.GetService<IAdminCatalog>();
        if (adminCatalog == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Admin catalog not available");
            return;
        }

        var layer = await adminCatalog.RefreshLayerAsync(layerId, context.RequestAborted);

        if (layer == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Layer {layerId} not found");
            return;
        }

        await InvalidateLayerCache(context, layerId);

        var response = MapToLayerResponse(layer);
        await WriteJsonAsync(context, response, MetadataJsonContext.Default.LayerResponse);
    }

    // ========================================================================
    // Relationship handlers
    // ========================================================================

    private static async Task HandleListRelationships(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var layerIdStr = context.GetRouteValue("layerId")?.ToString();
        if (!int.TryParse(layerIdStr, out var layerId))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Valid layer ID is required");
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var relationships = await catalog.ListRelationshipsAsync(layerId, context.RequestAborted);

        var response = new RelationshipListResponse
        {
            LayerId = layerId,
            Relationships = relationships.Select(MapToRelationshipResponse).ToArray()
        };

        await WriteJsonAsync(context, response, MetadataJsonContext.Default.RelationshipListResponse);
    }

    private static async Task HandleCreateRelationship(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var layerIdStr = context.GetRouteValue("layerId")?.ToString();
        if (!int.TryParse(layerIdStr, out var layerId))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Valid layer ID is required");
            return;
        }

        CreateRelationshipRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                MetadataJsonContext.Default.CreateRelationshipRequest,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Invalid JSON request body");
            return;
        }

        if (request == null)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Request body is required");
            return;
        }

        var adminCatalog = context.RequestServices.GetService<IAdminCatalog>();
        if (adminCatalog == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Admin catalog not available");
            return;
        }

        try
        {
            var relationship = await adminCatalog.CreateRelationshipAsync(
                layerId,
                request.RelatedLayerId,
                request.Name,
                request.RelationshipType,
                request.OriginForeignKeyField,
                request.DestinationForeignKeyField,
                request.Description,
                context.RequestAborted);

            // Invalidate both origin and related layer caches
            await InvalidateLayerCache(context, layerId);
            await InvalidateLayerCache(context, request.RelatedLayerId);

            var response = MapToRelationshipResponse(relationship);
            context.Response.StatusCode = StatusCodes.Status201Created;
            await WriteJsonAsync(context, response, MetadataJsonContext.Default.RelationshipResponse);
        }
        catch (InvalidOperationException)
        {
            // Use safe error message to avoid leaking internal details
            await WriteError(context, StatusCodes.Status400BadRequest, "Failed to create relationship. The relationship may already exist or the request is invalid.");
        }
        catch (DbException)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Failed to create relationship. The request is invalid.");
        }
    }

    private static async Task HandleDeleteRelationship(HttpContext context)
    {
        if (!HttpMethods.IsDelete(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var layerIdStr = context.GetRouteValue("layerId")?.ToString();
        var relationshipIdStr = context.GetRouteValue("relationshipId")?.ToString();

        if (!int.TryParse(layerIdStr, out var layerId))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Valid layer ID is required");
            return;
        }

        if (!int.TryParse(relationshipIdStr, out var relationshipId))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Valid relationship ID is required");
            return;
        }

        var adminCatalog = context.RequestServices.GetService<IAdminCatalog>();
        if (adminCatalog == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Admin catalog not available");
            return;
        }

        var deleted = await adminCatalog.DeleteRelationshipAsync(layerId, relationshipId, context.RequestAborted);

        if (!deleted)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Relationship {relationshipId} not found on layer {layerId}");
            return;
        }

        await InvalidateLayerCache(context, layerId);

        var response = new SuccessResponse { Success = true, Message = $"Relationship {relationshipId} deleted from layer {layerId}" };
        await WriteJsonAsync(context, response, MetadataJsonContext.Default.SuccessResponse);
    }

    // ========================================================================
    // Style handlers
    // ========================================================================

    private static async Task HandleGetStyle(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var layerIdStr = context.GetRouteValue("layerId")?.ToString();
        if (!int.TryParse(layerIdStr, out var layerId))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Valid layer ID is required");
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var styleService = context.RequestServices.GetService<ILayerStyleService>();
        if (styleService == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Layer style service not available");
            return;
        }

        var layer = await catalog.GetLayerAsync(layerId, context.RequestAborted);

        if (layer == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Layer {layerId} not found");
            return;
        }

        var style = await styleService.GetStyleAsync(layer, context.RequestAborted);
        if (style == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Style for layer {layerId} not found");
            return;
        }

        var response = new StyleResponse
        {
            LayerId = layerId,
            MapLibreStyle = style.MapLibreStyle,
            DrawingInfo = style.DrawingInfo
        };

        await WriteJsonAsync(context, response, MetadataJsonContext.Default.StyleResponse);
    }

    private static async Task HandleUpdateStyle(HttpContext context)
    {
        if (!HttpMethods.IsPut(context.Request.Method))
        {
            await WriteMethodNotAllowed(context);
            return;
        }

        var layerIdStr = context.GetRouteValue("layerId")?.ToString();
        if (!int.TryParse(layerIdStr, out var layerId))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Valid layer ID is required");
            return;
        }

        UpdateStyleRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                MetadataJsonContext.Default.UpdateStyleRequest,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Invalid JSON request body");
            return;
        }

        if (request == null)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Request body is required");
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var styleService = context.RequestServices.GetService<ILayerStyleService>();
        if (styleService == null)
        {
            await WriteError(context, StatusCodes.Status501NotImplemented, "Layer style service not available");
            return;
        }

        var layer = await catalog.GetLayerAsync(layerId, context.RequestAborted);

        if (layer == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Layer {layerId} not found");
            return;
        }

        var updateResult = await styleService.UpdateStyleAsync(
            layer,
            request.MapLibreStyle,
            request.DrawingInfo,
            context.RequestAborted);

        if (updateResult.Status == LayerStyleUpdateStatus.Invalid)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, updateResult.ErrorMessage ?? "Invalid style payload");
            return;
        }

        if (updateResult.Status == LayerStyleUpdateStatus.NotFound || updateResult.Style == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Layer {layerId} not found");
            return;
        }

        await InvalidateLayerCache(context, layerId);

        var response = new StyleResponse
        {
            LayerId = layerId,
            MapLibreStyle = updateResult.Style.MapLibreStyle,
            DrawingInfo = updateResult.Style.DrawingInfo
        };

        await WriteJsonAsync(context, response, MetadataJsonContext.Default.StyleResponse);
    }

    // ========================================================================
    // Helper methods
    // ========================================================================

    private static ServiceResponse MapToServiceResponse(ServiceDefinition service) => new()
    {
        Name = service.Name,
        Description = service.Description,
        SpatialReferenceSrid = service.SpatialReference.ToSrid(),
        LayerCount = service.Layers.Length,
        LayerIds = service.Layers.Select(l => l.Id).ToArray(),
        AccessPolicy = service.Metadata?.AccessPolicy,
        ConnectionId = service.ConnectionId
    };

    private static LayerResponse MapToLayerResponse(LayerDefinition layer) => new()
    {
        Id = layer.Id,
        Name = layer.Name,
        Description = layer.Description,
        GeometryType = layer.GeometryType.ToString(),
        SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
        FieldCount = layer.Fields.Length,
        FieldNames = layer.Fields.Select(f => f.Name).ToArray(),
        MinScale = layer.MinScale,
        MaxScale = layer.MaxScale,
        DefaultVisibility = layer.DefaultVisibility,
        SupportsAttachments = layer.SupportsAttachments,
        RelationshipCount = layer.LayerRelationships.Length,
        AccessPolicy = layer.Metadata?.AccessPolicy
    };

    private static RelationshipResponse MapToRelationshipResponse(Relationship relationship) => new()
    {
        RelationshipId = relationship.RelationshipId,
        Name = relationship.Name,
        RelatedLayerId = relationship.RelatedLayerId,
        RelationshipType = relationship.RelationshipType,
        OriginForeignKeyField = relationship.OriginForeignKeyField,
        DestinationForeignKeyField = relationship.DestinationForeignKeyField,
        Description = relationship.Description
    };

    private static async Task InvalidateServiceCache(HttpContext context, string serviceName)
    {
        var normalizedServiceName = serviceName.ToLowerInvariant();

        var cachingCatalog = context.RequestServices.GetService<CachingLayerCatalog>();
        if (cachingCatalog != null)
        {
            await cachingCatalog.InvalidateServiceAsync(serviceName, context.RequestAborted);
        }

        var cacheService = context.RequestServices.GetService<ICacheService>();
        if (cacheService != null)
        {
            await cacheService.RemoveAsync($"{ServiceCacheKeyPrefix}{normalizedServiceName}", context.RequestAborted);
            await cacheService.RemoveAsync(ServiceListCacheKey, context.RequestAborted);
        }

        var outputCache = context.RequestServices.GetService<IOutputCacheStore>();
        if (outputCache != null)
        {
            await outputCache.EvictByTagAsync($"service:{normalizedServiceName}", context.RequestAborted);
            await outputCache.EvictByTagAsync("ogc-metadata", context.RequestAborted);
            await outputCache.EvictByTagAsync("ogc-tiles", context.RequestAborted);
        }
    }

    private static async Task InvalidateLayerCache(HttpContext context, int layerId)
    {
        var cachingCatalog = context.RequestServices.GetService<CachingLayerCatalog>();
        if (cachingCatalog != null)
        {
            await cachingCatalog.InvalidateLayerAsync(layerId, context.RequestAborted);
        }

        var cacheService = context.RequestServices.GetService<ICacheService>();
        if (cacheService != null)
        {
            await cacheService.RemoveAsync($"{LayerCacheKeyPrefix}{layerId}", context.RequestAborted);
            await cacheService.RemoveAsync(LayerListCacheKey, context.RequestAborted);
            await cacheService.RemoveByPatternAsync($"{RelationshipCacheKeyPrefix}{layerId}:*", context.RequestAborted);
        }

        var outputCache = context.RequestServices.GetService<IOutputCacheStore>();
        if (outputCache != null)
        {
            await outputCache.EvictByTagAsync($"layer:{layerId}", context.RequestAborted);
            await outputCache.EvictByTagAsync("ogc-metadata", context.RequestAborted);
            await outputCache.EvictByTagAsync("ogc-tiles", context.RequestAborted);
        }
    }

    private static async Task InvalidateServiceAndLayerCache(HttpContext context, string serviceName, int layerId)
    {
        await InvalidateServiceCache(context, serviceName);
        await InvalidateLayerCache(context, layerId);
    }

    private static async Task WriteJsonAsync<T>(HttpContext context, T response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, response, typeInfo, context.RequestAborted);
    }

    private static Task WriteError(HttpContext context, int statusCode, string message)
    {
        return ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, message).ExecuteAsync(context);
    }

    private static Task WriteMethodNotAllowed(HttpContext context)
    {
        return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status405MethodNotAllowed, "Method not allowed").ExecuteAsync(context);
    }
}
