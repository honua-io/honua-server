// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for metadata resource CRUD (ADR-0023).
/// </summary>
internal static class MetadataResourceEndpoints
{
    private const string DefaultNamespace = "default";

    internal sealed class MetadataResourceEndpointsLog;

    /// <summary>
    /// Maps metadata resource endpoints for admin API.
    /// </summary>
    public static void MapMetadataResourceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/metadata/resources")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin Metadata")
            .RequireAdminAuthorization();

        _ = group.Map(string.Empty, HandleListResources)
            .WithName("ListMetadataResources")
            .WithSummary("List metadata resources")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/{kind}/{namespace}/{name}", HandleGetResource)
            .WithName("GetMetadataResource")
            .WithSummary("Get metadata resource")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map(string.Empty, HandleCreateResource)
            .WithName("CreateMetadataResource")
            .WithSummary("Create metadata resource")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        _ = group.Map("/{kind}/{namespace}/{name}", HandleUpdateResource)
            .WithName("UpdateMetadataResource")
            .WithSummary("Update metadata resource")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        _ = group.Map("/{kind}/{namespace}/{name}", HandleDeleteResource)
            .WithName("DeleteMetadataResource")
            .WithSummary("Delete metadata resource")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));
    }

    private static async Task HandleListResources(
        HttpContext context,
        IMetadataResourceStore store)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var kind = context.Request.Query["kind"].ToString();
        var @namespace = context.Request.Query["namespace"].ToString();

        var resources = await store.ListAsync(
            string.IsNullOrWhiteSpace(kind) ? null : kind,
            string.IsNullOrWhiteSpace(@namespace) ? null : @namespace,
            context.RequestAborted);

        var response = ApiResponse<MetadataResource[]>.CreateSuccess(resources.ToArray());
        await AdminResponseWriter.WriteJsonAsync(context, response, MetadataResourceJsonContext.Default.ApiResponseMetadataResourceArray);
    }

    private static async Task HandleGetResource(
        HttpContext context,
        string kind,
        string @namespace,
        string name,
        IMetadataResourceStore store,
        IETagService etagService)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var identifier = new MetadataResourceIdentifier(kind, NormalizeNamespace(@namespace), name);
        var resource = await store.GetAsync(identifier, context.RequestAborted);
        if (resource == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, "Resource not found.");
            return;
        }

        var etag = CreateResourceEtag(resource, etagService);
        if (!string.IsNullOrWhiteSpace(etag))
        {
            etagService.SetCacheHeaders(context.Response, etag);
        }

        var response = ApiResponse<MetadataResource>.CreateSuccess(resource);
        await AdminResponseWriter.WriteJsonAsync(context, response, MetadataResourceJsonContext.Default.ApiResponseMetadataResource);
    }

    private static async Task HandleCreateResource(
        HttpContext context,
        MetadataResource resource,
        IMetadataResourceStore store,
        IMetadataSchemaRegistry schemaRegistry,
        IMetadataCompiler compiler)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var normalized = NormalizeResource(resource, null);
        var validation = schemaRegistry.ValidateAndUpgrade(normalized);
        if (!validation.IsValid || validation.Resource == null)
        {
            await WriteValidationError(context, validation.Errors);
            return;
        }

        var compilation = await compiler.CompileAsync(validation.Resource, context.RequestAborted);
        var resourceWithStatus = new MetadataResource
        {
            ApiVersion = validation.Resource.ApiVersion,
            Kind = validation.Resource.Kind,
            Metadata = validation.Resource.Metadata,
            Spec = validation.Resource.Spec,
            Status = compilation.Status
        };

        var result = await store.CreateAsync(resourceWithStatus, context.RequestAborted);
        if (result.Outcome == MetadataResourceWriteOutcome.Conflict)
        {
            await WriteError(context, StatusCodes.Status409Conflict, result.Error ?? "Resource already exists.");
            return;
        }

        if (result.Resource == null)
        {
            await WriteError(context, StatusCodes.Status500InternalServerError, "Failed to create resource.");
            return;
        }

        var createdEtag = CreateResourceEtag(result.Resource, context.RequestServices.GetRequiredService<IETagService>());
        if (!string.IsNullOrWhiteSpace(createdEtag))
        {
            context.Response.Headers.ETag = createdEtag;
        }

        var artifact = new CompiledMetadataArtifact
        {
            ResourceId = result.Resource.Metadata?.Id,
            ApiVersion = result.Resource.ApiVersion,
            Kind = result.Resource.Kind,
            ResourceVersion = result.Resource.Metadata?.ResourceVersion,
            Spec = result.Resource.Spec,
            GeneratedAt = compilation.Artifact.GeneratedAt,
            CompilerVersion = compilation.Artifact.CompilerVersion
        };

        await store.StoreCompiledArtifactAsync(artifact, context.RequestAborted);

        context.Response.StatusCode = StatusCodes.Status201Created;
        var response = ApiResponse<MetadataResource>.CreateSuccess(result.Resource);
        await AdminResponseWriter.WriteJsonAsync(context, response, MetadataResourceJsonContext.Default.ApiResponseMetadataResource);
    }

    private static async Task HandleUpdateResource(
        HttpContext context,
        string kind,
        string @namespace,
        string name,
        MetadataResource resource,
        IMetadataResourceStore store,
        IMetadataSchemaRegistry schemaRegistry,
        IMetadataCompiler compiler,
        IETagService etagService)
    {
        if (!HttpMethods.IsPut(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var identifier = new MetadataResourceIdentifier(kind, NormalizeNamespace(@namespace), name);
        var existing = await store.GetAsync(identifier, context.RequestAborted);
        if (existing == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, "Resource not found.");
            return;
        }

        if (!TryValidateIfMatch(context, existing, etagService, out var errorStatus))
        {
            await WriteError(context, errorStatus, errorStatus == StatusCodes.Status428PreconditionRequired
                ? "If-Match header is required."
                : "ETag precondition failed.");
            return;
        }

        var normalized = NormalizeResource(resource, identifier, existing);
        var validation = schemaRegistry.ValidateAndUpgrade(normalized);
        if (!validation.IsValid || validation.Resource == null)
        {
            await WriteValidationError(context, validation.Errors);
            return;
        }

        var compilation = await compiler.CompileAsync(validation.Resource, context.RequestAborted);
        var resourceWithStatus = new MetadataResource
        {
            ApiVersion = validation.Resource.ApiVersion,
            Kind = validation.Resource.Kind,
            Metadata = validation.Resource.Metadata,
            Spec = validation.Resource.Spec,
            Status = compilation.Status
        };

        var expectedVersion = ParseResourceVersion(existing.Metadata?.ResourceVersion);
        var updateResult = await store.UpdateAsync(resourceWithStatus, expectedVersion, context.RequestAborted);
        if (updateResult.Outcome == MetadataResourceWriteOutcome.Conflict)
        {
            await WriteError(context, StatusCodes.Status409Conflict, updateResult.Error ?? "Resource version conflict.");
            return;
        }

        if (updateResult.Resource == null)
        {
            await WriteError(context, StatusCodes.Status500InternalServerError, "Failed to update resource.");
            return;
        }

        var updatedEtag = CreateResourceEtag(updateResult.Resource, etagService);
        if (!string.IsNullOrWhiteSpace(updatedEtag))
        {
            context.Response.Headers.ETag = updatedEtag;
        }

        var artifact = new CompiledMetadataArtifact
        {
            ResourceId = updateResult.Resource.Metadata?.Id,
            ApiVersion = updateResult.Resource.ApiVersion,
            Kind = updateResult.Resource.Kind,
            ResourceVersion = updateResult.Resource.Metadata?.ResourceVersion,
            Spec = updateResult.Resource.Spec,
            GeneratedAt = compilation.Artifact.GeneratedAt,
            CompilerVersion = compilation.Artifact.CompilerVersion
        };

        await store.StoreCompiledArtifactAsync(artifact, context.RequestAborted);

        var response = ApiResponse<MetadataResource>.CreateSuccess(updateResult.Resource);
        await AdminResponseWriter.WriteJsonAsync(context, response, MetadataResourceJsonContext.Default.ApiResponseMetadataResource);
    }

    private static async Task HandleDeleteResource(
        HttpContext context,
        string kind,
        string @namespace,
        string name,
        IMetadataResourceStore store,
        IETagService etagService)
    {
        if (!HttpMethods.IsDelete(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var identifier = new MetadataResourceIdentifier(kind, NormalizeNamespace(@namespace), name);
        var existing = await store.GetAsync(identifier, context.RequestAborted);
        if (existing == null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, "Resource not found.");
            return;
        }

        if (!TryValidateIfMatch(context, existing, etagService, out var errorStatus))
        {
            await WriteError(context, errorStatus, errorStatus == StatusCodes.Status428PreconditionRequired
                ? "If-Match header is required."
                : "ETag precondition failed.");
            return;
        }

        var expectedVersion = ParseResourceVersion(existing.Metadata?.ResourceVersion);
        var result = await store.DeleteAsync(identifier, expectedVersion, context.RequestAborted);
        if (result.Outcome == MetadataResourceWriteOutcome.Conflict)
        {
            await WriteError(context, StatusCodes.Status409Conflict, result.Error ?? "Resource version conflict.");
            return;
        }

        var response = ApiResponse<object>.SuccessWithMessage("Resource deleted.");
        await AdminResponseWriter.WriteJsonAsync(context, response, MetadataResourceJsonContext.Default.ApiResponseObject);
    }

    private static MetadataResource NormalizeResource(
        MetadataResource resource,
        MetadataResourceIdentifier? identifier,
        MetadataResource? existing = null)
    {
        var metadata = resource.Metadata ?? new ResourceMetadata();
        var name = identifier?.Name ?? metadata.Name ?? existing?.Metadata?.Name;
        var @namespace = identifier?.Namespace ?? metadata.Namespace ?? existing?.Metadata?.Namespace ?? DefaultNamespace;
        var kind = identifier?.Kind ?? resource.Kind ?? existing?.Kind;

        metadata = metadata with
        {
            Name = name,
            Namespace = @namespace,
            Id = existing?.Metadata?.Id ?? metadata.Id,
            CreatedAt = existing?.Metadata?.CreatedAt ?? metadata.CreatedAt
        };

        return new MetadataResource
        {
            ApiVersion = resource.ApiVersion ?? existing?.ApiVersion,
            Kind = kind,
            Metadata = metadata,
            Spec = resource.Spec,
            Status = resource.Status
        };
    }

    private static string NormalizeNamespace(string? @namespace)
        => string.IsNullOrWhiteSpace(@namespace) ? DefaultNamespace : @namespace;

    private static bool TryValidateIfMatch(
        HttpContext context,
        MetadataResource resource,
        IETagService etagService,
        out int statusCode)
    {
        var ifMatch = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            statusCode = StatusCodes.Status428PreconditionRequired;
            return false;
        }

        var etag = CreateResourceEtag(resource, etagService);
        if (string.IsNullOrWhiteSpace(etag) || !etagService.MatchesPrecondition(ifMatch, etag))
        {
            statusCode = StatusCodes.Status412PreconditionFailed;
            return false;
        }

        statusCode = StatusCodes.Status200OK;
        return true;
    }

    private static string? CreateResourceEtag(MetadataResource resource, IETagService etagService)
    {
        var version = resource.Metadata?.ResourceVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return etagService.ComputeETag(version);
    }

    private static long ParseResourceVersion(string? resourceVersion)
    {
        if (string.IsNullOrWhiteSpace(resourceVersion))
        {
            return 0;
        }

        return long.TryParse(resourceVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static Task WriteValidationError(HttpContext context, IReadOnlyList<string> errors)
    {
        var message = errors.Count == 0 ? "Validation failed." : string.Join(" ", errors);
        return WriteError(context, StatusCodes.Status400BadRequest, message);
    }

    private static Task WriteError(HttpContext context, int statusCode, string message)
        => AdminResponseWriter.WriteErrorAsync(context, message, statusCode);
}
