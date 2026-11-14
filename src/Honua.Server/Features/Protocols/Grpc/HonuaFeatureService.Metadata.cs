// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Grpc.Core;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    public override async Task<Proto.DescribeServiceResponse> DescribeService(
        Proto.DescribeServiceRequest request, ServerCallContext context)
    {
        if (request.PageSize < 0 || request.PageSize > 1000)
        {
            throw MetadataArgument("page_size must be between 0 and 1000.");
        }
        var (service, snapshot) = await ValidateMetadataServiceAsync(request.ServiceId, context).ConfigureAwait(false);
        var layers = await VisibleServiceLayersAsync(service, snapshot, context).ConfigureAwait(false);
        // A layer-specific grant is sufficient for discovery; no service-wide grant is required.
        // When none is visible, authorize the service before exposing its metadata or an empty list.
        if (layers.Count == 0 && await AccessPolicyHelpers.RequireServiceAccessAsync(context.GetHttpContext(), service,
                AuthorizationOperation.Query, context.CancellationToken).ConfigureAwait(false) is not null)
        {
            throw new RpcException(new Status(context.GetHttpContext().User.Identity?.IsAuthenticated == true
                ? StatusCode.PermissionDenied : StatusCode.Unauthenticated, "Service access is denied."));
        }
        var offset = ReadMetadataToken(request.PageToken, request.ServiceId, snapshot.Revision);
        if (offset > layers.Count)
        {
            throw MetadataArgument("page_token is outside the visible service layer list.");
        }
        var response = await CreateServiceMetadataAsync(service, layers, context).ConfigureAwait(false);
        response.Service.ServiceId = request.ServiceId;
        PopulateServicePage(response, layers, new(request, snapshot.Revision, offset));
        return response;
    }

    public override async Task<Proto.DescribeLayerResponse> DescribeLayer(
        Proto.DescribeLayerRequest request, ServerCallContext context)
    {
        var layer = await ValidateGrpcLayerAsync(request.ServiceId, request.LayerId, context.CancellationToken)
            .ConfigureAwait(false);
        await EnsureReadAccessAsync(context, layer.Service, layer.Resource).ConfigureAwait(false);
        var fields = await VisibleMetadataFieldsAsync(layer, context).ConfigureAwait(false);
        var visible = fields.Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resource = layer.Resource;
        var descriptor = new Proto.LayerDescriptor
        {
            ServiceId = request.ServiceId,
            LayerId = request.LayerId,
            Name = LayerName(layer),
            Description = resource.Metadata.Description ?? string.Empty,
            GeometryType = GrpcConversionHelpers.ToProtoGeometryType(layer.GeometryType),
            SpatialReference = GrpcConversionHelpers.ToProtoSpatialReference(layer.SpatialReference),
            ObjectIdField = GrpcMetadataProjector.Visible(layer.ObjectIdFieldName, visible),
            GlobalIdField = GrpcMetadataProjector.Visible(resource.Editing?.GlobalIdField, visible),
            DisplayField = GrpcMetadataProjector.Visible(resource.Display?.DisplayField, visible),
            MaxRecordCount = _maxRecordCount,
            HasZ = resource.Display?.HasZ ?? false,
            HasM = resource.Display?.HasM ?? false,
            Capabilities = await MetadataCapabilitiesAsync(layer, context).ConfigureAwait(false)
        };
        PopulateLayerMetadata(descriptor, layer, fields);
        await AddMetadataRelationshipsAsync(descriptor, new(layer, visible), context).ConfigureAwait(false);
        return new() { Layer = descriptor };
    }

    private async Task<(MetadataV2Service Service, MetadataV2GraphSnapshot Snapshot)> ValidateMetadataServiceAsync(
        string serviceId, ServerCallContext context)
    {
        var validation = await _resourceValidator.ValidateServiceV2Async(serviceId, context.CancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid || validation.Resource is not { } service)
        {
            throw new RpcException(new Status(validation.ErrorCode == ResourceValidationError.NotFound
                ? StatusCode.NotFound : StatusCode.InvalidArgument, "Service is unavailable."));
        }
        if (!IsGrpcEnabled(service))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Grpc is not enabled for this service."));
        }
        var snapshot = validation.Snapshot
            ?? throw new RpcException(new Status(StatusCode.Unavailable, "Metadata snapshot is unavailable."));
        return (service, snapshot);
    }

    private async Task<Proto.DescribeServiceResponse> CreateServiceMetadataAsync(MetadataV2Service service,
        IReadOnlyList<GrpcLayerContext> layers, ServerCallContext context)
    {
        var response = new Proto.DescribeServiceResponse
        {
            Service = new()
            {
                ServiceId = service.EffectivePublicName,
                Name = service.Metadata.Title ?? service.EffectivePublicName,
                Description = service.Metadata.Description ?? string.Empty,
                MaxRecordCount = _maxRecordCount,
                Capabilities = await ServiceMetadataCapabilitiesAsync(layers, context).ConfigureAwait(false)
            }
        };
        if (service.SpatialReference?.ResolveSrid() is { } srid)
        {
            response.Service.SpatialReference = new() { Wkid = srid };
        }
        return response;
    }

    private static void PopulateServicePage(Proto.DescribeServiceResponse response,
        IReadOnlyList<GrpcLayerContext> layers, MetadataPage page)
    {
        var (request, revision, offset) = page;
        var pageSize = request.PageSize == 0 ? 100 : request.PageSize;
        foreach (var layer in layers.Skip(offset).Take(pageSize))
        {
            response.Layers.Add(new Proto.LayerSummary
            {
                LayerId = layer.Publication.LayerIndex!.Value,
                Name = LayerName(layer),
                Description = layer.Resource.Metadata.Description ?? string.Empty,
                GeometryType = GrpcConversionHelpers.ToProtoGeometryType(layer.GeometryType)
            });
        }
        var next = offset + response.Layers.Count;
        if (next < layers.Count)
        {
            response.NextPageToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{request.ServiceId}\n{revision.ToString(CultureInfo.InvariantCulture)}\n{next.ToString(CultureInfo.InvariantCulture)}"));
        }
    }

    private static async Task<List<GrpcLayerContext>> VisibleServiceLayersAsync(MetadataV2Service service,
        MetadataV2GraphSnapshot snapshot, ServerCallContext context)
    {
        var layers = new List<GrpcLayerContext>();
        foreach (var publication in snapshot.PublicationsForService(service.Metadata.Id)
                     .Where(publication => publication.LayerIndex.HasValue && snapshot.IsRoutable(publication))
                     .OrderBy(publication => publication.LayerIndex))
        {
            var resource = snapshot.ResolveResource(publication)!;
            var storageId = snapshot.ResolveStorageLayerId(publication, resource);
            if (!storageId.HasValue)
            {
                continue;
            }
            var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(context.GetHttpContext(), resource,
                service, AuthorizationOperation.Query, context.CancellationToken).ConfigureAwait(false);
            if (decision.IsAllowed)
            {
                layers.Add(CreateLayerContext(service, publication, resource, storageId.Value, snapshot));
            }
        }
        return layers;
    }

    private static async Task<Proto.FeatureCapabilities> ServiceMetadataCapabilitiesAsync(
        IReadOnlyList<GrpcLayerContext> layers, ServerCallContext context)
    {
        var result = new Proto.FeatureCapabilities();
        foreach (var layer in layers)
        {
            var capabilities = await MetadataCapabilitiesAsync(layer, context).ConfigureAwait(false);
            result.Query |= capabilities.Query;
            result.QueryStream |= capabilities.QueryStream;
            result.QueryDistinct |= capabilities.QueryDistinct;
            result.QueryOrderBy |= capabilities.QueryOrderBy;
            result.QueryPagination |= capabilities.QueryPagination;
            result.Create |= capabilities.Create;
            result.Update |= capabilities.Update;
            result.Delete |= capabilities.Delete;
        }
        return result;
    }

    private static string LayerName(GrpcLayerContext layer)
        => layer.Publication.TitleOverride ?? layer.Resource.Metadata.Title ?? layer.Resource.Metadata.Name;

    private static async Task<MetadataV2Field[]> VisibleMetadataFieldsAsync(GrpcLayerContext layer, ServerCallContext context)
    {
        var maskSource = context.GetHttpContext().RequestServices.GetService<IFieldMaskSource>();
        var fields = layer.Resource.SchemaFields.Where(field => !field.Hidden);
        if (maskSource is null)
        {
            return fields.ToArray();
        }
        var masked = (await maskSource.ResolveAsync(layer.Resource, context.CancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return fields.Where(field => !masked.Contains(field.Name)).ToArray();
    }

    private static async Task<Proto.FeatureCapabilities> MetadataCapabilitiesAsync(GrpcLayerContext layer, ServerCallContext context)
    {
        var result = new Proto.FeatureCapabilities
        {
            Query = true,
            QueryStream = true,
            QueryDistinct = true,
            QueryOrderBy = true,
            QueryPagination = true
        };
        var binding = layer.Snapshot?.ResolveStorageBinding(layer.Publication);
        if (binding is null || !FeatureStorageMapping.FromMetadata(layer.Resource, binding).SupportsManagedWrites)
        {
            return result;
        }
        result.Create = await AllowsAsync(AuthorizationOperation.Insert).ConfigureAwait(false);
        result.Update = await AllowsAsync(AuthorizationOperation.Update).ConfigureAwait(false);
        result.Delete = await AllowsAsync(AuthorizationOperation.Delete).ConfigureAwait(false);
        return result;

        async Task<bool> AllowsAsync(AuthorizationOperation operation)
        {
            if (LayerValidationHelpers.FindUndeclaredEditCapability(layer.Service, layer.Publication, operation) is not null)
            {
                return false;
            }
            var decision = await ServiceDataEditorAuthorization.EvaluateResourceDataEditorAsync(context.GetHttpContext(),
                layer.Resource, layer.Service, operation, context.CancellationToken).ConfigureAwait(false);
            return decision.IsAllowed;
        }
    }

    private static int ReadMetadataToken(string token, string serviceId, long revision)
    {
        if (string.IsNullOrEmpty(token))
        {
            return 0;
        }
        if (token.Length > 4096)
        {
            throw MetadataArgument("Invalid page_token.");
        }
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(token)).Split('\n');
            if (parts.Length == 3 && parts[0] == serviceId &&
                long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tokenRevision) && tokenRevision == revision &&
                int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0)
            {
                return offset;
            }
        }
        catch (FormatException)
        {
            // Invalid tokens never bypass the same authorization and snapshot checks as the first page.
        }
        throw MetadataArgument("Invalid or stale page_token.");
    }

    private sealed record MetadataPage(Proto.DescribeServiceRequest Request, long Revision, int Offset);

    private sealed record MetadataVisibility(GrpcLayerContext Layer, ISet<string> Fields);

    private static RpcException MetadataArgument(string message) => new(new Status(StatusCode.InvalidArgument, message));
}
